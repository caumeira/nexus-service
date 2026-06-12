import CoreVideo
import Foundation

// Renders a scrolling gradient plus a frame counter straight into the NV12
// planes. Pure CPU writes, no AppKit, so it stays cheap inside the sandboxed
// extension and proves the pixel path end to end.
enum NexusTestPattern {

    private static let lumaBlack: UInt8 = 16
    private static let lumaWhite: UInt8 = 235
    private static let lumaSpan = 220
    private static let chromaNeutral: UInt8 = 128

    // Per-digit rows, top to bottom; bits are columns, MSB leftmost.
    private static let glyphColumns = 3
    private static let glyphRows = 5
    private static let digitGlyphs: [[UInt8]] = [
        [0b111, 0b101, 0b101, 0b101, 0b111],
        [0b010, 0b110, 0b010, 0b010, 0b111],
        [0b111, 0b001, 0b111, 0b100, 0b111],
        [0b111, 0b001, 0b111, 0b001, 0b111],
        [0b101, 0b101, 0b111, 0b001, 0b001],
        [0b111, 0b100, 0b111, 0b001, 0b111],
        [0b111, 0b100, 0b111, 0b101, 0b111],
        [0b111, 0b001, 0b001, 0b001, 0b001],
        [0b111, 0b101, 0b111, 0b101, 0b111],
        [0b111, 0b101, 0b111, 0b001, 0b111],
    ]

    static func render(into pixelBuffer: CVPixelBuffer, frameIndex: UInt64) {
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

        guard let lumaBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0),
              let chromaBase = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 1)
        else { return }

        let width = CVPixelBufferGetWidthOfPlane(pixelBuffer, 0)
        let height = CVPixelBufferGetHeightOfPlane(pixelBuffer, 0)
        let lumaStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)
        let chromaStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 1)
        let luma = lumaBase.assumingMemoryBound(to: UInt8.self)
        let chroma = chromaBase.assumingMemoryBound(to: UInt8.self)

        let phase = Int(frameIndex % UInt64(max(width, 1)))

        // Diagonal luma gradient scrolling with the frame index, kept inside
        // video range.
        for y in 0..<height {
            let row = luma + y * lumaStride
            for x in 0..<width {
                row[x] = lumaBlack + UInt8((x + y / 2 + phase * 4) % lumaSpan)
            }
        }

        // Chroma plane is interleaved CbCr at half resolution.
        let chromaWidth = width / 2
        let chromaHeight = height / 2
        for y in 0..<chromaHeight {
            let row = chroma + y * chromaStride
            for x in 0..<chromaWidth {
                row[2 * x] = lumaBlack + UInt8((x + phase) % lumaSpan)
                row[2 * x + 1] = lumaBlack + UInt8((y + phase) % lumaSpan)
            }
        }

        drawCounter(
            String(frameIndex),
            luma: luma, lumaStride: lumaStride,
            chroma: chroma, chromaStride: chromaStride,
            width: width, height: height
        )
    }

    // White-on-black blocky digits in the top-left corner; chroma is
    // neutralized underneath so the counter stays legible over the gradient.
    private static func drawCounter(
        _ text: String,
        luma: UnsafeMutablePointer<UInt8>, lumaStride: Int,
        chroma: UnsafeMutablePointer<UInt8>, chromaStride: Int,
        width: Int, height: Int
    ) {
        let scale = 16
        let pad = scale
        let glyphWidth = glyphColumns * scale
        let glyphHeight = glyphRows * scale

        let digits = text.compactMap { $0.wholeNumberValue }
        let boxWidth = min(width, digits.count * (glyphWidth + pad) + pad)
        let boxHeight = min(height, glyphHeight + 2 * pad)

        for y in 0..<boxHeight {
            let row = luma + y * lumaStride
            for x in 0..<boxWidth {
                row[x] = lumaBlack
            }
        }
        for y in 0..<(boxHeight / 2) {
            let row = chroma + y * chromaStride
            for x in 0..<(boxWidth / 2) {
                row[2 * x] = chromaNeutral
                row[2 * x + 1] = chromaNeutral
            }
        }

        var originX = pad
        for digit in digits {
            guard digit >= 0, digit < digitGlyphs.count else { continue }
            let glyph = digitGlyphs[digit]
            for row in 0..<glyphRows {
                let bits = glyph[row]
                for col in 0..<glyphColumns where bits & (1 << (glyphColumns - 1 - col)) != 0 {
                    fillBlock(
                        luma: luma, lumaStride: lumaStride,
                        x: originX + col * scale, y: pad + row * scale,
                        size: scale, width: width, height: height
                    )
                }
            }
            originX += glyphWidth + pad
            if originX + glyphWidth > width { break }
        }
    }

    private static func fillBlock(
        luma: UnsafeMutablePointer<UInt8>, lumaStride: Int,
        x: Int, y: Int, size: Int, width: Int, height: Int
    ) {
        let maxY = min(y + size, height)
        let maxX = min(x + size, width)
        for py in y..<maxY {
            let row = luma + py * lumaStride
            for px in x..<maxX {
                row[px] = lumaWhite
            }
        }
    }
}
