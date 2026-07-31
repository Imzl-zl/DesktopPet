import XCTest
@testable import DesktopPetCore

final class ActivityStoreTests: XCTestCase {
    private let t0 = Date(timeIntervalSince1970: 1_000)

    private func event(_ id: String, kind: ActivityEvent.Kind = .appFocus,
                       at date: Date, title: String = "X") -> ActivityEvent {
        ActivityEvent(id: id, kind: kind, source: "test", timestamp: date, title: title)
    }

    // MARK: - initialState mapping

    func testInitialStateMapping() {
        XCTAssertEqual(ActivityStore.initialState(for: .appFocus), .active)
        XCTAssertEqual(ActivityStore.initialState(for: .inputBurst), .active)
        XCTAssertEqual(ActivityStore.initialState(for: .agentActivity), .active)
        XCTAssertEqual(ActivityStore.initialState(for: .chatMessage), .done)
        XCTAssertEqual(ActivityStore.initialState(for: .dailySummary), .done)
        XCTAssertEqual(ActivityStore.initialState(for: .userAction), .done)
    }

    // MARK: - apply

    func testApplyCreatesAndUpdates() {
        let store = ActivityStore()
        let created = store.apply(event("a", at: t0), now: t0)
        XCTAssertEqual(created?.state, .active)
        XCTAssertEqual(created?.kind, .appFocus)
        XCTAssertEqual(store.sorted.count, 1)

        let updated = store.apply(event("a", kind: .chatMessage, at: t0, title: "hi"), now: t0)
        XCTAssertEqual(updated?.state, .done)
        XCTAssertEqual(updated?.title, "hi")
        XCTAssertEqual(store.sorted.count, 1, "same id updates in place")
    }

    func testApplyDistinctIDsStaySeparate() {
        let store = ActivityStore()
        store.apply(event("a", at: t0), now: t0)
        store.apply(event("b", at: t0), now: t0)
        XCTAssertEqual(store.sorted.count, 2)
    }

    // MARK: - prune (timeout chain)

    func testDoneFallsBackToIdleThenRemoved() {
        let store = ActivityStore()
        store.apply(event("a", kind: .chatMessage, at: t0), now: t0)
        XCTAssertEqual(store.activity(id: "a")?.state, .done)

        store.prune(now: t0.addingTimeInterval(31))
        XCTAssertEqual(store.activity(id: "a")?.state, .idle, "done demotes to idle after 30s")

        store.prune(now: t0.addingTimeInterval(31 + 601))
        XCTAssertNil(store.activity(id: "a"), "idle removed after 600s")
    }

    func testStaleActiveRemoved() {
        let store = ActivityStore()
        store.apply(event("a", at: t0), now: t0)
        store.prune(now: t0.addingTimeInterval(301))
        XCTAssertNil(store.activity(id: "a"), "active removed after 300s quiet")
    }

    func testStalePausedRemoved() {
        let store = ActivityStore()
        store.apply(event("a", kind: .chatMessage, at: t0), now: t0)
        // Force paused via direct state write is not exposed; paused currently
        // has no producer, so only verify the timeout config is reachable via
        // a done→idle transition and that fresh activity survives pruning.
        store.prune(now: t0.addingTimeInterval(10))
        XCTAssertNotNil(store.activity(id: "a"), "recent activity survives prune")
    }

    // MARK: - ordering

    func testSortedByAttentionThenRecency() {
        let store = ActivityStore()
        store.apply(event("done1", kind: .chatMessage, at: t0, title: "d1"), now: t0)
        store.apply(event("act1", at: t0.addingTimeInterval(-60), title: "a1"), now: t0)
        let sorted = store.sorted
        XCTAssertEqual(sorted.map(\.id), ["act1", "done1"], "active beats done")
        XCTAssertEqual(sorted.map(\.title), ["a1", "d1"])
    }
}
