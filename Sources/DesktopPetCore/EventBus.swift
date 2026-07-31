import Foundation

/// Minimal in-app event bus, replacing the agent hook socket channel.
/// Phase 2 producers (DesktopMonitor, ConversationController, ...) publish
/// `ActivityEvent`s here; the pet layer subscribes. Thread-safe: all mutable
/// state is guarded by `lock`, so the class opts out of compiler concurrency
/// checks via `@unchecked Sendable` (SE-0343: the class itself guarantees
/// thread safety).
public final class EventBus: @unchecked Sendable {
    public static let shared = EventBus()

    private var subscribers: [UUID: (ActivityEvent) -> Void] = [:]
    private let lock = NSLock()

    private init() {}

    public func publish(_ event: ActivityEvent) {
        lock.lock()
        let handlers = Array(subscribers.values)
        lock.unlock()
        for handler in handlers {
            handler(event)
        }
    }

    /// Registers a handler; returns a token for `unsubscribe`.
    @discardableResult
    public func subscribe(_ handler: @escaping (ActivityEvent) -> Void) -> UUID {
        let token = UUID()
        lock.lock()
        subscribers[token] = handler
        lock.unlock()
        return token
    }

    public func unsubscribe(_ token: UUID) {
        lock.lock()
        subscribers.removeValue(forKey: token)
        lock.unlock()
    }
}
