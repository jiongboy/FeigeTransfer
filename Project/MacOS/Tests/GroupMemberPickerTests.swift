import XCTest
@testable import FeigeMac

final class GroupMemberPickerTests: XCTestCase {
    private func candidate(_ id: String, _ availability: GroupMemberPickerAvailability) -> GroupMemberPickerCandidate {
        GroupMemberPickerCandidate(id: id, name: "设备\(id)", avatarID: "a", availability: availability)
    }

    func testOnlyAvailableCandidatesToggle() {
        var selection = GroupMemberPickerSelection(existingMemberCount: 2)
        let existing = candidate("e", .existingMember)
        let offline = candidate("o", .notConnected)
        let old = candidate("v", .unsupportedVersion)
        let ready = candidate("r", .available)
        for item in [existing, offline, old] { selection.toggle(item) }
        XCTAssertTrue(selection.selectedIDs.isEmpty)
        XCTAssertTrue(selection.isSelected(existing), "existing members stay checked")
        selection.toggle(ready)
        XCTAssertEqual(selection.selectedIDs, ["r"])
        selection.toggle(ready)
        XCTAssertTrue(selection.selectedIDs.isEmpty)
    }

    func testRemainingCapacityBlocksFurtherSelections() {
        var selection = GroupMemberPickerSelection(existingMemberCount: GroupConversation.maximumMemberCount - 1)
        let first = candidate("1", .available), second = candidate("2", .available)
        selection.toggle(first)
        XCTAssertEqual(selection.remaining, 0)
        XCTAssertFalse(selection.canToggle(second))
        selection.toggle(second)
        XCTAssertEqual(selection.selectedIDs, ["1"])
        XCTAssertTrue(selection.canToggle(first), "a selected device can still be unchecked at the limit")
    }

    func testConfirmedIDsDropDevicesThatDisconnected() {
        var selection = GroupMemberPickerSelection(existingMemberCount: 1)
        selection.toggle(candidate("1", .available))
        selection.toggle(candidate("2", .available))
        let later = [candidate("1", .available), candidate("2", .notConnected)]
        XCTAssertEqual(selection.confirmedIDs(in: later), ["1"])
    }

    func testVisibleSortsInvitableFirstAndFilters() {
        let items = [candidate("o", .notConnected), candidate("e", .existingMember), candidate("r", .available)]
        XCTAssertEqual(GroupMemberPickerSelection.visible(items, matching: "").map(\.id), ["r", "e", "o"])
        XCTAssertEqual(GroupMemberPickerSelection.visible(items, matching: "设备o").map(\.id), ["o"])
    }
}
