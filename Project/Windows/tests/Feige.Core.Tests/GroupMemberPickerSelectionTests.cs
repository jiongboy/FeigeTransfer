using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class GroupMemberPickerSelectionTests
{
    private static GroupMemberPickerCandidate Candidate(string id, GroupMemberPickerAvailability availability) =>
        new(id, "设备" + id, availability);

    [Fact]
    public void OnlyAvailableCandidatesToggle()
    {
        var selection = new GroupMemberPickerSelection(2);
        var existing = Candidate("e", GroupMemberPickerAvailability.ExistingMember);
        var offline = Candidate("o", GroupMemberPickerAvailability.NotConnected);
        var ready = Candidate("r", GroupMemberPickerAvailability.Available);
        selection.Toggle(existing);
        selection.Toggle(offline);
        Assert.Empty(selection.SelectedFingerprints);
        Assert.True(selection.IsSelected(existing));
        selection.Toggle(ready);
        Assert.Equal(["r"], selection.SelectedFingerprints);
        selection.Toggle(ready);
        Assert.Empty(selection.SelectedFingerprints);
    }

    [Fact]
    public void RemainingCapacityBlocksFurtherSelections()
    {
        var selection = new GroupMemberPickerSelection(GroupConversation.MaximumMembers - 1);
        var first = Candidate("1", GroupMemberPickerAvailability.Available);
        var second = Candidate("2", GroupMemberPickerAvailability.Available);
        selection.Toggle(first);
        Assert.Equal(0, selection.Remaining);
        Assert.False(selection.CanToggle(second));
        selection.Toggle(second);
        Assert.Equal(["1"], selection.SelectedFingerprints);
        Assert.True(selection.CanToggle(first));
    }

    [Fact]
    public void ConfirmedFingerprintsDropDevicesThatDisconnected()
    {
        var selection = new GroupMemberPickerSelection(1);
        selection.Toggle(Candidate("1", GroupMemberPickerAvailability.Available));
        selection.Toggle(Candidate("2", GroupMemberPickerAvailability.Available));
        var later = new[] { Candidate("1", GroupMemberPickerAvailability.Available), Candidate("2", GroupMemberPickerAvailability.NotConnected) };
        Assert.Equal(["1"], selection.ConfirmedFingerprints(later));
    }

    [Fact]
    public void VisibleSortsInvitableFirstAndFilters()
    {
        var items = new[]
        {
            Candidate("o", GroupMemberPickerAvailability.NotConnected),
            Candidate("e", GroupMemberPickerAvailability.ExistingMember),
            Candidate("r", GroupMemberPickerAvailability.Available),
        };
        Assert.Equal(["r", "e", "o"], GroupMemberPickerSelection.Visible(items, "").Select(c => c.Fingerprint));
        Assert.Equal(["o"], GroupMemberPickerSelection.Visible(items, "设备o").Select(c => c.Fingerprint));
    }
}
