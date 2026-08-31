import XCTest
@testable import DesktopPetCore

// MARK: - Helpers

/// Synthesises a single-row strip of `frameCount` solid-colour cells.
private func makeStrip(frameCount: Int, cellW: Int, cellH: Int, gutter: Int, pad: Int = 0) -> PixelImage {
    let w = pad * 2 + frameCount * cellW + (frameCount - 1) * gutter
    let h = pad * 2 + cellH
    var img = PixelImage.transparent(width: w, height: h)
    for i in 0..<frameCount {
        let x = pad + i * (cellW + gutter)
        for yy in 0..<cellH {
            for xx in 0..<cellW {
                setPixel(&img, x: x + xx, y: pad + yy,
                         r: UInt8(40 + i * 40), g: 80, b: 200, a: 255)
            }
        }
    }
    return img
}

/// Synthesises a full sheet with one row per element of `rowFrameCounts`.
private func makeRaggedSheet(rowFrameCounts: [Int], cellW: Int, cellH: Int, gutter: Int) -> PixelImage {
    let maxCols = rowFrameCounts.max() ?? 0
    let w = maxCols * cellW + (maxCols - 1) * gutter
    let h = rowFrameCounts.count * cellH + (rowFrameCounts.count - 1) * gutter
    var img = PixelImage.transparent(width: w, height: h)
    for (row, count) in rowFrameCounts.enumerated() {
        let y = row * (cellH + gutter)
        for col in 0..<count {
            let x = col * (cellW + gutter)
            for yy in 0..<cellH {
                for xx in 0..<cellW {
                    setPixel(&img, x: x + xx, y: y + yy, r: 60, g: UInt8(100 + col * 40), b: 150, a: 255)
                }
            }
        }
    }
    return img
}

private func setPixel(_ img: inout PixelImage, x: Int, y: Int, r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
    // Small helper using an inout var + mutating drawCentered would be complex;
    // rebuild via a mutating writer instead.
    var copy = img
    copy.writePixel(x: x, y: y, r: r, g: g, b: b, a: a)
    img = copy
}

// MARK: - Tests

final class SpriteToolsTests: XCTestCase {

    // MARK: sliceStrip

    func test_sliceStrip_splits_strip_by_alpha_gutter() throws {
        let strip = makeStrip(frameCount: 3, cellW: 20, cellH: 24, gutter: 8)
        let frames = try SpriteTools.sliceStrip(strip, frameCount: 3)
        XCTAssertEqual(frames.count, 3)
        for (i, f) in frames.enumerated() {
            XCTAssertEqual(f.width, 20, "frame \(i) width")
            XCTAssertEqual(f.height, 24, "frame \(i) height")
        }
    }

    func test_sliceStrip_throws_when_frame_count_mismatches_gutter_bands() {
        // 3 visible cells but requested 5 frames -> deterministic error, no silent fallback.
        let strip = makeStrip(frameCount: 3, cellW: 20, cellH: 24, gutter: 8)
        XCTAssertThrowsError(try SpriteTools.sliceStrip(strip, frameCount: 5))
    }

    func test_sliceStrip_returns_empty_for_fully_transparent_strip() throws {
        let blank = PixelImage.transparent(width: 60, height: 24)
        let frames = try SpriteTools.sliceStrip(blank, frameCount: 2)
        XCTAssertTrue(frames.isEmpty, "Transparent strip should yield no frames")
    }

    // MARK: composeSheet

    func test_composeSheet_builds_ragged_rows_roundtrip() throws {
        // Compose two rows (3 and 2 frames), then slice each composed row back
        // and confirm frame counts survive the round trip.
        let cell = CellSize(width: 20, height: 24)
        let strip3 = makeStrip(frameCount: 3, cellW: 20, cellH: 24, gutter: 8)
        let strip2 = makeStrip(frameCount: 2, cellW: 20, cellH: 24, gutter: 8)
        let row3 = try SpriteTools.sliceStrip(strip3, frameCount: 3)
        let row2 = try SpriteTools.sliceStrip(strip2, frameCount: 2)

        let sheet = SpriteTools.composeSheet(rows: [row3, row2], cell: cell, gutter: 1)

        // Row height band.
        let bandH = cell.height
        let stride = cell.height + 1
        let row0 = try SpriteTools.sliceStrip(try XCTUnwrap(sheet.cropping(x: 0, y: 0, width: sheet.width, height: bandH)), frameCount: 3)
        let row1 = try SpriteTools.sliceStrip(try XCTUnwrap(sheet.cropping(x: 0, y: stride, width: sheet.width, height: bandH)), frameCount: 2)
        XCTAssertEqual(row0.count, 3)
        XCTAssertEqual(row1.count, 2)
    }

    func test_composeSheet_cells_are_centered_within_their_slot() {
        let cell = CellSize(width: 40, height: 40)
        let small = makeStrip(frameCount: 1, cellW: 20, cellH: 20, gutter: 0)
        let sheet = SpriteTools.composeSheet(rows: [[small]], cell: cell, gutter: 0)
        XCTAssertEqual(sheet.width, 40)
        XCTAssertEqual(sheet.height, 40)
        // Top-left pixel must be transparent (frame centred, not flush top-left).
        XCTAssertEqual(sheet.alpha(x: 0, y: 0), 0, "corner should be transparent when frame is centred")
    }

    // MARK: validateSheet

    func test_validateSheet_reports_ok_for_matching_sheet() {
        let cell = CellSize(width: 20, height: 24)
        let sheet = makeRaggedSheet(rowFrameCounts: [3, 2], cellW: 20, cellH: 24, gutter: 1)
        let actions = [
            ActionSpec(id: "idle", frameCount: 3, durations: [], loop: true),
            ActionSpec(id: "jump", frameCount: 2, durations: [], loop: false),
        ]
        let report = SpriteTools.validateSheet(sheet, actions: actions, cell: cell)
        XCTAssertTrue(report.ok, "matching sheet should pass validation: \(report.issues)")
        XCTAssertTrue(report.issues.isEmpty)
    }

    func test_validateSheet_reports_missing_frames() {
        let cell = CellSize(width: 20, height: 24)
        // Only one row rendered but two actions declared.
        let sheet = makeRaggedSheet(rowFrameCounts: [3], cellW: 20, cellH: 24, gutter: 1)
        let actions = [
            ActionSpec(id: "idle", frameCount: 3, durations: [], loop: true),
            ActionSpec(id: "jump", frameCount: 2, durations: [], loop: false),
        ]
        let report = SpriteTools.validateSheet(sheet, actions: actions, cell: cell)
        XCTAssertFalse(report.ok, "missing row should fail validation")
        XCTAssertFalse(report.issues.isEmpty)
    }
}
