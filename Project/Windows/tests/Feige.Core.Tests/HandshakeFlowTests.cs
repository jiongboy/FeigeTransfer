using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Feige.Core.Crypto;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// The four-message exchange of 通信协议 §3.2, run end to end between two real <see cref="LocalHandshake"/>
/// instances: once over an in-memory duplex and once over a real TCP loopback socket.
/// </summary>
public sealed class HandshakeFlowTests
{
    [Fact]
    public async Task Run_BothSidesAgreeOnKeysSasAndFingerprints()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "123456");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "123456");

        (MemoryDuplex a, MemoryDuplex b) = MemoryDuplex.CreatePair();

        Task<HandshakeOutcome> initiatorTask = initiator.RunAsync(a, prompt =>
        {
            Assert.Equal("Win-B", prompt.Peer.DeviceName);
            Assert.Equal(identityB.Fingerprint, prompt.PeerFingerprint);
            return true;
        });
        Task<HandshakeOutcome> responderTask = responder.RunAsync(b, prompt =>
        {
            Assert.Equal("Win-A", prompt.Peer.DeviceName);
            Assert.Equal(identityA.Fingerprint, prompt.PeerFingerprint);
            return true;
        });

        HandshakeOutcome[] outcomes = await Task.WhenAll(initiatorTask, responderTask).WaitAsync(TimeSpan.FromSeconds(10));
        HandshakeOutcome fromInitiator = outcomes[0];
        HandshakeOutcome fromResponder = outcomes[1];

        // §3.9: the initiator's send key is the responder's receive key, and the nonce prefixes pair up.
        Assert.Equal(ToHex(fromInitiator.Keys.SendKey), ToHex(fromResponder.Keys.ReceiveKey));
        Assert.Equal(ToHex(fromInitiator.Keys.ReceiveKey), ToHex(fromResponder.Keys.SendKey));
        Assert.Equal(ToHex(fromInitiator.Keys.SendNoncePrefix), ToHex(fromResponder.Keys.ReceiveNoncePrefix));

        // §3.10: both sides must show the same six-digit SAS.
        Assert.Equal(fromInitiator.Sas, fromResponder.Sas);
        Assert.Equal(6, fromInitiator.Sas.Length);

        // Both transcripts must be byte-identical, otherwise the signatures could not have verified.
        Assert.Equal(ToHex(fromInitiator.Transcript.Encode()), ToHex(fromResponder.Transcript.Encode()));
        Assert.Equal(identityA.Fingerprint, fromResponder.PeerFingerprint);
        Assert.Equal(identityB.Fingerprint, fromInitiator.PeerFingerprint);

        // The derived keys must actually work with the record layer, in both directions.
        RecordCipher initiatorSend = RecordCipher.ForSending(fromInitiator.Keys);
        RecordCipher responderReceive = RecordCipher.ForReceiving(fromResponder.Keys);
        OpenedRecord record = responderReceive.Open(initiatorSend.Seal(TransferMessageType.Decision, [0x01]));
        Assert.Equal(TransferMessageType.Decision, record.Type);
        Assert.Equal("01", ToHex(record.Payload));
    }

    [Fact]
    public async Task Run_OverTcpLoopback_CompletesWithoutDeadlock()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "424242");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "424242");

        await using NetworkStream clientStream = client.GetStream();
        await using NetworkStream serverStream = server.GetStream();

        Task<HandshakeOutcome> a = initiator.RunAsync(new StreamHandshakeDuplex(clientStream), _ => true);
        Task<HandshakeOutcome> b = responder.RunAsync(new StreamHandshakeDuplex(serverStream), _ => true);

        HandshakeOutcome[] outcomes = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ToHex(outcomes[0].Keys.SendKey), ToHex(outcomes[1].Keys.ReceiveKey));
        Assert.Equal(outcomes[0].Sas, outcomes[1].Sas);
    }

    [Fact]
    public async Task Run_RejectsDifferentPairingCodes()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "123456");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "654321");

        (MemoryDuplex a, MemoryDuplex b) = MemoryDuplex.CreatePair();

        Task<HandshakeOutcome> initiatorTask = initiator.RunAsync(a, _ => true);
        Task<HandshakeOutcome> responderTask = responder.RunAsync(b, _ => true);

        ProtocolException first = await Assert.ThrowsAsync<ProtocolException>(() => initiatorTask);
        ProtocolException second = await Assert.ThrowsAsync<ProtocolException>(() => responderTask);
        Assert.Equal(ProtocolError.PairingCodeMismatch, first.Error);
        Assert.Equal(ProtocolError.PairingCodeMismatch, second.Error);
    }

    [Fact]
    public async Task Run_DoesNotDeriveKeysWhenUserRejects()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "123456");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "123456");

        (MemoryDuplex a, MemoryDuplex b) = MemoryDuplex.CreatePair();

        Task<HandshakeOutcome> initiatorTask = initiator.RunAsync(a, _ => false);
        Task<HandshakeOutcome> responderTask = responder.RunAsync(b, _ => true);

        ProtocolException rejected = await Assert.ThrowsAsync<ProtocolException>(() => initiatorTask);
        Assert.Equal(ProtocolError.UserRejected, rejected.Error);

        // The peer's own handshake still succeeds locally; it only learns about the refusal when the socket closes.
        HandshakeOutcome peer = await responderTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(peer.Keys.SendKey);
    }

    [Fact]
    public async Task Run_RejectsTamperedProofSignature()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "123456");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "123456");

        (MemoryDuplex a, MemoryDuplex b) = MemoryDuplex.CreatePair();
        var tampering = new MutatingDuplex(b, payload => MutateProof(payload, proof =>
        {
            proof.Signature[^1] ^= 0x01;
            return proof;
        }));

        Task<HandshakeOutcome> initiatorTask = initiator.RunAsync(a, _ => true);
        Task<HandshakeOutcome> responderTask = responder.RunAsync(tampering, _ => true);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => initiatorTask);
        Assert.Equal(ProtocolError.SignatureFailed, error.Error);

        // The responder never learns about the tampering: its own exchange succeeded locally.
        HandshakeOutcome peer = await responderTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(peer.Keys.SendKey);
    }

    [Fact]
    public async Task Run_RejectsProofThatEchoesADifferentOffer()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        using LocalHandshake initiator = LocalHandshake.Create(identityA, HandshakeRole.Initiator, "Win-A", "123456");
        using LocalHandshake responder = LocalHandshake.Create(identityB, HandshakeRole.Responder, "Win-B", "123456");

        (MemoryDuplex a, MemoryDuplex b) = MemoryDuplex.CreatePair();
        var tampering = new MutatingDuplex(b, payload => MutateProof(payload, proof =>
        {
            // Same key material, different name: the echoed offer no longer matches the one received first.
            HandshakeOffer swapped = new(
                proof.Offer.Version,
                proof.Offer.Role,
                "Evil-B",
                proof.Offer.IdentityPublicKeyDer,
                proof.Offer.EphemeralPublicKey,
                proof.Offer.Nonce,
                proof.Offer.SessionCode);
            return proof with { Offer = swapped };
        }));

        Task<HandshakeOutcome> initiatorTask = initiator.RunAsync(a, _ => true);
        Task<HandshakeOutcome> responderTask = responder.RunAsync(tampering, _ => true);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => initiatorTask);
        Assert.Equal(ProtocolError.InvalidTranscript, error.Error);

        HandshakeOutcome peer = await responderTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(peer.Keys.SendKey);
    }

    [Fact]
    public async Task Run_RejectsSmallOrderEphemeralKey()
    {
        using IdentityKey localIdentity = IdentityKey.Create();
        using IdentityKey peerIdentity = IdentityKey.Create();
        using LocalHandshake local = LocalHandshake.Create(localIdentity, HandshakeRole.Initiator, "Win-A", "123456");

        // A peer offer whose ephemeral key is all zeros would yield an all-zero shared secret (§3.9).
        var peerOffer = new HandshakeOffer(
            HandshakeOffer.CurrentVersion,
            HandshakeRole.Responder,
            "Win-B",
            peerIdentity.SubjectPublicKeyInfo,
            new byte[32],
            [.. Enumerable.Repeat((byte)0x44, 32)],
            "123456");
        HandshakeTranscript transcript = HandshakeTranscript.Create(local.Offer, peerOffer);
        byte[] peerSignature = peerIdentity.Sign(transcript.Encode());

        var scripted = new ScriptedDuplex(
            HandshakeMessages.EncodeOfferJson(peerOffer),
            HandshakeMessages.EncodeProofJson(peerOffer, peerSignature));

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => local.RunAsync(scripted, _ => true));
        Assert.Equal(ProtocolError.InvalidOffer, error.Error);
    }

    [Fact]
    public void Create_RejectsMissingIdentityAndBadDeviceName()
    {
        using IdentityKey identity = IdentityKey.Create();

        Assert.Throws<ArgumentNullException>(() => LocalHandshake.Create(null!, HandshakeRole.Initiator, "x"));
        AssertError(ProtocolError.InvalidOffer, () => LocalHandshake.Create(identity, HandshakeRole.Initiator, ""));
        AssertError(ProtocolError.InvalidOffer, () => LocalHandshake.Create(identity, HandshakeRole.Initiator, "x", "12345"));
    }

    [Fact]
    public async Task Run_AfterDispose_Throws()
    {
        using IdentityKey identity = IdentityKey.Create();
        LocalHandshake handshake = LocalHandshake.Create(identity, HandshakeRole.Initiator, "Win-A");
        handshake.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => handshake.RunAsync(new ScriptedDuplex(), _ => true));
    }

    private static byte[] MutateProof(byte[] payload, Func<HandshakeProof, HandshakeProof> mutate)
    {
        try
        {
            HandshakeProof proof = HandshakeMessages.DecodeProof(payload);
            HandshakeProof mutated = mutate(proof);
            return HandshakeMessages.EncodeProofJson(mutated.Offer, mutated.Signature);
        }
        catch (ProtocolException)
        {
            // Offers are left untouched; only proof messages are interesting here.
            return payload;
        }
    }

    private static void AssertError(ProtocolError expected, Action action)
    {
        ProtocolException error = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expected, error.Error);
    }

    private static string ToHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();

    /// <summary>Two connected in-memory channels, one message queue per direction.</summary>
    private sealed class MemoryDuplex : IHandshakeDuplex
    {
        private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
        private MemoryDuplex? _peer;

        public static (MemoryDuplex A, MemoryDuplex B) CreatePair()
        {
            var a = new MemoryDuplex();
            var b = new MemoryDuplex();
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            => _peer!._inbox.Writer.WriteAsync(HandshakeMessages.Unframe(frame.Span).ToArray(), cancellationToken);

        public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_inbox.Reader.TryRead(out byte[]? payload))
                {
                    return payload;
                }
            }

            throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
        }
    }

    /// <summary>Applies <c>transform</c> to outgoing payloads, simulating a tampering peer.</summary>
    private sealed class MutatingDuplex : IHandshakeDuplex
    {
        private readonly IHandshakeDuplex _inner;
        private readonly Func<byte[], byte[]> _transform;

        public MutatingDuplex(IHandshakeDuplex inner, Func<byte[], byte[]> transform)
        {
            _inner = inner;
            _transform = transform;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            byte[] payload = HandshakeMessages.Unframe(frame.Span).ToArray();
            return _inner.WriteAsync(HandshakeMessages.Frame(_transform(payload)), cancellationToken);
        }

        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
            => _inner.ReadAsync(cancellationToken);
    }

    /// <summary>Replays a fixed list of payloads and swallows writes, for driving one side without a peer.</summary>
    private sealed class ScriptedDuplex : IHandshakeDuplex
    {
        private readonly Queue<byte[]> _payloads;

        public ScriptedDuplex(params byte[][] payloads) => _payloads = new Queue<byte[]>(payloads);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_payloads.Count == 0)
            {
                throw new ProtocolException(ProtocolError.UnexpectedEndOfStream);
            }

            return ValueTask.FromResult(_payloads.Dequeue());
        }
    }
}
