import Foundation

/// Errors produced by the deterministic sprite tools.
enum SpriteToolsError: Error, Equatable, Sendable {
    /// `sliceStrip` detected `detected` alpha bands but the caller declared `expected`.
    case frameCountMismatch(detected: Int, expected: Int)
}

/// Deterministic sprite-sheet image tools (pure Swift, platform-independent).
///
/// These operate on `PixelImage` and mirror the consumer's gutter-detection
/// semantics (`SpriteSlicer` on macOS): frames are separated by fully
/// transparent vertical gutters. Composed sheets keep a transparent gutter so
/// the consumer can slice the same rows back.
enum SpriteTools {
    /// Splits a single-row strip into `frameCount` frames by detecting fully
    /// transparent vertical gutters. Throws if the detected band count does not
    /// match `frameCount` — never silently falls back (a silent fallback would
    /// diverge from what the consumer slices back).
    static func sliceStrip(_ image: PixelImage, frameCount: Int, alphaThreshold: UInt8 = 16) throws -> [PixelImage] {
        let bands = columnBands(in: image, yStart: 0, height: image.height, alphaThreshold: alphaThreshold)
        if bands.isEmpty { return [] }
        guard bands.count == frameCount else {
            throw SpriteToolsError.frameCountMismatch(detected: bands.count, expected: frameCount)
        }
        return bands.map { band in
            // Crop the full height of the strip at this band's x-range.
            image.cropping(x: band.lower, y: 0,
                           width: band.upper - band.lower, height: image.height)!
        }
    }

    /// Composes a spritesheet: one row per animation action, each frame centred
    /// in its `cell` slot, with a transparent `gutter` between cells (and
    /// between rows) so the consumer's gutter detection slices it back.
    static func composeSheet(rows: [[PixelImage]], cell: CellSize, gutter: Int = 1) -> PixelImage {
        let maxCols = rows.map(\.count).max() ?? 0
        let width = maxCols * cell.width + max(0, maxCols - 1) * gutter
        let height = rows.count * cell.height + max(0, rows.count - 1) * gutter
        var sheet = PixelImage.transparent(width: width, height: height)

        for (row, frames) in rows.enumerated() {
            let y = row * (cell.height + gutter)
            for (col, frame) in frames.enumerated() {
                let x = col * (cell.width + gutter)
                let ox = x + (cell.width - frame.width) / 2
                let oy = y + (cell.height - frame.height) / 2
                sheet.drawCentered(frame, at: (ox, oy))
            }
        }
        return sheet
    }

    /// Validates that a sheet contains exactly one row per action and each row
    /// has the declared frame count, with cells aligned to `cell` and no
    /// opaque pixels in the inter-cell/inter-row gutter.
    static func validateSheet(_ image: PixelImage, actions: [ActionSpec], cell: CellSize, gutter: Int = 1) -> SheetReport {
        var issues: [String] = []

        let expectedRows = actions.count
        let maxFrames = actions.map(\.frameCount).max() ?? 0
        let expectedWidth = maxFrames * cell.width + max(0, maxFrames - 1) * gutter
        let expectedHeight = expectedRows * cell.height + max(0, expectedRows - 1) * gutter

        if image.width != expectedWidth {
            issues.append("sheet width \(image.width) != expected \(expectedWidth)")
        }
        if image.height != expectedHeight {
            issues.append("sheet height \(image.height) != expected \(expectedHeight)")
        }

        // One row band per action, with the declared frame count each.
        let stride = cell.height + gutter
        for (i, action) in actions.enumerated() {
            let y = i * stride
            let bands = columnBands(in: image, yStart: y, height: cell.height)
            if bands.count != action.frameCount {
                issues.append("row \(i) (\(action.id)): detected \(bands.count) frames, expected \(action.frameCount)")
            }
        }

        // Inter-row gutter rows must stay fully transparent (no detached pixels).
        if expectedRows > 1 {
            for i in 0..<(expectedRows - 1) {
                let gutterY = (i + 1) * cell.height + i * gutter
                if rowHasAlpha(image, yStart: gutterY, height: gutter) {
                    issues.append("gutter above row \(i + 1) contains opaque pixels")
                }
            }
        }

        return SheetReport(ok: issues.isEmpty, issues: issues)
    }

    // MARK: - Scanning helpers

    /// Returns contiguous x-bands (lower..<upper) whose columns contain at least
    /// one pixel above `alphaThreshold` within the given y-range.
    private static func columnBands(in image: PixelImage, yStart: Int, height: Int, alphaThreshold: UInt8 = 16) -> [(lower: Int, upper: Int)] {
        var colHas = [Bool](repeating: false, count: image.width)
        for x in 0..<image.width {
            for y in yStart..<(yStart + height) {
                guard y >= 0, y < image.height else { continue }
                if image.alpha(x: x, y: y) > alphaThreshold {
                    colHas[x] = true
                    break
                }
            }
        }
        return segments(colHas)
    }

    /// Returns true when any pixel in the y-range has alpha above threshold.
    private static func rowHasAlpha(_ image: PixelImage, yStart: Int, height: Int, alphaThreshold: UInt8 = 16) -> Bool {
        for y in yStart..<(yStart + height) {
            guard y >= 0, y < image.height else { continue }
            for x in 0..<image.width where image.alpha(x: x, y: y) > alphaThreshold {
                return true
            }
        }
        return false
    }

    /// Converts a boolean occupancy array into contiguous bands.
    private static func segments(_ occupancy: [Bool]) -> [(lower: Int, upper: Int)] {
        var result: [(Int, Int)] = []
        var start: Int?
        for (i, filled) in occupancy.enumerated() {
            if filled {
                if start == nil { start = i }
            } else if let s = start {
                result.append((s, i))
                start = nil
            }
        }
        if let s = start { result.append((s, occupancy.count)) }
        return result
    }
}
