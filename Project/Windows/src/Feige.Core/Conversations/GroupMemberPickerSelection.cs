namespace Feige.Core.Conversations;

public enum GroupMemberPickerAvailability { Available, ExistingMember, NotConnected }

/// <summary>A row in the shared create/add-member picker; unavailable devices stay listed with a reason.</summary>
public sealed record GroupMemberPickerCandidate(string Fingerprint, string Name, GroupMemberPickerAvailability Availability)
{
    public string StatusText => Availability switch
    {
        GroupMemberPickerAvailability.Available => "已连接 · 可邀请",
        GroupMemberPickerAvailability.ExistingMember => "已在群中",
        _ => "未建立群聊连接",
    };

    public bool IsDimmed => Availability == GroupMemberPickerAvailability.NotConnected;
}

/// <summary>Selection rules for the member picker, kept out of WinUI so they can be tested on any platform.</summary>
public sealed class GroupMemberPickerSelection(int existingMemberCount)
{
    private readonly List<string> _selected = [];

    /// <summary>Members already counted against the limit, including this device (1 when creating a group).</summary>
    public int ExistingMemberCount { get; } = existingMemberCount;
    public IReadOnlyList<string> SelectedFingerprints => _selected;
    public int Remaining => Math.Max(0, GroupConversation.MaximumMembers - ExistingMemberCount - _selected.Count);

    public bool IsSelected(GroupMemberPickerCandidate candidate) =>
        candidate.Availability == GroupMemberPickerAvailability.ExistingMember || _selected.Contains(candidate.Fingerprint);

    public bool CanToggle(GroupMemberPickerCandidate candidate) =>
        candidate.Availability == GroupMemberPickerAvailability.Available && (_selected.Contains(candidate.Fingerprint) || Remaining > 0);

    public void Toggle(GroupMemberPickerCandidate candidate)
    {
        if (!CanToggle(candidate)) return;
        if (!_selected.Remove(candidate.Fingerprint)) _selected.Add(candidate.Fingerprint);
    }

    public void Remove(string fingerprint) => _selected.Remove(fingerprint);

    /// <summary>Selected devices that are still invitable; a device that disconnected after being picked is dropped.</summary>
    public IReadOnlyList<string> ConfirmedFingerprints(IEnumerable<GroupMemberPickerCandidate> candidates)
    {
        var available = candidates.Where(c => c.Availability == GroupMemberPickerAvailability.Available)
            .Select(c => c.Fingerprint).ToHashSet(StringComparer.Ordinal);
        return _selected.Where(available.Contains).ToArray();
    }

    public static IReadOnlyList<GroupMemberPickerCandidate> Visible(IEnumerable<GroupMemberPickerCandidate> candidates, string? search)
    {
        string query = search?.Trim() ?? "";
        return candidates
            .Where(c => query.Length == 0 || c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                c.Fingerprint.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Availability switch
            {
                GroupMemberPickerAvailability.Available => 0,
                GroupMemberPickerAvailability.ExistingMember => 1,
                _ => 2,
            })
            .ToArray();
    }
}
