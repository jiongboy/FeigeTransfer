using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feige.Core.Conversations;

namespace Feige.Core.Tests;

public sealed class V4GroupWireTests
{
    private static readonly JsonSerializerOptions FixtureJson = new()
    {
        PropertyNameCaseInsensitive = false
    };

    [Fact]
    public void SharedVectors_DecodeEncodeAndHashToCanonicalBytes()
    {
        V4VectorFixture fixture = LoadFixture();
        Assert.Equal(V4GroupWire.Canonicalization, fixture.Canonicalization);
        Assert.Equal(4, fixture.Version);

        foreach (V4Vector vector in fixture.Vectors)
        {
            object decoded = Decode(vector.Type, Encoding.UTF8.GetBytes(vector.CanonicalJson));
            byte[] canonical = Encode(vector.Type, decoded);
            Assert.Equal(vector.CanonicalJson, Encoding.UTF8.GetString(canonical));
            Assert.Equal(vector.Sha256Hex,
                Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant());
        }
    }

    [Fact]
    public void SharedInvalidVectors_AreRejected()
    {
        V4VectorFixture fixture = LoadFixture();
        foreach (V4InvalidVector vector in fixture.InvalidVectors)
        {
            Assert.Throws<InvalidDataException>(() => Decode(vector.Type, Encoding.UTF8.GetBytes(vector.RawJson)));
        }
    }

    [Fact]
    public void StrictDecoders_RejectUnknownNestedFields()
    {
        string control = LoadFixture().Vectors.Single(vector => vector.Type == "groupControl").CanonicalJson;
        string nestedUnknown = control.Replace(
            "\"displayName\":\"Owner\"",
            "\"displayName\":\"Owner\",\"extra\":true",
            StringComparison.Ordinal);

        Assert.NotEqual(control, nestedUnknown);
        Assert.Throws<InvalidDataException>(() => V4GroupWire.DecodeControl(Encoding.UTF8.GetBytes(nestedUnknown)));

        const string fileMessage = "{\"id\":\"00000000-0000-0000-0000-000000000002\",\"groupID\":\"00000000-0000-0000-0000-000000000001\",\"senderFingerprint\":\"0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101 0101\",\"memberRevision\":7,\"date\":1700000000123,\"kind\":\"file\",\"fileMetadata\":{\"transferID\":\"00000000-0000-0000-0000-000000000004\",\"fileName\":\"a.txt\",\"size\":1,\"extra\":true}}";
        Assert.Throws<InvalidDataException>(() => V4GroupWire.DecodeGroupText(Encoding.UTF8.GetBytes(fileMessage)));
    }

    [Fact]
    public void MembershipAbort_RequiresExactEpochTransitionAndNoPayload()
    {
        Guid groupID = Guid.NewGuid();
        Guid eventID = Guid.NewGuid();

        string owner = string.Concat(Enumerable.Repeat("01", 32));
        string target = string.Concat(Enumerable.Repeat("02", 32));
        _ = new GroupControlMessage(GroupControlKind.MembershipAbort, groupID, owner, 0, eventID, targetFingerprint: target);
        _ = new GroupControlMessage(GroupControlKind.MembershipAbort, groupID, owner, 2, eventID, baseEpoch: 1, targetFingerprint: target);

        Assert.Throws<InvalidDataException>(() => new GroupControlMessage(
            GroupControlKind.MembershipAbort, groupID, owner, 3, eventID, baseEpoch: 1, targetFingerprint: target));
        Assert.Throws<InvalidDataException>(() => new GroupControlMessage(
            GroupControlKind.MembershipAbort, groupID, owner, 1, eventID, targetFingerprint: target));
        Assert.Throws<InvalidDataException>(() => new GroupControlMessage(
            GroupControlKind.MembershipAbort, groupID, owner, 0, eventID, name: "invalid", targetFingerprint: target));
        Assert.Throws<InvalidDataException>(() => new GroupControlMessage(
            GroupControlKind.MembershipAbort, groupID, owner, 0, eventID));
    }

    [Fact]
    public void TextAck_RejectsContradictoryAcceptedAndReasonState()
    {
        Guid groupID = Guid.NewGuid();
        Guid messageID = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => new GroupTextAck(groupID, messageID, 1, accepted: true, reason: "declined"));
        Assert.Throws<InvalidDataException>(() => new GroupTextAck(groupID, messageID, 1, accepted: false));
    }

    private static V4VectorFixture LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v4.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<V4VectorFixture>(stream, FixtureJson)
            ?? throw new InvalidDataException("共享 v4 向量 fixture 为空。");
    }

    private static object Decode(string type, byte[] json) => type switch
    {
        "groupControl" => V4GroupWire.DecodeControl(json),
        "groupText" => V4GroupWire.DecodeGroupText(json),
        "groupTextAck" => V4GroupWire.DecodeGroupTextAck(json),
        _ => throw new InvalidDataException($"未知的共享向量类型：{type}")
    };

    private static byte[] Encode(string type, object value) => type switch
    {
        "groupControl" when value is GroupControlMessage control => V4GroupWire.EncodeCanonical(control),
        "groupText" when value is GroupMessageEnvelope envelope => V4GroupWire.EncodeCanonical(envelope),
        "groupTextAck" when value is GroupTextAck acknowledgement => V4GroupWire.EncodeCanonical(acknowledgement),
        _ => throw new InvalidDataException($"向量类型与解码模型不匹配：{type}")
    };

    private sealed class V4VectorFixture
    {
        [JsonPropertyName("format")]
        public string Format { get; init; } = string.Empty;

        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("canonicalization")]
        public string Canonicalization { get; init; } = string.Empty;

        [JsonPropertyName("vectors")]
        public List<V4Vector> Vectors { get; init; } = [];

        [JsonPropertyName("invalidVectors")]
        public List<V4InvalidVector> InvalidVectors { get; init; } = [];
    }

    private sealed class V4Vector
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("canonicalJson")]
        public string CanonicalJson { get; init; } = string.Empty;

        [JsonPropertyName("sha256Hex")]
        public string Sha256Hex { get; init; } = string.Empty;
    }

    private sealed class V4InvalidVector
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("rawJson")]
        public string RawJson { get; init; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = string.Empty;
    }
}
