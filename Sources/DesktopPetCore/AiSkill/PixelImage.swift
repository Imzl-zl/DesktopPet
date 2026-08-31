import Foundation

/// Platform-independent RGBA bitmap.
///
/// `pixels` is row-major, length `width * height * 4`, non-premultiplied
/// (channels are stored as-is; alpha lives in the 4th byte of each pixel).
/// Value type, `Sendable`, no IO — safe to use from the domain layer and to
/// test on any Swift platform (Windows included).
struct PixelImage: Equatable, Sendable {
    let width: Int
    let height: Int
    var pixels: [UInt8]

    /// - Parameter pixels: row-major RGBA, exactly `width * height * 4` bytes.
    init(width: Int, height: Int, pixels: [UInt8]) {
        precondition(width > 0 && height > 0, "PixelImage must be non-empty")
        precondition(pixels.count == width * height * 4,
                     "pixels count \(pixels.count) != \(width) * \(height) * 4")
        self.width = width
        self.height = height
        self.pixels = pixels
    }

    static func transparent(width: Int, height: Int) -> PixelImage {
        PixelImage(width: width, height: height, pixels: [UInt8](repeating: 0, count: width * height * 4))
    }

    @inline(__always)
    func index(x: Int, y: Int) -> Int {
        (y * width + x) * 4
    }

    func alpha(x: Int, y: Int) -> UInt8 {
        pixels[index(x: x, y: y) + 3]
    }

    /// Writes one RGBA pixel (no-op when out of bounds).
    mutating func writePixel(x: Int, y: Int, r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        guard x >= 0, x < width, y >= 0, y < height else { return }
        let i = index(x: x, y: y)
        pixels[i] = r
        pixels[i + 1] = g
        pixels[i + 2] = b
        pixels[i + 3] = a
    }

    func rgba(x: Int, y: Int) -> (r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        let i = index(x: x, y: y)
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3])
    }

    /// Crops a sub-rectangle. Returns nil when the rect is out of bounds.
    func cropping(x: Int, y: Int, width cropW: Int, height cropH: Int) -> PixelImage? {
        guard x >= 0, y >= 0, cropW > 0, cropH > 0,
              x + cropW <= width, y + cropH <= height else { return nil }
        var out = [UInt8](repeating: 0, count: cropW * cropH * 4)
        for row in 0..<cropH {
            let srcStart = ((y + row) * width + x) * 4
            let dstStart = row * cropW * 4
            out.replaceSubrange(dstStart..<(dstStart + cropW * 4),
                                with: pixels[srcStart..<(srcStart + cropW * 4)])
        }
        return PixelImage(width: cropW, height: cropH, pixels: out)
    }

    /// Draws `image` centred onto this bitmap. Pixels outside the destination
    /// bounds are clipped. Opaque source pixels overwrite; transparent source
    /// pixels leave the destination untouched.
    mutating func drawCentered(_ image: PixelImage) {
        drawCentered(image, at: ((width - image.width) / 2, (height - image.height) / 2))
    }

    /// Draws `image` centred at `origin` (the top-left of the drawing area).
    mutating func drawCentered(_ image: PixelImage, at origin: (x: Int, y: Int)) {
        for sy in 0..<image.height {
            let ty = origin.y + sy
            guard ty >= 0, ty < height else { continue }
            for sx in 0..<image.width {
                let tx = origin.x + sx
                guard tx >= 0, tx < width else { continue }
                let src = image.index(x: sx, y: sy)
                guard image.pixels[src + 3] > 0 else { continue }
                let dst = index(x: tx, y: ty)
                pixels[dst] = image.pixels[src]
                pixels[dst + 1] = image.pixels[src + 1]
                pixels[dst + 2] = image.pixels[src + 2]
                pixels[dst + 3] = image.pixels[src + 3]
            }
        }
    }
}
