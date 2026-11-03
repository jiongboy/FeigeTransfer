namespace Feige.Core.Profiles;

/// <summary>Stable, display-only avatar identifiers shared by both platform clients.</summary>
public static class AvatarIds
{
    public const string Default = "bird";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "bird", "cat", "fox", "panda", "bear", "fish", "rocket", "sun", "star", "leaf",
    };

    public static IReadOnlyCollection<string> All { get; } = Known;

    public static bool IsKnown(string? avatarId) => avatarId is not null && Known.Contains(avatarId);

    /// <summary>Uses the default avatar for missing or unknown local/display metadata.</summary>
    public static string Normalize(string? avatarId) => IsKnown(avatarId) ? avatarId! : Default;

    /// <summary>Normalizes only a full SHA-256 device fingerprint; discovery prefixes are not identities.</summary>
    public static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        string compact = string.Concat(fingerprint.Where(character => !char.IsWhiteSpace(character)));
        return compact.Length == 64 && compact.All(char.IsAsciiHexDigit) ? compact.ToUpperInvariant() : null;
    }
}
