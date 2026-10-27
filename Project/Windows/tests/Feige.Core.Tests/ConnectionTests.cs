using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Crypto;
using Feige.Core.Net;
using Feige.Core.Protocol;
using Xunit;

namespace Feige.Core.Tests;

/// <summary>
/// Connection assembly: <see cref="PeerListener"/> + <see cref="PeerConnector"/> performing a real handshake over a real
/// loopback socket, then carrying a real file, plus the local timeout and one-transfer-per-connection rules.
/// </summary>
public sealed class ConnectionTests : IDisposable
{
    private const string Code = "123456";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-connection-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Connect_EstablishesMutuallyAuthenticatedSession()
    {
        await using LoopbackPair pair = await LoopbackPair.CreateAsync("123456").WaitAsync(TimeSpan.FromSeconds(30));

        // Both sides must have agreed on the same SAS and on each other's fingerprint (§3.10, §3.6).
        Assert.Equal(pair.Initiator.Sas, pair.Responder.Sas);
        Assert.Equal("Win-B", pair.Initiator.PeerDeviceName);
        Assert.Equal("Win-A", pair.Responder.PeerDeviceName);
        Assert.Equal(pair.ResponderIdentity.Fingerprint, pair.Initiator.PeerFingerprint);
        Assert.Equal(pair.InitiatorIdentity.Fingerprint, pair.Responder.PeerFingerprint);

        // Same transcript bytes, mirrored keys — proof that the record layer will interoperate.
        Assert.Equal(
            Convert.ToHexString(pair.Initiator.Handshake.Transcript.Encode()),
            Convert.ToHexString(pair.Responder.Handshake.Transcript.Encode()));
        Assert.Equal(
            Convert.ToHexString(pair.Initiator.Handshake.Keys.SendKey),
            Convert.ToHexString(pair.Responder.Handshake.Keys.ReceiveKey));

        // §3.4: the roles must be opposite, which each side sees in the peer's offer.
        Assert.Equal(HandshakeRole.Responder, pair.Initiator.Handshake.Peer.Role);
        Assert.Equal(HandshakeRole.Initiator, pair.Responder.Handshake.Peer.Role);
        Assert.NotNull(pair.Initiator.RemoteEndPoint);
        Assert.False(pair.Initiator.TransferStarted);
    }

    [Fact]
    public async Task Session_SendsFileFromInitiatorToResponder()
    {
        await using LoopbackPair pair = await LoopbackPair.CreateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        string source = CreateSourceFile("report.bin", 200_000);
        string inbox = Path.Combine(_root, "inbox");

        Task send = pair.Initiator.SendFileAsync(source);
        Task<ReceivedFile> receive = pair.Responder.ReceiveFileAsync(inbox, manifest =>
        {
            Assert.Equal("report.bin", manifest.FileName);
            Assert.Equal(200_000, manifest.Size);
            return true;
        });

        await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(30));

        ReceivedFile file = await receive;
        Assert.Equal(Path.Combine(inbox, "report.bin"), file.Path);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(file.Path));
        Assert.True(pair.Initiator.TransferStarted);
        Assert.True(pair.Responder.TransferStarted);
    }

    [Fact]
    public async Task Session_SendsFileFromResponderToInitiator()
    {
        await using LoopbackPair pair = await LoopbackPair.CreateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // §1: the handshake role does not decide who sends.
        string source = CreateSourceFile("from-responder.bin", 70_001);
        string inbox = Path.Combine(_root, "inbox");

        Task send = pair.Responder.SendFileAsync(source);
        Task<ReceivedFile> receive = pair.Initiator.ReceiveFileAsync(inbox, _ => true);

        await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync((await receive).Path));
    }

    [Fact]
    public async Task Session_RefusesSecondTransferOnSameConnection()
    {
        await using LoopbackPair pair = await LoopbackPair.CreateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        string source = CreateSourceFile("once.bin", 4096);
        Task send = pair.Initiator.SendFileAsync(source);
        Task<ReceivedFile> receive = pair.Responder.ReceiveFileAsync(Path.Combine(_root, "inbox"), _ => true);
        await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(30));

        string second = CreateSourceFile("twice.bin", 16);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Initiator.SendFileAsync(second));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pair.Responder.ReceiveFileAsync(Path.Combine(_root, "inbox2"), _ => true));
    }

    [Fact]
    public async Task Session_ReceiptIsSentOnlyAfterTheFileIsCommitted()
    {
        await using LoopbackPair pair = await LoopbackPair.CreateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        string source = CreateSourceFile("commit.bin", 90_000);
        string inbox = Path.Combine(_root, "inbox");

        Task send = pair.Initiator.SendFileAsync(source);
        ReceivedFile received = await pair.Responder.ReceiveFileAsync(inbox, _ => true);
        await send.WaitAsync(TimeSpan.FromSeconds(30));

        // The sender only returns after the receiver's receipt, so by now the committed file must be on disk.
        Assert.True(File.Exists(received.Path));
        Assert.Equal(90_000, new FileInfo(received.Path).Length);
        Assert.Empty(Directory.GetFiles(inbox, ".feige-*.tmp"));
    }

    [Fact]
    public async Task Listener_RejectsMismatchedPairingCodeOnBothSides()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        var initiator = new LocalDevice(identityA, "Win-A");
        var responder = new LocalDevice(identityB, "Win-B");

        await using var listener = new PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();

        Task<PeerSession> accept = listener.AcceptAsync(responder, "111111", _ => true).AsTask();
        Task<PeerSession> connect = PeerConnector.ConnectAsync(
            new PeerTarget(new IPEndPoint(IPAddress.Loopback, listener.Port)), initiator, "222222", _ => true);

        // Both sides write their offer before reading, so both detect the mismatch locally.
        ProtocolException fromInitiator = await Assert.ThrowsAsync<ProtocolException>(() => connect);
        ProtocolException fromResponder = await Assert.ThrowsAsync<ProtocolException>(() => accept);
        Assert.Equal(ProtocolError.PairingCodeMismatch, fromInitiator.Error);
        Assert.Equal(ProtocolError.PairingCodeMismatch, fromResponder.Error);
    }

    [Fact]
    public async Task Listener_RejectsUserDeclinedHandshake()
    {
        using IdentityKey identityA = IdentityKey.Create();
        using IdentityKey identityB = IdentityKey.Create();
        var initiator = new LocalDevice(identityA, "Win-A");
        var responder = new LocalDevice(identityB, "Win-B");

        await using var listener = new PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();

        Task<PeerSession> accept = listener.AcceptAsync(responder, Code, _ => false).AsTask();
        Task<PeerSession> connect = PeerConnector.ConnectAsync(
            new PeerTarget(new IPEndPoint(IPAddress.Loopback, listener.Port)), initiator, Code, _ => true);

        ProtocolException declined = await Assert.ThrowsAsync<ProtocolException>(() => accept);
        Assert.Equal(ProtocolError.UserRejected, declined.Error);

        // The peer finished its own handshake before the socket closed, so it cannot know about the refusal.
        PeerSession peer = await connect.WaitAsync(TimeSpan.FromSeconds(20));
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Listener_TimesOutOnSilentPeer()
    {
        var options = ConnectionOptions.Default with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) };

        await using var listener = new PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();

        using IdentityKey identity = IdentityKey.Create();
        var local = new LocalDevice(identity, "Win-B");

        Task<PeerSession> accept = listener.AcceptAsync(local, Code, _ => true, options).AsTask();

        // A peer that connects and then says nothing: the listener writes its offer and waits for a reply.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, listener.Port);

        ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => accept.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(ProtocolError.Timeout, error.Error);

        // One bad peer must not kill the listener: a well-behaved handshake still works afterwards.
        using IdentityKey peerIdentity = IdentityKey.Create();
        var peerLocal = new LocalDevice(peerIdentity, "Win-A");
        Task<PeerSession> second = listener.AcceptAsync(local, Code, _ => true).AsTask();
        PeerSession connected = await PeerConnector.ConnectAsync(
            new PeerTarget(new IPEndPoint(IPAddress.Loopback, listener.Port)), peerLocal, Code, _ => true);
        await using PeerSession accepted = await second.WaitAsync(TimeSpan.FromSeconds(20));
        await connected.DisposeAsync();
    }

    [Fact]
    public async Task Listener_StopsWaitingWhenCancelled()
    {
        await using var listener = new PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();

        using IdentityKey identity = IdentityKey.Create();
        var local = new LocalDevice(identity, "Win-B");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => listener.AcceptAsync(local, Code, _ => true, cancellationToken: cts.Token).AsTask());
    }

    [Fact]
    public async Task Connect_ToClosedPortEndsWithinTheConnectBudget()
    {
        // Reserve a port, then release it, so nothing is listening there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using IdentityKey identity = IdentityKey.Create();
        var local = new LocalDevice(identity, "Win-A");
        var options = ConnectionOptions.Default with { ConnectTimeout = TimeSpan.FromSeconds(10) };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Exception? error = await Record.ExceptionAsync(() => PeerConnector.ConnectAsync(
            new PeerTarget(new IPEndPoint(IPAddress.Loopback, port)),
            local,
            Code,
            _ => true,
            options));
        stopwatch.Stop();

        // Windows can take a couple of seconds to refuse a loopback connect (SYN retry), so either the OS error or the
        // local timeout is acceptable — what must not happen is waiting past the budget.
        Assert.NotNull(error);
        Assert.True(
            error is SocketException || (error is ProtocolException protocol && protocol.Error == ProtocolError.Timeout),
            $"Unexpected failure type: {error.GetType().Name}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Connect took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Session_IsSafeToDisposeTwiceAndRejectsUseAfterDispose()
    {
        LoopbackPair pair = await LoopbackPair.CreateAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await pair.DisposeAsync();
        await pair.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pair.Initiator.SendFileAsync(CreateSourceFile("x.bin", 8)));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => pair.Responder.ReceiveFileAsync(Path.Combine(_root, "inbox"), _ => true));
    }

    [Fact]
    public async Task TimeoutStream_TurnsAStalledReadIntoATimeoutError()
    {
        // A raw listener: this test is about the stream wrapper, not about the handshake.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using TcpClient server = await listener.AcceptTcpClientAsync();

            await using var stream = new TimeoutStream(client.GetStream(), TimeSpan.FromMilliseconds(200));
            ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(
                () => stream.ReadAsync(new byte[16]).AsTask());
            Assert.Equal(ProtocolError.Timeout, error.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HandshakeDuplex_MapsPeerAbortToUnexpectedEndOfStream()
    {
        // A peer that disappears mid-handshake shows up as a socket error (10053/10054), not as a clean EOF. Callers
        // must see a protocol error: the raw exception used to escape into the app's receive loop.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using TcpClient server = await listener.AcceptTcpClientAsync();

            // Zero linger turns Close into an abort (RST) instead of an orderly FIN.
            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            client.Close();
            await Task.Delay(100);

            var duplex = new StreamHandshakeDuplex(server.GetStream());
            ProtocolException error = await Assert.ThrowsAsync<ProtocolException>(() => duplex.ReadAsync().AsTask());

            Assert.Equal(ProtocolError.UnexpectedEndOfStream, error.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ConnectionOptions_RejectNonsenseTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionOptions { ConnectTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionOptions { HandshakeTimeout = TimeSpan.FromHours(2) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutStream(Stream.Null, TimeSpan.FromSeconds(-1)));

        ConnectionOptions.Default.Validate();
    }

    [Fact]
    public void PeerTarget_RejectsWildcardAndBadPort()
    {
        Assert.Throws<ArgumentException>(() => new PeerTarget(new IPEndPoint(IPAddress.Any, 1234)).Validate());
        Assert.Throws<ArgumentException>(() => new PeerTarget(new IPEndPoint(IPAddress.IPv6Any, 1234)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerTarget(new IPEndPoint(IPAddress.Loopback, 0)).Validate());

        new PeerTarget(new IPEndPoint(IPAddress.Loopback, 51_234)).Validate();
    }

    [Fact]
    public void LocalDevice_ValidatesTheAnnouncedName()
    {
        using IdentityKey identity = IdentityKey.Create();

        Assert.Throws<ProtocolException>(() => new LocalDevice(identity, "   "));
        Assert.Throws<ProtocolException>(() => new LocalDevice(identity, new string('x', 129)));
        Assert.Throws<ArgumentNullException>(() => new LocalDevice(null!, "Win-A"));

        var device = new LocalDevice(identity, "  Win-A  ");
        Assert.Equal("Win-A", device.DeviceName);
        Assert.Equal(identity.Fingerprint, device.Fingerprint);

        string suggested = LocalDevice.SuggestDeviceName();
        Assert.NotEmpty(suggested);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(suggested) <= HandshakeOffer.MaxDeviceNameBytes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the test scratch directory.
        }
    }

    private string CreateSourceFile(string name, int size)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }

    /// <summary>A listener plus one already-handshaken initiator and responder session on loopback.</summary>
    private sealed class LoopbackPair : IAsyncDisposable
    {
        private readonly PeerListener _listener;

        private LoopbackPair(
            PeerListener listener,
            IdentityKey initiatorIdentity,
            IdentityKey responderIdentity,
            PeerSession initiator,
            PeerSession responder)
        {
            _listener = listener;
            InitiatorIdentity = initiatorIdentity;
            ResponderIdentity = responderIdentity;
            Initiator = initiator;
            Responder = responder;
        }

        public PeerSession Initiator { get; }

        public PeerSession Responder { get; }

        public IdentityKey InitiatorIdentity { get; }

        public IdentityKey ResponderIdentity { get; }

        /// <summary>Runs both handshakes concurrently; the connect side dials the listener's assigned port.</summary>
        public static async Task<LoopbackPair> CreateAsync(
            string sessionCode = Code,
            ConnectionOptions? options = null,
            Func<HandshakePrompt, bool>? approveInitiator = null,
            Func<HandshakePrompt, bool>? approveResponder = null)
        {
            var listener = new PeerListener(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Start();

            IdentityKey identityA = IdentityKey.Create();
            IdentityKey identityB = IdentityKey.Create();
            var localA = new LocalDevice(identityA, "Win-A");
            var localB = new LocalDevice(identityB, "Win-B");

            Task<PeerSession> accept = listener
                .AcceptAsync(localB, sessionCode, approveResponder ?? (_ => true), options)
                .AsTask();

            try
            {
                PeerSession initiator = await PeerConnector.ConnectAsync(
                    new PeerTarget(new IPEndPoint(IPAddress.Loopback, listener.Port)),
                    localA,
                    sessionCode,
                    approveInitiator ?? (_ => true),
                    options).WaitAsync(TimeSpan.FromSeconds(20));

                PeerSession responder = await accept.WaitAsync(TimeSpan.FromSeconds(20));
                return new LoopbackPair(listener, identityA, identityB, initiator, responder);
            }
            catch
            {
                identityA.Dispose();
                identityB.Dispose();
                await listener.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Initiator.DisposeAsync();
            await Responder.DisposeAsync();
            await _listener.DisposeAsync();

            // Sessions and identities are independent after the handshake, so the keys can go now.
            InitiatorIdentity.Dispose();
            ResponderIdentity.Dispose();
        }
    }
}
