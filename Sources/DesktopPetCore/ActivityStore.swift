import Foundation

/// In-memory store of live activities.
///
/// Pure logic, deliberately not thread-safe and free of wall-clock reads:
/// callers pass `now` so behaviour is deterministic and testable. The app
/// layer confines all access to the main actor.
public final class ActivityStore {
    /// `done` activities fall back to `idle` after this much quiet time.
    public var doneToIdleAfter: TimeInterval
    /// `idle` activities are removed after this much quiet time.
    public var removeIdleAfter: TimeInterval
    /// `active` activities with no update for this long are removed (the
    /// producer died or stopped reporting).
    public var staleActiveAfter: TimeInterval
    /// `paused` activities are dropped sooner (nothing is actually waiting).
    public var stalePausedAfter: TimeInterval

    private var byID: [String: ActivitySession] = [:]

    public init(doneToIdleAfter: TimeInterval = 30,
                removeIdleAfter: TimeInterval = 600,
                staleActiveAfter: TimeInterval = 300,
                stalePausedAfter: TimeInterval = 90) {
        self.doneToIdleAfter = doneToIdleAfter
        self.removeIdleAfter = removeIdleAfter
        self.staleActiveAfter = staleActiveAfter
        self.stalePausedAfter = stalePausedAfter
    }

    /// Removes all tracked activities.
    public func clear() {
        byID.removeAll()
    }

    /// Removes a single activity.
    public func remove(id: String) {
        byID.removeValue(forKey: id)
    }

    /// The normalized state a fresh event of this kind starts in.
    public static func initialState(for kind: ActivityEvent.Kind) -> ActivityState {
        switch kind {
        case .appFocus, .inputBurst, .agentActivity: return .active
        case .chatMessage, .dailySummary, .userAction: return .done
        }
    }

    /// Applies an event, creating or updating the matching activity.
    /// Returns the updated activity, or `nil` if it has no displayable state.
    @discardableResult
    public func apply(_ event: ActivityEvent, now: Date) -> ActivitySession? {
        let state = Self.initialState(for: event.kind)
        if var existing = byID[event.id] {
            if existing.state != state { existing.stateSince = now }
            existing.state = state
            existing.updatedAt = now
            existing.title = event.title
            existing.detail = event.detail
            byID[event.id] = existing
            return existing
        }
        let session = ActivitySession(
            id: event.id,
            kind: event.kind,
            state: state,
            title: event.title,
            detail: event.detail,
            createdAt: now,
            updatedAt: now,
            stateSince: now
        )
        byID[event.id] = session
        return session
    }

    /// Demotes stale `done` activities to `idle`, removes long-idle ones, and
    /// drops active/paused activities that went quiet.
    public func prune(now: Date) {
        for id in Array(byID.keys) {
            guard let session = byID[id] else { continue }
            let quiet = now.timeIntervalSince(session.updatedAt)
            switch session.state {
            case .done:
                if quiet >= doneToIdleAfter {
                    var s = session
                    s.state = .idle
                    s.updatedAt = now
                    s.stateSince = now
                    byID[id] = s
                }
            case .idle:
                if quiet >= removeIdleAfter {
                    byID.removeValue(forKey: id)
                }
            case .paused:
                if quiet >= stalePausedAfter {
                    byID.removeValue(forKey: id)
                }
            case .active:
                if quiet >= staleActiveAfter {
                    byID.removeValue(forKey: id)
                }
            }
        }
    }

    public var activities: [ActivitySession] {
        Array(byID.values)
    }

    /// Activities ordered by attention priority then recency, for display.
    public var sorted: [ActivitySession] {
        byID.values.sorted { lhs, rhs in
            let lp = lhs.state.attentionPriority
            let rp = rhs.state.attentionPriority
            if lp != rp { return lp > rp }
            return lhs.updatedAt > rhs.updatedAt
        }
    }

    public func activity(id: String) -> ActivitySession? {
        byID[id]
    }
}
