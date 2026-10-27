using System.Text;
using Feige.Core.Discovery;

namespace Feige.Core.Conversations;

/// <summary>
/// A conversation's current UI label. Discovery names are advisory and never replace the full authenticated
/// fingerprint, the connection's handshake name, or names recorded on historical messages.
/// </summary>
public sealed class ConversationDisplayName(string fingerprint, string name)
{
    public string Fingerprint { get; } = ConversationHistory.NormalizeFingerprint(fingerprint);
    public string Name { get; private set; } = name;

    public bool SetName(string name)
    {
        if (Name == name) return false;
        Name = name;
        return true;
    }

    /// <summary>Applies a v3 discovery label to an already known conversation, without creating or authenticating one.</summary>
    public bool ApplyDiscovery(DiscoveredPeer peer)
    {
        if (Fingerprint.Length != 64 || !Fingerprint.All(char.IsAsciiHexDigit) ||
            peer.Instance != FeigeService.InstanceName("peer", Fingerprint, version: 3) ||
            string.IsNullOrWhiteSpace(peer.DeviceName) || Encoding.UTF8.GetByteCount(peer.DeviceName) > 128 ||
            peer.DeviceName.Any(char.IsControl)) return false;

        return SetName(peer.DeviceName.Trim());
    }
}
