import Foundation

/// One animation action = one row in the composed spritesheet.
/// Model-only (no UI, no IO) so it stays unit-testable on any platform.
struct ActionSpec: Codable, Equatable, Sendable {
    var id: String
    var frameCount: Int
    /// Per-frame durations in milliseconds. Empty = uniform timing.
    var durations: [Double]
    var loop: Bool
}

/// Validation result for a composed sheet.
struct SheetReport: Equatable, Sendable {
    var ok: Bool
    var issues: [String]

    static func ok() -> SheetReport { SheetReport(ok: true, issues: []) }
}

/// Cell size of one sprite frame. Kept as its own type so the image tools do
/// not depend on CoreGraphics (available on Apple platforms only).
struct CellSize: Equatable, Sendable {
    var width: Int
    var height: Int
}
