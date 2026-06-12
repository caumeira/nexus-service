import CoreVideo
import Foundation

// Fills the NV12 planes with solid video-range black for the idle state
// (no phone frames). Pure CPU writes, no AppKit, cheap inside the sandbox.
enum NexusTestPattern {

    private static let lumaBlack: UInt8 = 16
    private static let chromaNeutral: UInt8 = 128


    // Idle frame: solid video-range black. A live consumer (Zoom, FaceTime)
    // must never see a synthetic pattern when no phone frames are arriving.
    static func render(into pixelBuffer: CVPixelBuffer, frameIndex: UInt64) {
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

        guard let lumaBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0),
              let chromaBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 1)
        else { return }

        let height = CVPixelBufferGetHeightOfPlane(pixelBuffer, 0)
        let lumaStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)
        let chromaStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 1)
        let luma = lumaBase.assumingMemoryBound(to: UInt8.self)
        let chroma = chromaBase.assumingMemoryBound(to: UInt8.self)

        for y in 0..<height {
            memset(luma + y * lumaStride, Int32(lumaBlack), lumaStride)
        }
        let chromaHeight = height / 2
        for y in 0..<chromaHeight {
            memset(chroma + y * chromaStride, Int32(chromaNeutral), chromaStride)
        }
    }
}
