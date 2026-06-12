// nexus-camera-helper
//
// Bridges the Nexus service to the Nexus Camera CMIO system extension. The
// service pipes encoded phone frames (H.264 Annex-B or MJPEG) to stdin; each
// frame is decoded with VideoToolbox into an IOSurface-backed NV12 pixel
// buffer and enqueued on the extension's sink stream through the CoreMediaIO
// DAL client API. The extension forwards sink buffers to its source stream,
// which camera apps capture.
//
// Why a helper: VideoToolbox + CoreMediaIO carry CF bridging and callback
// semantics that are hostile to .NET Native AOT P/Invoke; the sidecar keeps
// that surface in Swift (same reasoning as nexus-audio-helper).
//
// Contract with the service (full spec in NOTES-HELPER.md):
// - stdin: framed records, fixed little-endian header followed by payload.
// - stdout: a single READY line once the sink stream is open and started.
// - stderr: diagnostics; FRAME-DROP and decode errors are rate-limited.
// - EOF on stdin or SIGTERM stops the sink stream and exits cleanly.

import CoreMedia
import CoreMediaIO
import CoreVideo
import Foundation
import VideoToolbox

// MARK: - Constants

// Wire framing: the WS stream header from WebcamSession.cs plus an explicit
// payload length so the pipe can be framed without message boundaries.
let wireVersion: UInt8 = 1
let headerLength = 14
let codecShift: UInt8 = 1
let codecMask: UInt8 = 0b11
let codecH264: UInt8 = 0
let codecMjpeg: UInt8 = 1
// Desync guard; any sane encoded frame is far below this.
let maxPayloadBytes: UInt32 = 16 * 1024 * 1024

// Must match NexusCameraDeviceUUID in the extension's Info.plist.
let nexusCameraDeviceUID = "ECDD7A18-3AAE-4968-869D-D345C62F2EA5"
// kCMIOStreamPropertyDirection value of a stream the client feeds (sink).
let sinkStreamDirection: UInt32 = 1
// Matches the frame duration the extension advertises on both streams.
let nominalFrameRate: Int32 = 30

let dropLogStride: UInt64 = 300
let decodeErrorLogStride: UInt64 = 300

enum ExitCode: Int32 {
    case ok = 0
    case deviceNotFound = 2
    case sinkNotFound = 3
    case sinkOpenFailed = 4
    case protocolError = 5
    case decoderError = 6
}

// MARK: - Logging

let stderrHandle = FileHandle.standardError

func logErr(_ message: String) {
    stderrHandle.write(Data(("[camera-helper] " + message + "\n").utf8))
}

func fail(_ code: ExitCode, _ message: String) -> Never {
    logErr(message)
    exit(code.rawValue)
}

// MARK: - CMIO discovery

func globalAddress(_ selector: Int) -> CMIOObjectPropertyAddress {
    CMIOObjectPropertyAddress(
        mSelector: CMIOObjectPropertySelector(selector),
        mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
        mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain)
    )
}

func cmioObjectIDs(_ object: CMIOObjectID, _ selector: Int) -> [CMIOObjectID] {
    var address = globalAddress(selector)
    var byteSize: UInt32 = 0
    guard CMIOObjectGetPropertyDataSize(object, &address, 0, nil, &byteSize) == 0, byteSize > 0 else {
        return []
    }
    let count = Int(byteSize) / MemoryLayout<CMIOObjectID>.stride
    var ids = [CMIOObjectID](repeating: 0, count: count)
    var used: UInt32 = 0
    let status = ids.withUnsafeMutableBytes {
        CMIOObjectGetPropertyData(object, &address, 0, nil, byteSize, &used, $0.baseAddress!)
    }
    guard status == 0 else { return [] }
    return Array(ids.prefix(Int(used) / MemoryLayout<CMIOObjectID>.stride))
}

func cmioDeviceUID(_ device: CMIOObjectID) -> String? {
    var address = globalAddress(kCMIODevicePropertyDeviceUID)
    // The property hands back a retained CFString; storing it in a managed
    // var lets ARC balance that retain when the var goes out of scope.
    var uid: CFString?
    var used: UInt32 = 0
    let status = withUnsafeMutablePointer(to: &uid) {
        CMIOObjectGetPropertyData(
            device, &address, 0, nil,
            UInt32(MemoryLayout<CFString?>.stride), &used, UnsafeMutableRawPointer($0)
        )
    }
    guard status == 0, let uid else { return nil }
    return uid as String
}

func cmioStreamName(_ stream: CMIOStreamID) -> String? {
    var address = globalAddress(kCMIOObjectPropertyName)
    var name: CFString?
    var used: UInt32 = 0
    let status = withUnsafeMutablePointer(to: &name) {
        CMIOObjectGetPropertyData(
            stream, &address, 0, nil,
            UInt32(MemoryLayout<CFString?>.stride), &used, UnsafeMutableRawPointer($0)
        )
    }
    guard status == 0, let name else { return nil }
    return name as String
}

func cmioStreamDirection(_ stream: CMIOStreamID) -> UInt32? {
    var address = globalAddress(kCMIOStreamPropertyDirection)
    var direction: UInt32 = 0
    var used: UInt32 = 0
    let status = withUnsafeMutablePointer(to: &direction) {
        CMIOObjectGetPropertyData(
            stream, &address, 0, nil,
            UInt32(MemoryLayout<UInt32>.stride), &used, UnsafeMutableRawPointer($0)
        )
    }
    return status == 0 ? direction : nil
}

func findNexusCameraDevice() -> CMIOObjectID? {
    for device in cmioObjectIDs(CMIOObjectID(kCMIOObjectSystemObject), kCMIOHardwarePropertyDevices) {
        if let uid = cmioDeviceUID(device),
           uid.caseInsensitiveCompare(nexusCameraDeviceUID) == .orderedSame
        {
            return device
        }
    }
    return nil
}

func findSinkStream(on device: CMIOObjectID) -> CMIOStreamID? {
    let streams = cmioObjectIDs(device, kCMIODevicePropertyStreams)
    // Select by name: this extension's streams report the input-direction
    // value on the source, so kCMIOStreamPropertyDirection is not a reliable
    // discriminator. Source is added first, sink second (index 1 fallback).
    if let byName = streams.first(where: {
        cmioStreamName($0)?.localizedCaseInsensitiveContains("sink") == true
    }) {
        return byName
    }
    return streams.count > 1 ? streams[1] : streams.first
}

// MARK: - Sink feeder

// Wraps decoded pixel buffers in CMSampleBuffers and pushes them onto the
// extension's sink buffer queue. Ownership of a sample buffer transfers to
// the queue on successful enqueue; release only on failure.
final class SinkFeeder {
    private let deviceID: CMIOObjectID
    private let streamID: CMIOStreamID
    private let queue: CMSimpleQueue
    private var cachedFormat: CMVideoFormatDescription?
    private var dropTally: UInt64 = 0

    init(deviceID: CMIOObjectID, streamID: CMIOStreamID, queue: CMSimpleQueue) {
        self.deviceID = deviceID
        self.streamID = streamID
        self.queue = queue
    }

    func enqueue(_ pixelBuffer: CVPixelBuffer) {
        if CMSimpleQueueGetCount(queue) >= CMSimpleQueueGetCapacity(queue) {
            noteDrop()
            return
        }

        let format: CMVideoFormatDescription
        if let cached = cachedFormat, CMVideoFormatDescriptionMatchesImageBuffer(cached, imageBuffer: pixelBuffer) {
            format = cached
        } else {
            var fresh: CMVideoFormatDescription?
            guard CMVideoFormatDescriptionCreateForImageBuffer(
                allocator: kCFAllocatorDefault,
                imageBuffer: pixelBuffer,
                formatDescriptionOut: &fresh
            ) == noErr, let fresh else { return }
            cachedFormat = fresh
            format = fresh
        }

        // The extension's streams run on the host time clock and derive their
        // scheduled-output host time from this PTS, so it must be host time.
        var timing = CMSampleTimingInfo(
            duration: CMTime(value: 1, timescale: nominalFrameRate),
            presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()),
            decodeTimeStamp: .invalid
        )
        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: format,
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer
        ) == noErr, let sampleBuffer else { return }

        let element = Unmanaged.passRetained(sampleBuffer).toOpaque()
        let status = CMSimpleQueueEnqueue(queue, element: element)
        if status != noErr {
            Unmanaged<CMSampleBuffer>.fromOpaque(element).release()
            noteDrop()
        }
    }

    func stop() {
        CMIODeviceStopStream(deviceID, streamID)
    }

    private func noteDrop() {
        dropTally += 1
        if dropTally == 1 || dropTally % dropLogStride == 0 {
            logErr("FRAME-DROP sink queue full (total \(dropTally))")
        }
    }
}

// MARK: - Decoder

// Decodes H.264 Annex-B or MJPEG payloads to NV12 pixel buffers. One session
// at a time; rebuilt when parameter sets, dimensions, or codec change, or
// when VideoToolbox invalidates the session (media services reset).
final class VideoDecoder {
    private var session: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    private var sps: [UInt8] = []
    private var pps: [UInt8] = []
    private var jpegWidth: Int32 = 0
    private var jpegHeight: Int32 = 0
    private var decodeErrorTally: UInt64 = 0
    private let emit: (CVPixelBuffer) -> Void

    init(emit: @escaping (CVPixelBuffer) -> Void) {
        self.emit = emit
    }

    func decodeH264(_ payload: Data, timestampMs: UInt32) {
        // Strip parameter sets out of the access unit and convert the VCL/SEI
        // NALs to length-prefixed (AVCC) form for VideoToolbox.
        var sampleData = Data()
        var parametersChanged = false
        for nal in annexBNALUnits(payload) {
            guard let first = nal.first else { continue }
            switch first & 0x1f {
            case 7:
                let bytes = Array(nal)
                if bytes != sps { sps = bytes; parametersChanged = true }
            case 8:
                let bytes = Array(nal)
                if bytes != pps { pps = bytes; parametersChanged = true }
            case 9:
                break
            default:
                let lengthBE = UInt32(nal.count).bigEndian
                withUnsafeBytes(of: lengthBE) { sampleData.append(contentsOf: $0) }
                nal.withUnsafeBufferPointer { sampleData.append($0.baseAddress!, count: $0.count) }
            }
        }

        if parametersChanged || session == nil, !sps.isEmpty, !pps.isEmpty {
            rebuildH264Session()
        }
        guard !sampleData.isEmpty else { return }
        // Nothing to decode against until the first keyframe delivers SPS/PPS.
        decode(sampleData, timestampMs: timestampMs)
    }

    func decodeMjpeg(_ payload: Data, width: UInt16, height: UInt16, timestampMs: UInt32) {
        if session == nil || formatDescription == nil
            || Int32(width) != jpegWidth || Int32(height) != jpegHeight
        {
            var description: CMVideoFormatDescription?
            let status = CMVideoFormatDescriptionCreate(
                allocator: kCFAllocatorDefault,
                codecType: kCMVideoCodecType_JPEG,
                width: Int32(width),
                height: Int32(height),
                extensions: nil,
                formatDescriptionOut: &description
            )
            guard status == noErr, let description else {
                noteDecodeError(status)
                return
            }
            jpegWidth = Int32(width)
            jpegHeight = Int32(height)
            adoptFormat(description)
        }
        decode(payload, timestampMs: timestampMs)
    }

    private func rebuildH264Session() {
        var description: CMVideoFormatDescription?
        let status: OSStatus = sps.withUnsafeBufferPointer { spsBuf in
            pps.withUnsafeBufferPointer { ppsBuf in
                let pointers: [UnsafePointer<UInt8>] = [spsBuf.baseAddress!, ppsBuf.baseAddress!]
                let sizes: [Int] = [spsBuf.count, ppsBuf.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: pointers.count,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &description
                )
            }
        }
        guard status == noErr, let description else {
            logErr("H264 parameter sets rejected (status \(status))")
            return
        }
        adoptFormat(description)
    }

    private func adoptFormat(_ description: CMVideoFormatDescription) {
        formatDescription = description
        if let session, VTDecompressionSessionCanAcceptFormatDescription(session, formatDescription: description) {
            return
        }
        if let session { VTDecompressionSessionInvalidate(session) }
        session = nil

        // NV12 video-range, IOSurface-backed: the extension's only advertised
        // format, and IOSurface keeps the cross-process hand-off zero-copy.
        let attributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferIOSurfacePropertiesKey: [CFString: Any](),
        ]
        var fresh: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: description,
            decoderSpecification: nil,
            imageBufferAttributes: attributes as CFDictionary,
            outputCallback: nil,
            decompressionSessionOut: &fresh
        )
        guard status == noErr, let fresh else {
            fail(.decoderError, "VTDecompressionSessionCreate failed (status \(status))")
        }
        VTSessionSetProperty(fresh, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        session = fresh
    }

    private func decode(_ data: Data, timestampMs: UInt32) {
        guard let session, let formatDescription else { return }

        var blockBuffer: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: data.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: data.count,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuffer
        )
        guard status == kCMBlockBufferNoErr, let blockBuffer else {
            noteDecodeError(status)
            return
        }
        status = data.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(
                with: $0.baseAddress!, blockBuffer: blockBuffer,
                offsetIntoDestination: 0, dataLength: data.count
            )
        }
        guard status == kCMBlockBufferNoErr else {
            noteDecodeError(status)
            return
        }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: CMTimeValue(timestampMs), timescale: 1000),
            decodeTimeStamp: .invalid
        )
        var sampleSize = data.count
        var sampleBuffer: CMSampleBuffer?
        status = CMSampleBufferCreate(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr, let sampleBuffer else {
            noteDecodeError(status)
            return
        }

        // Synchronous decode: the output handler runs before this returns,
        // so stdin backpressure naturally paces the whole pipeline.
        let decodeStatus = VTDecompressionSessionDecodeFrame(
            session, sampleBuffer: sampleBuffer, flags: [], infoFlagsOut: nil
        ) { [weak self] status, _, imageBuffer, _, _ in
            guard let self else { return }
            if status == noErr, let imageBuffer {
                self.emit(imageBuffer)
            } else {
                self.noteDecodeError(status)
            }
        }
        if decodeStatus == kVTInvalidSessionErr {
            // Media services were reset; rebuild from the next parameter sets
            // (H.264) or the next frame (MJPEG).
            VTDecompressionSessionInvalidate(session)
            self.session = nil
            jpegWidth = 0
            jpegHeight = 0
            noteDecodeError(decodeStatus)
        } else if decodeStatus != noErr {
            noteDecodeError(decodeStatus)
        }
    }

    // Splits an Annex-B elementary stream into NAL units, tolerating both
    // short and long start codes.
    private func annexBNALUnits(_ payload: Data) -> [ArraySlice<UInt8>] {
        let bytes = [UInt8](payload)
        var units: [ArraySlice<UInt8>] = []
        let count = bytes.count
        var i = 0
        var start = -1
        while i + 2 < count {
            if bytes[i] == 0, bytes[i + 1] == 0, bytes[i + 2] == 1 {
                if start >= 0 {
                    var end = i
                    // Trim the extra zero of a long start code.
                    if end > start, bytes[end - 1] == 0 { end -= 1 }
                    if end > start { units.append(bytes[start..<end]) }
                }
                i += 3
                start = i
            } else {
                i += 1
            }
        }
        if start >= 0, start < count {
            units.append(bytes[start..<count])
        }
        return units
    }

    private func noteDecodeError(_ status: OSStatus) {
        decodeErrorTally += 1
        if decodeErrorTally == 1 || decodeErrorTally % decodeErrorLogStride == 0 {
            logErr("decode error (status \(status), total \(decodeErrorTally))")
        }
    }
}

// MARK: - stdin framing

func readFully(_ fd: Int32, _ buffer: UnsafeMutableRawPointer, _ count: Int) -> Int {
    var total = 0
    while total < count {
        let n = read(fd, buffer.advanced(by: total), count - total)
        if n == 0 { return total }
        if n < 0 {
            if errno == EINTR { continue }
            return -1
        }
        total += n
    }
    return total
}

func runReadLoop(decoder: VideoDecoder) {
    let stdinFD: Int32 = 0
    var header = [UInt8](repeating: 0, count: headerLength)

    while true {
        let got = header.withUnsafeMutableBytes { readFully(stdinFD, $0.baseAddress!, headerLength) }
        if got == 0 { return } // clean EOF at a record boundary
        if got < headerLength { fail(.protocolError, "truncated frame header") }

        guard header[0] == wireVersion else {
            fail(.protocolError, "unknown protocol version \(header[0])")
        }
        let flags = header[1]
        let codec = (flags >> codecShift) & codecMask
        let width = UInt16(header[2]) | (UInt16(header[3]) << 8)
        let height = UInt16(header[4]) | (UInt16(header[5]) << 8)
        let timestampMs = UInt32(header[6]) | (UInt32(header[7]) << 8)
            | (UInt32(header[8]) << 16) | (UInt32(header[9]) << 24)
        let payloadLength = UInt32(header[10]) | (UInt32(header[11]) << 8)
            | (UInt32(header[12]) << 16) | (UInt32(header[13]) << 24)

        guard payloadLength <= maxPayloadBytes else {
            fail(.protocolError, "payload length \(payloadLength) exceeds cap")
        }
        guard codec == codecH264 || codec == codecMjpeg else {
            fail(.protocolError, "unknown codec bits \(codec)")
        }
        if payloadLength == 0 { continue }

        var payload = Data(count: Int(payloadLength))
        let read = payload.withUnsafeMutableBytes { readFully(stdinFD, $0.baseAddress!, $0.count) }
        guard read == Int(payloadLength) else {
            fail(.protocolError, "truncated frame payload")
        }

        if codec == codecH264 {
            decoder.decodeH264(payload, timestampMs: timestampMs)
        } else {
            decoder.decodeMjpeg(payload, width: width, height: height, timestampMs: timestampMs)
        }
    }
}

// MARK: - Main

guard let deviceID = findNexusCameraDevice() else {
    fail(.deviceNotFound, "Nexus Camera device not found (system extension not installed or not approved)")
}
guard let sinkID = findSinkStream(on: deviceID) else {
    fail(.sinkNotFound, "Nexus Camera sink stream not found on device")
}

var queueRef: Unmanaged<CMSimpleQueue>?
// A non-nil altered proc is required: with nil the call succeeds but hands
// back a null queue on current macOS.
let queueAlteredProc: CMIODeviceStreamQueueAlteredProc = { _, _, _ in }
let copyStatus = CMIOStreamCopyBufferQueue(sinkID, queueAlteredProc, nil, &queueRef)
guard copyStatus == 0, let queueRef else {
    fail(.sinkOpenFailed, "CMIOStreamCopyBufferQueue failed (status \(copyStatus))")
}
let sinkQueue = queueRef.takeRetainedValue()

let startStatus = CMIODeviceStartStream(deviceID, sinkID)
guard startStatus == 0 else {
    fail(.sinkOpenFailed, "CMIODeviceStartStream failed (status \(startStatus))")
}

let feeder = SinkFeeder(deviceID: deviceID, streamID: sinkID, queue: sinkQueue)
let decoder = VideoDecoder { feeder.enqueue($0) }

// The parent may close stdout; frame flow must not die on a write signal.
signal(SIGPIPE, SIG_IGN)
signal(SIGTERM, SIG_IGN)
signal(SIGINT, SIG_IGN)
let signalQueue = DispatchQueue(label: "com.hellonexus.panel.service.camera-helper.signals")
let termSource = DispatchSource.makeSignalSource(signal: SIGTERM, queue: signalQueue)
termSource.setEventHandler {
    feeder.stop()
    exit(ExitCode.ok.rawValue)
}
termSource.resume()
let intSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: signalQueue)
intSource.setEventHandler {
    feeder.stop()
    exit(ExitCode.ok.rawValue)
}
intSource.resume()

// The service blocks on this line before piping frames.
FileHandle.standardOutput.write(Data("READY\n".utf8))

runReadLoop(decoder: decoder)
feeder.stop()
exit(ExitCode.ok.rawValue)
