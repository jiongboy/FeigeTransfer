using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Feige.Core.Protocol;

/// <summary>
/// The <c>manifest</c> record payload (通信协议 §4.4): what the sender wants to transfer, before any byte is written.
/// </summary>
/// <remarks>
/// <para>
/// This is the only plaintext the receiver sees before the user decides, so every field is validated here and the
/// name is never used as a path until it passed <see cref="ValidateFileName"/>.
/// </para>
/// <para>
/// <c>fileName</c> must be a bare name: no <c>/</c>, no <c>\</c>, no control characters, not <c>.</c> or <c>..</c>.
/// That is what prevents path traversal — the receiver joins it to its own directory and nothing else.
/// </para>
/// </remarks>
public sealed class TransferManifest
{
    /// <summary>Maximum transfer size: 100 MiB.</summary>
    public const long MaxFileSize = 100L * 1024 * 1024;

    /// <summary>Maximum length of <c>fileName</c> in UTF-8 bytes.</summary>
    public const int MaxFileNameBytes = 255;

    /// <summary>Length of the SHA-256 digest field.</summary>
    public const int Sha256Size = 32;

    private readonly byte[] _sha256;

    /// <summary>Creates a manifest with a fresh <see cref="Guid"/> transfer id.</summary>
    public TransferManifest(string fileName, long size, ReadOnlySpan<byte> sha256)
        : this(Guid.NewGuid(), fileName, size, sha256)
    {
    }

    /// <summary>Creates a manifest with an explicit transfer id (tests and resumed transfers).</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidManifest"/> for any field out of range.</exception>
    public TransferManifest(Guid transferId, string fileName, long size, ReadOnlySpan<byte> sha256)
    {
        ValidateFileName(fileName);

        if (size < 1 || size > MaxFileSize)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        if (sha256.Length != Sha256Size)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        TransferId = transferId;
        FileName = fileName;
        Size = size;
        _sha256 = sha256.ToArray();
    }

    /// <summary>Identifier the receiver binds its confirmation to; unique per transfer.</summary>
    public Guid TransferId { get; }

    /// <summary>Bare file name chosen by the sender, already validated.</summary>
    public string FileName { get; }

    /// <summary>Exact number of file bytes that will follow.</summary>
    public long Size { get; }

    /// <summary>SHA-256 of the complete file.</summary>
    public ReadOnlyMemory<byte> Sha256 => _sha256;

    /// <summary>
    /// Validates the <c>fileName</c> rules of §4.4.
    /// </summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidManifest"/> when illegal.</exception>
    public static void ValidateFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." || fileName is "..")
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        if (Encoding.UTF8.GetByteCount(fileName) > MaxFileNameBytes)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        foreach (char c in fileName)
        {
            if (c is '/' or '\\' || char.IsControl(c))
            {
                throw new ProtocolException(ProtocolError.InvalidManifest);
            }
        }
    }

    /// <summary>Encodes the manifest as the compact UTF-8 JSON of §4.4.</summary>
    public byte[] ToJson()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("transferID", TransferId.ToString("D").ToUpperInvariant());
            writer.WriteString("fileName", FileName);
            writer.WriteNumber("size", Size);
            writer.WriteBase64String("sha256", _sha256);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes and validates a manifest payload.</summary>
    /// <exception cref="ProtocolException"><see cref="ProtocolError.InvalidManifest"/> for malformed input.</exception>
    public static TransferManifest FromJson(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest, ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ProtocolError.InvalidManifest);
            }

            string transferIdText = RequireString(root, "transferID");
            if (!Guid.TryParse(transferIdText, out Guid transferId) || transferId == Guid.Empty)
            {
                throw new ProtocolException(ProtocolError.InvalidManifest);
            }

            string fileName = RequireString(root, "fileName");

            JsonElement sizeElement = Require(root, "size");
            if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out long size))
            {
                throw new ProtocolException(ProtocolError.InvalidManifest);
            }

            byte[] sha256;
            try
            {
                sha256 = Require(root, "sha256").GetBytesFromBase64();
            }
            catch (FormatException ex)
            {
                throw new ProtocolException(ProtocolError.InvalidManifest, ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new ProtocolException(ProtocolError.InvalidManifest, ex);
            }

            return new TransferManifest(transferId, fileName, size, sha256);
        }
    }

    /// <summary>Constant-time comparison against a computed digest.</summary>
    public bool MatchesHash(ReadOnlySpan<byte> computed) => CryptographicOperations.FixedTimeEquals(_sha256, computed);

    /// <summary>
    /// Hashes a local file and builds the manifest that announces it.
    /// </summary>
    public static async Task<TransferManifest> ForFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fileName = Path.GetFileName(path);
        ValidateFileName(fileName);

        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1 || info.Length > MaxFileSize)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new TransferManifest(fileName, info.Length, digest);
    }

    /// <summary>Log-safe summary; the file name is included because it is user-visible, no key material is.</summary>
    public override string ToString() => $"TransferManifest({FileName}, {Size} bytes)";

    private static JsonElement Require(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        JsonElement value = Require(parent, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(ProtocolError.InvalidManifest);
        }

        return value.GetString()!;
    }
}
