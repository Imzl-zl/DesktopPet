import Foundation

/// Normalized state of a tracked activity. Keeps the attention semantics of
/// the old agent states (working / waiting / done / idle) without any
/// agent coupling.
public enum ActivityState: String, Codable, Sendable, CaseIterable {
    /// Something is happening right now (app in use, work in progress).
    case active
    /// Waiting on the user (e.g. the pet asked something).
    case paused
    /// Just finished (e.g. a chat turn completed) — the pet can celebrate.
    case done
    /// No recent activity.
    case idle
}

extension ActivityState {
    /// Higher means more deserving of the user's attention (display ordering).
    public var attentionPriority: Int {
        switch self {
        case .active: return 4
        case .paused: return 3
        case .done: return 2
        case .idle: return 0
        }
    }
}
