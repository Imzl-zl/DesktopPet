import Foundation

/// A live activity tracked by `ActivityStore`, the successor of the old
/// agent session model. Grouped by `id`; each tracked activity carries a title and
/// a normalized state the pet's mood resolves from.
public struct ActivitySession: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var kind: ActivityEvent.Kind
    public var state: ActivityState
    /// Display title (app name, conversation summary, ...).
    public var title: String
    public var detail: String?
    /// Project path this activity belongs to (optional). Powers per-project
    /// pet windows (split mode); Phase 2 sources may fill it.
    public var project: String?
    public var createdAt: Date
    public var updatedAt: Date
    /// When the current state started (for state-duration display).
    public var stateSince: Date

    public init(
        id: String,
        kind: ActivityEvent.Kind,
        state: ActivityState,
        title: String,
        detail: String? = nil,
        project: String? = nil,
        createdAt: Date = Date(),
        updatedAt: Date = Date(),
        stateSince: Date = Date()
    ) {
        self.id = id
        self.kind = kind
        self.state = state
        self.title = title
        self.detail = detail
        self.project = project
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.stateSince = stateSince
    }
}
