import XCTest
@testable import DesktopPetCore

final class MoodResolverTests: XCTestCase {
    private func session(_ state: ActivityState, id: String) -> ActivitySession {
        ActivitySession(id: id, kind: .userAction, state: state, title: id,
                        createdAt: Date(timeIntervalSince1970: 0),
                        updatedAt: Date(timeIntervalSince1970: 0),
                        stateSince: Date(timeIntervalSince1970: 0))
    }

    func testEmptyIsIdle() {
        XCTAssertEqual(MoodResolver.aggregate([]), .idle)
    }

    func testWorkingWins() {
        let sessions = [session(.active, id: "a"), session(.paused, id: "b"), session(.done, id: "c")]
        XCTAssertEqual(MoodResolver.aggregate(sessions), .working, "running work is prioritised")
    }

    func testWaitingBeatsDone() {
        XCTAssertEqual(MoodResolver.aggregate([session(.done, id: "a"), session(.paused, id: "b")]), .waiting)
    }

    func testIdleIsNotWorking() {
        // A merely idle activity keeps the pet idle, not "working".
        XCTAssertEqual(MoodResolver.aggregate([session(.idle, id: "a")]), .idle)
        XCTAssertEqual(MoodResolver.aggregate([session(.idle, id: "a"), session(.active, id: "b")]), .working)
    }

    func testDoneOnly() {
        XCTAssertEqual(MoodResolver.aggregate([session(.done, id: "a"), session(.idle, id: "b")]), .done)
    }
}
