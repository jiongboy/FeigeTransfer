using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Feige.Core.Protocol;

/// <summary>
/// JSON codec for the two handshake messages, <c>offer</c> and <c>proof</c> (通信协议 §3.3, §3.7), plus the
/// <c>u32</c> length framing of §3.1.
/// </summary>
/// <remarks>
/// <para>
/// Field names and value kinds are fixed: integers are JSON numbers, key material is Base64 strings, and the
/// wrapped object is always the top-level key <c>offer</c>. Base64 is what Swift's <c>JSONEncoder</c> emits for
/// <c>Data</c>, so this is the interop-critical detail.
/// </para>
/// <para>
/// Decoding is strict: a missing or wrongly typed field is <see cref="ProtocolError.InvalidOffer"/>, while a bad
/// length prefix or unparseable JSON is <see cref="ProtocolError.InvalidHandshakeFrame"/>. Nothing is defaulted.
/// </para>
/// </remarks>
public static class HandshakeMessages
{
    /// <summary>Maximum JSON payload in a handshake frame (§3.1).</summary>
    public const int MaxJsonBytes = 16 * 1024;

    /// <summary>Encodes and frames an <c>offer</c> message.</summary>
    public static byte[] EncodeOfferFrame(HandshakeOffer offer)
        => Frame(EncodeOfferJson(offer));

    /// <summary>Encodes and frames a <c>proof</c> message containing the offer and its signature.</summary>
    public static byte[] EncodeProofFrame(HandshakeOffer offer, ReadOnlySpan<byte> signature)
        => Frame(EncodeProofJson(offer, signature));

    /// <summary>Encodes an <c>offer</c> message payload without the length prefix.</summary>
    public static byte[] EncodeOfferJson(HandshakeOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return WriteJson(offer, ReadOnlySpan<byte>.Empty, includeSignature: false);
    }

    /// <summary>Encodes a <c>proof</c> message payload without the length prefix.</summary>
    public static byte[] EncodeProofJson(HandshakeOffer offer, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (signature.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        return WriteJson(offer, signature, includeSignature: true);
    }

    /// <summary>Decodes an <c>offer</c> payload (no length prefix).</summary>
    public static HandshakeOffer DecodeOffer(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = Parse(json);
        return ReadOfferElement(document.RootElement);
    }

    /// <summary>Decodes a <c>proof</c> payload (no length prefix).</summary>
    public static HandshakeProof DecodeProof(ReadOnlySpan<byte> json)
    {
        using JsonDocument document = Parse(json);
        JsonElement root = document.RootElement;
        HandshakeOffer offer = ReadOfferElement(root);

        if (!root.TryGetProperty("signature", out JsonElement signatureElement)
            || signatureElement.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        byte[] signature;
        try
        {
            signature = signatureElement.GetBytesFromBase64();
        }
        catch (FormatException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame, ex);
        }

        if (signature.Length != Crypto.IdentityKey.SignatureSize)
        {
            throw new ProtocolException(ProtocolError.SignatureFailed);
        }

        return new HandshakeProof(offer, signature);
    }

    /// <summary>Validates the length prefix of a framed message and returns the JSON payload extent.</summary>
    public static ReadOnlySpan<byte> Unframe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < sizeof(uint))
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (declared == 0 || declared > MaxJsonBytes || frame.Length != sizeof(uint) + declared)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        return frame[sizeof(uint)..];
    }

    /// <summary>Decodes a framed <c>offer</c> message.</summary>
    public static HandshakeOffer DecodeOfferFrame(ReadOnlySpan<byte> frame) => DecodeOffer(Unframe(frame));

    /// <summary>Decodes a framed <c>proof</c> message.</summary>
    public static HandshakeProof DecodeProofFrame(ReadOnlySpan<byte> frame) => DecodeProof(Unframe(frame));

    /// <summary>Prepends the <c>u32</c> big-endian length of <paramref name="payload"/>.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaxJsonBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        byte[] output = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)payload.Length);
        payload.CopyTo(output.AsSpan(sizeof(uint)));
        return output;
    }

    private static byte[] WriteJson(HandshakeOffer offer, ReadOnlySpan<byte> signature, bool includeSignature)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false });

        using (writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("offer");
            writer.WriteStartObject();
            writer.WriteNumber("version", offer.Version);
            writer.WriteNumber("role", (byte)offer.Role);
            writer.WriteString("deviceName", offer.DeviceName);
            writer.WriteBase64String("identityPublicKeyDER", offer.IdentityPublicKeyDer);
            writer.WriteBase64String("ephemeralPublicKey", offer.EphemeralPublicKey);
            writer.WriteBase64String("nonce", offer.Nonce);
            writer.WriteString("sessionCode", offer.SessionCode);
            writer.WriteEndObject();

            if (includeSignature)
            {
                writer.WriteBase64String("signature", signature);
            }

            writer.WriteEndObject();
        }

        byte[] json = buffer.WrittenSpan.ToArray();
        if (json.Length > MaxJsonBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        return json;
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaxJsonBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        try
        {
            return JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame, ex);
        }
    }

    private static HandshakeOffer ReadOfferElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("offer", out JsonElement offer)
            || offer.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException(ProtocolError.InvalidHandshakeFrame);
        }

        ushort version = RequireUInt16(offer, "version");
        byte role = RequireByte(offer, "role");
        string deviceName = RequireString(offer, "deviceName");
        byte[] identity = RequireBase64(offer, "identityPublicKeyDER");
        byte[] ephemeral = RequireBase64(offer, "ephemeralPublicKey");
        byte[] nonce = RequireBase64(offer, "nonce");
        string sessionCode = RequireString(offer, "sessionCode");

        return new HandshakeOffer(version, (HandshakeRole)role, deviceName, identity, ephemeral, nonce, sessionCode);
    }

    private static JsonElement Require(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        return value.GetString()!;
    }

    private static byte[] RequireBase64(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        try
        {
            return value.GetBytesFromBase64();
        }
        catch (FormatException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer, ex);
        }
    }

    private static ushort RequireUInt16(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        try
        {
            return value.GetUInt16();
        }
        catch (FormatException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer, ex);
        }
    }

    private static byte RequireByte(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer);
        }

        try
        {
            return value.GetByte();
        }
        catch (FormatException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidOffer, ex);
        }
    }
}
