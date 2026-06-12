import CoreMediaIO
import CoreVideo
import Foundation
import IOKit.audio
import os.log

let nexusCameraFrameRate = 30
let nexusCameraWidth = 1920
let nexusCameraHeight = 1080

let nexusCameraLog = OSLog(subsystem: "com.hellonexus.panel.service.camera-extension", category: "extension")

// One virtual device with two streams: a source stream that camera clients
// (FaceTime, Zoom, ...) read, and a sink stream the Nexus service feeds via the
// CoreMediaIO DAL client API. While the sink is live its frames are forwarded
// to the source; otherwise a generated test pattern keeps the camera alive.
class NexusCameraDeviceSource: NSObject, CMIOExtensionDeviceSource {
    private(set) var device: CMIOExtensionDevice!

    private var streamSource: NexusCameraStreamSource!
    private var streamSink: NexusCameraStreamSink!

    private var streamingCounter: UInt32 = 0
    private var sinkStreamingCounter: UInt32 = 0

    private var patternTimer: DispatchSourceTimer?
    private var consumeTimer: DispatchSourceTimer?

    private let timerQueue = DispatchQueue(
        label: "com.hellonexus.panel.service.camera-extension.timer",
        qos: .userInteractive,
        attributes: [],
        autoreleaseFrequency: .workItem,
        target: .global(qos: .userInteractive)
    )

    private var videoDescription: CMFormatDescription!
    private var bufferPool: CVPixelBufferPool!
    private var bufferAuxAttributes: NSDictionary!

    private var frameCounter: UInt64 = 0

    private(set) var sinkStarted = false

    init(localizedName: String, deviceUUID: UUID, sourceUUID: UUID, sinkUUID: UUID) {
        super.init()

        device = CMIOExtensionDevice(
            localizedName: localizedName,
            deviceID: deviceUUID,
            legacyDeviceID: nil,
            source: self
        )

        let dimensions = CMVideoDimensions(width: Int32(nexusCameraWidth), height: Int32(nexusCameraHeight))
        CMVideoFormatDescriptionCreate(
            allocator: kCFAllocatorDefault,
            codecType: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            width: dimensions.width,
            height: dimensions.height,
            extensions: nil,
            formatDescriptionOut: &videoDescription
        )

        // IOSurface backing is required for zero-copy hand-off to clients.
        let pixelBufferAttributes: NSDictionary = [
            kCVPixelBufferWidthKey: dimensions.width,
            kCVPixelBufferHeightKey: dimensions.height,
            kCVPixelBufferPixelFormatTypeKey: videoDescription.mediaSubType,
            kCVPixelBufferIOSurfacePropertiesKey: [CFString: CFTypeRef](),
        ]
        CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, pixelBufferAttributes, &bufferPool)
        bufferAuxAttributes = [kCVPixelBufferPoolAllocationThresholdKey: 5]

        let videoStreamFormat = CMIOExtensionStreamFormat(
            formatDescription: videoDescription,
            maxFrameDuration: CMTime(value: 1, timescale: Int32(nexusCameraFrameRate)),
            minFrameDuration: CMTime(value: 1, timescale: Int32(nexusCameraFrameRate)),
            validFrameDurations: nil
        )

        streamSource = NexusCameraStreamSource(
            localizedName: "Nexus Camera Source",
            streamID: sourceUUID,
            streamFormat: videoStreamFormat,
            device: device
        )
        streamSink = NexusCameraStreamSink(
            localizedName: "Nexus Camera Sink",
            streamID: sinkUUID,
            streamFormat: videoStreamFormat,
            device: device
        )

        do {
            try device.addStream(streamSource.stream)
            try device.addStream(streamSink.stream)
        } catch {
            fatalError("Failed to add streams: \(error.localizedDescription)")
        }
    }

    var availableProperties: Set<CMIOExtensionProperty> {
        [.deviceTransportType, .deviceModel]
    }

    func deviceProperties(forProperties properties: Set<CMIOExtensionProperty>) throws
        -> CMIOExtensionDeviceProperties
    {
        let deviceProperties = CMIOExtensionDeviceProperties(dictionary: [:])
        if properties.contains(.deviceTransportType) {
            deviceProperties.transportType = kIOAudioDeviceTransportTypeVirtual
        }
        if properties.contains(.deviceModel) {
            deviceProperties.model = "Nexus Camera"
        }
        return deviceProperties
    }

    func setDeviceProperties(_ deviceProperties: CMIOExtensionDeviceProperties) throws {}

    // MARK: - Source stream (test pattern fallback)

    func startStreaming() {
        guard bufferPool != nil else { return }

        streamingCounter += 1
        guard patternTimer == nil else { return }

        let timer = DispatchSource.makeTimerSource(flags: .strict, queue: timerQueue)
        timer.schedule(
            deadline: .now(),
            repeating: 1.0 / Double(nexusCameraFrameRate),
            leeway: .seconds(0)
        )
        timer.setEventHandler { [weak self] in
            guard let self, !self.sinkStarted else { return }
            self.sendTestPatternFrame()
        }
        patternTimer = timer
        timer.resume()
    }

    func stopStreaming() {
        if streamingCounter > 1 {
            streamingCounter -= 1
        } else {
            streamingCounter = 0
            patternTimer?.cancel()
            patternTimer = nil
        }
    }

    private func sendTestPatternFrame() {
        var pixelBuffer: CVPixelBuffer?
        let poolStatus = CVPixelBufferPoolCreatePixelBufferWithAuxAttributes(
            kCFAllocatorDefault, bufferPool, bufferAuxAttributes, &pixelBuffer
        )
        if poolStatus == kCVReturnPoolAllocationFailed {
            os_log(.error, log: nexusCameraLog, "pixel buffer pool exhausted")
            return
        }
        guard let pixelBuffer else { return }

        frameCounter += 1
        NexusTestPattern.render(into: pixelBuffer, frameIndex: frameCounter)

        var sampleBuffer: CMSampleBuffer!
        var timingInfo = CMSampleTimingInfo()
        timingInfo.presentationTimeStamp = CMClockGetTime(CMClockGetHostTimeClock())

        let status = CMSampleBufferCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: videoDescription,
            sampleTiming: &timingInfo,
            sampleBufferOut: &sampleBuffer
        )
        if status == noErr {
            streamSource.stream.send(
                sampleBuffer,
                discontinuity: [],
                hostTimeInNanoseconds: UInt64(timingInfo.presentationTimeStamp.seconds * Double(NSEC_PER_SEC))
            )
        }
    }

    // MARK: - Sink stream (frames pushed by the Nexus service)

    // Pull loop instead of a notification: CMIOExtensionStream offers no
    // callback for sink arrival, so OBS (and we) poll consumeSampleBuffer
    // faster than the nominal frame rate.
    func startStreamingSink(client: CMIOExtensionClient) {
        sinkStreamingCounter += 1
        sinkStarted = true

        guard consumeTimer == nil else { return }
        let timer = DispatchSource.makeTimerSource(flags: .strict, queue: timerQueue)
        timer.schedule(
            deadline: .now(),
            repeating: 1.0 / (Double(nexusCameraFrameRate) * 3.0),
            leeway: .seconds(0)
        )
        timer.setEventHandler { [weak self] in
            self?.consumeBuffer(client)
        }
        consumeTimer = timer
        timer.resume()
    }

    func stopStreamingSink() {
        if sinkStreamingCounter > 1 {
            sinkStreamingCounter -= 1
        } else {
            sinkStreamingCounter = 0
            sinkStarted = false
            consumeTimer?.cancel()
            consumeTimer = nil
        }
    }

    private func consumeBuffer(_ client: CMIOExtensionClient) {
        guard sinkStarted else { return }

        streamSink.stream.consumeSampleBuffer(from: client) {
            [weak self] sampleBuffer, sequenceNumber, discontinuity, hasMoreSampleBuffers, error in
            guard let self, let sampleBuffer else { return }

            let nowNanos = UInt64(CMClockGetTime(CMClockGetHostTimeClock()).seconds * Double(NSEC_PER_SEC))

            if self.streamingCounter > 0 {
                self.streamSource.stream.send(
                    sampleBuffer,
                    discontinuity: [],
                    hostTimeInNanoseconds: UInt64(sampleBuffer.presentationTimeStamp.seconds * Double(NSEC_PER_SEC))
                )
            }

            // Tells the feeding client its buffer was consumed (drives the
            // queueAlteredProc registered via CMIOStreamCopyBufferQueue).
            let output = CMIOExtensionScheduledOutput(
                sequenceNumber: sequenceNumber,
                hostTimeInNanoseconds: nowNanos
            )
            self.streamSink.stream.notifyScheduledOutputChanged(output)
        }
    }
}
