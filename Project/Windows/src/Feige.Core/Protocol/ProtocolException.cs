namespace Feige.Core.Protocol;

/// <summary>
/// Why a record or handshake message was rejected. Every one of these is fatal for the connection:
/// v1 has no <c>close_notify</c> or in-band error record, so the only response is to drop the socket (§4.6).
/// </summary>
public enum ProtocolError
{
    /// <summary>A length field is inconsistent, out of range, or the payload size is illegal for the type.</summary>
    InvalidLength,

    /// <summary>The record type byte is outside 1..7.</summary>
    InvalidType,

    /// <summary>The sequence number was duplicated, skipped or reordered (§4.2).</summary>
    UnexpectedSequence,

    /// <summary>The GCM tag did not verify: header, ciphertext or tag was tampered with or truncated.</summary>
    AuthenticationFailed,

    /// <summary>The 64-bit sequence space for this direction is exhausted and a new handshake is required.</summary>
    SequenceExhausted,

    /// <summary>A record was sent or received after <c>finish</c> in that direction (§4.2).</summary>
    AlreadyFinished,

    /// <summary>The peer closed the connection before an authenticated <c>finish</c> record.</summary>
    UnexpectedEndOfStream,

    /// <summary>More than <c>MaxReadBatchSize</c> unread bytes accumulated without a complete record.</summary>
    FrameTooLarge,

    /// <summary>Bytes followed an authenticated <c>finish</c> record on the same connection.</summary>
    UnexpectedTrailingData,

    /// <summary>A handshake frame's length prefix or JSON body is invalid (§3.1).</summary>
    InvalidHandshakeFrame,

    /// <summary>An <c>offer</c> field violates the constraints in §3.3.</summary>
    InvalidOffer,

    /// <summary>The two offers cannot form a transcript: version, roles or identity mismatch (§3.4).</summary>
    InvalidTranscript,

    /// <summary>The two sides used different <c>sessionCode</c> values (§3.4).</summary>
    PairingCodeMismatch,

    /// <summary>An RSA-PSS signature did not verify against the transcript (§3.6).</summary>
    SignatureFailed,

    /// <summary>A fingerprint does not match the key it is supposed to identify (§3.6).</summary>
    FingerprintMismatch,

    /// <summary>The local user declined the confirmation prompt, so no session key was derived (§3.8).</summary>
    UserRejected,

    /// <summary>The <c>manifest</c> payload is malformed or violates §4.4.</summary>
    InvalidManifest,

    /// <summary>The peer answered <c>decision 0x00</c> or otherwise refused the transfer (§5.2).</summary>
    PeerRejected,

    /// <summary>Received bytes do not add up to <c>manifest.size</c>.</summary>
    SizeMismatch,

    /// <summary>The received file's SHA-256 differs from <c>manifest.sha256</c>.</summary>
    ChecksumMismatch,

    /// <summary>A record type is legal on the wire but not expected at this point in the exchange.</summary>
    UnexpectedMessage,

    /// <summary>The local file system refused an operation (create, write, rename).</summary>
    StorageFailure,

    /// <summary>
    /// A connect, handshake or record read/write exceeded a local timeout (§2 makes timeouts local policy, not
    /// wire format). Fatal for the connection like every other error here.
    /// </summary>
    Timeout,
}

/// <summary>
/// A protocol violation detected locally. Instances never carry record contents, keys or full file paths,
/// because callers may surface the message in logs (开发总则 §安全).
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <summary>Creates an exception for <paramref name="error"/>.</summary>
    public ProtocolException(ProtocolError error)
        : base(Describe(error))
    {
        Error = error;
    }

    /// <summary>Creates an exception for <paramref name="error"/> with an inner cause.</summary>
    public ProtocolException(ProtocolError error, Exception? innerException)
        : base(Describe(error), innerException)
    {
        Error = error;
    }

    /// <summary>The rejection reason, for callers that need to branch on it.</summary>
    public ProtocolError Error { get; }

    private static string Describe(ProtocolError error) => error switch
    {
        ProtocolError.InvalidLength => "Record or message length is invalid.",
        ProtocolError.InvalidType => "Record type is not defined by protocol v1.",
        ProtocolError.UnexpectedSequence => "Record sequence number is duplicated, skipped or reordered.",
        ProtocolError.AuthenticationFailed => "Record authentication failed.",
        ProtocolError.SequenceExhausted => "Record sequence space exhausted; a new handshake is required.",
        ProtocolError.AlreadyFinished => "A record was used after finish in this direction.",
        ProtocolError.UnexpectedEndOfStream => "Stream ended before an authenticated finish record.",
        ProtocolError.FrameTooLarge => "Unread record buffer exceeded the maximum frame budget.",
        ProtocolError.UnexpectedTrailingData => "Bytes followed the authenticated finish record.",
        ProtocolError.InvalidHandshakeFrame => "Handshake frame length or JSON body is invalid.",
        ProtocolError.InvalidOffer => "Handshake offer field violates protocol constraints.",
        ProtocolError.InvalidTranscript => "Offers do not form a valid transcript.",
        ProtocolError.PairingCodeMismatch => "The two sides used different pairing codes.",
        ProtocolError.SignatureFailed => "Identity signature verification failed.",
        ProtocolError.FingerprintMismatch => "Identity fingerprint does not match the key.",
        ProtocolError.UserRejected => "The local user rejected the handshake confirmation.",
        ProtocolError.InvalidManifest => "Transfer manifest is invalid.",
        ProtocolError.PeerRejected => "The peer refused the transfer.",
        ProtocolError.SizeMismatch => "Received byte count does not match the manifest.",
        ProtocolError.ChecksumMismatch => "Received SHA-256 does not match the manifest.",
        ProtocolError.UnexpectedMessage => "Record type is not expected at this point in the transfer.",
        ProtocolError.StorageFailure => "The file system refused a local operation.",
        ProtocolError.Timeout => "The connection exceeded a local timeout.",
        _ => "Protocol violation.",
    };
}
