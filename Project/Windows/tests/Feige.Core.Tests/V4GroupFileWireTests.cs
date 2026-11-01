using System.Text;
using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class V4GroupFileWireTests
{
    private static readonly Guid GroupID = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MessageID = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid TransferID = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly byte[] Digest = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void Manifest_EncodesAndDecodesSwiftWireShapeWithDefaultKind()
    {
        var metadata = new V4GroupFileMetadata(TransferID, "hello.txt", 0);
        var manifest = new V4GroupFileManifest(GroupID, MessageID, "fingerprint", 7, metadata, Digest);
        string json = Encoding.UTF8.GetString(V4GroupFileWire.EncodeManifest(manifest));
        string digestBase64 = Convert.ToBase64String(Digest);

        Assert.Equal(
            $"{{\"groupID\":\"{GroupID:D}\",\"messageID\":\"{MessageID:D}\",\"senderFingerprint\":\"fingerprint\",\"memberRevision\":7,\"metadata\":{{\"transferID\":\"{TransferID:D}\",\"fileName\":\"hello.txt\",\"size\":0,\"kind\":\"file\"}},\"sha256\":\"{digestBase64}\"}}",
            json);

        V4GroupFileManifest decoded = V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(json));
        Assert.Equal(manifest.GroupID, decoded.GroupID);
        Assert.Equal(manifest.MessageID, decoded.MessageID);
        Assert.Equal(manifest.SenderFingerprint, decoded.SenderFingerprint);
        Assert.Equal(manifest.MemberRevision, decoded.MemberRevision);
        Assert.Equal(metadata, decoded.Metadata);
        Assert.Equal(Digest, decoded.Sha256);
    }

    [Fact]
    public void Manifest_EncodesImageKindAndPreservesUnicodeFileName()
    {
        var metadata = new V4GroupFileMetadata(TransferID, "照片.png", 123, V4GroupFileKind.Image);
        var manifest = new V4GroupFileManifest(GroupID, MessageID, "fingerprint", 2, metadata, Digest);
        string json = Encoding.UTF8.GetString(V4GroupFileWire.EncodeManifest(manifest));

        V4GroupFileManifest decoded = V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(json));
        Assert.Equal("照片.png", decoded.Metadata.FileName);
        Assert.Equal(V4GroupFileKind.Image, decoded.Metadata.Kind);
    }

    [Fact]
    public void Decision_OmitsAbsentReasonAndRequiresReasonForRejection()
    {
        var accepted = new V4GroupFileDecision(GroupID, TransferID, accepted: true);
        string acceptedJson = Encoding.UTF8.GetString(V4GroupFileWire.EncodeDecision(accepted));
        Assert.Equal($"{{\"groupID\":\"{GroupID:D}\",\"transferID\":\"{TransferID:D}\",\"accepted\":true}}", acceptedJson);
        Assert.Equal(accepted, V4GroupFileWire.DecodeDecision(Encoding.UTF8.GetBytes(acceptedJson)));

        var rejected = new V4GroupFileDecision(GroupID, TransferID, accepted: false, reason: "declined");
        string rejectedJson = Encoding.UTF8.GetString(V4GroupFileWire.EncodeDecision(rejected));
        Assert.Contains("\"reason\":\"declined\"", rejectedJson, StringComparison.Ordinal);
        Assert.Equal(rejected, V4GroupFileWire.DecodeDecision(Encoding.UTF8.GetBytes(rejectedJson)));

        Assert.Throws<InvalidDataException>(() => new V4GroupFileDecision(GroupID, TransferID, accepted: false));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileDecision(GroupID, TransferID, accepted: false, reason: " \n"));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileDecision(GroupID, TransferID, accepted: true, reason: "unexpected"));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileDecision(GroupID, TransferID, accepted: false, reason: new string('x', 257)));
    }

    [Fact]
    public void Receipt_UsesBase64DigestAndRoundTrips()
    {
        var receipt = new V4GroupFileReceipt(GroupID, TransferID, Digest);
        string json = Encoding.UTF8.GetString(V4GroupFileWire.EncodeReceipt(receipt));

        Assert.Equal($"{{\"groupID\":\"{GroupID:D}\",\"transferID\":\"{TransferID:D}\",\"sha256\":\"{Convert.ToBase64String(Digest)}\"}}", json);
        V4GroupFileReceipt decoded = V4GroupFileWire.DecodeReceipt(Encoding.UTF8.GetBytes(json));
        Assert.Equal(receipt.GroupID, decoded.GroupID);
        Assert.Equal(receipt.TransferID, decoded.TransferID);
        Assert.Equal(receipt.Sha256, decoded.Sha256);
    }

    [Theory]
    [InlineData("{\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"accepted\":true,\"extra\":1}")]
    [InlineData("{\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"accepted\":true}")]
    [InlineData("{\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"transferID\":\"00000000-0000-0000-0000-000000000003\"}")]
    public void Decision_RejectsUnknownDuplicateAndMissingFields(string json)
        => Assert.Throws<InvalidDataException>(() => V4GroupFileWire.DecodeDecision(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("\"metadata\":{\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"fileName\":\"a.txt\",\"size\":1,\"kind\":\"file\",\"extra\":true}")]
    [InlineData("\"metadata\":{\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"fileName\":\"a.txt\",\"size\":1,\"kind\":\"file\"}")]
    [InlineData("\"metadata\":{\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"fileName\":\"a.txt\",\"size\":1}")]
    public void Manifest_RejectsUnknownDuplicateAndMissingNestedFields(string metadata)
    {
        string json = ManifestPrefix + metadata + ManifestSuffix;
        Assert.Throws<InvalidDataException>(() => V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Manifest_RejectsUnknownDuplicateAndMissingRootFields()
    {
        string valid = ManifestPrefix + "\"metadata\":{\"transferID\":\"00000000-0000-0000-0000-000000000003\",\"fileName\":\"a.txt\",\"size\":1,\"kind\":\"file\"}" + ManifestSuffix;
        Assert.Throws<InvalidDataException>(() => V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(valid.Replace("\"memberRevision\":7,", "\"memberRevision\":7,\"extra\":true,", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(valid.Replace("\"groupID\":\"00000000-0000-0000-0000-000000000001\",", "\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"groupID\":\"00000000-0000-0000-0000-000000000001\",", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => V4GroupFileWire.DecodeManifest(Encoding.UTF8.GetBytes(valid.Replace("\"messageID\":\"00000000-0000-0000-0000-000000000002\",", string.Empty, StringComparison.Ordinal))));
    }

    [Fact]
    public void Models_EnforceSwiftManifestAndReceiptConstraints()
    {
        Assert.Throws<InvalidDataException>(() => new V4GroupFileMetadata(TransferID, "../unsafe", 1));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileMetadata(TransferID, "bad\nname", 1));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileMetadata(TransferID, "a.txt", (ulong)long.MaxValue + 1));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileManifest(GroupID, MessageID, "", 0,
            new V4GroupFileMetadata(TransferID, "a.txt", 1), Digest));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileManifest(GroupID, MessageID, "fingerprint", 0,
            new V4GroupFileMetadata(TransferID, "a.txt", 1), new byte[31]));
        Assert.Throws<InvalidDataException>(() => new V4GroupFileReceipt(GroupID, TransferID, new byte[33]));
    }

    private const string ManifestPrefix = "{\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"messageID\":\"00000000-0000-0000-0000-000000000002\",\"senderFingerprint\":\"fingerprint\",\"memberRevision\":7,";
    private const string ManifestSuffix = ",\"sha256\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"}";
}
