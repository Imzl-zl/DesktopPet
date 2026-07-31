import Foundation

/// Unified activity event: every input that drives the pet normalizes to this.
/// Replaces the agent hook events as the data axis of the app.
public struct ActivityEvent: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        /// User switched to an app (desktop monitor, Phase 2).
        case appFocus
        /// Keyboard/mouse activity burst (desktop monitor, Phase 2).
        case inputBurst
        /// User chatted with the pet (conversation, Phase 2).
        case chatMessage
        /// A daily summary was generated (daily reporter, Phase 3).
        case dailySummary
        /// Manual user interaction (feeding, petting, ...).
        case userAction
        /// Reserved: a future agent bridge re-enables agent monitoring
        /// without touching the pet side (architecture stays reversible).
        case agentActivity
    }

    public var id: String
    public var kind: Kind
    /// Producer name, e.g. "desktopMonitor" / "conversation" / "user".
    public var source: String
    public var timestamp: Date
    /// Display text: app name, chat summary, ...
    public var title: String
    /// Optional extra context.
    public var detail: String?
    /// Feeding weight in 0…1 (how much this event feeds the pet).
    public var weight: Double

    public init(
        id: String = UUID().uuidString,
        kind: Kind,
        source: String,
        timestamp: Date = Date(),
        title: String,
        detail: String? = nil,
        weight: Double = 0
    ) {
        self.id = id
        self.kind = kind
        self.source = source
        self.timestamp = timestamp
        self.title = title
        self.detail = detail
        self.weight = weight
    }
}
