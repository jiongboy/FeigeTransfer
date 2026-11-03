using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;
using Feige.Core.Crypto;
using Feige.Core.Storage;

namespace Feige.Core.Tests;

public sealed class ResumableTransferTests : IDisposable
{
    // Fixed identities so receiver storage keys survive reconnects, as they do between real devices.
    private static readonly IdentityKey Sender = IdentityKey.Create();
    private static readonly IdentityKey Receiver = IdentityKey.Create();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "feige-v6-" + Guid.NewGuid().ToString("N"));
    private string SendDirectory => Path.Combine(_root, "Send");
    private string ReceiveDirectory => Path.Combine(_root, "Receive");

    public ResumableTransferTests()
    {
        Directory.CreateDirectory(SendDirectory);
        Directory.CreateDirectory(ReceiveDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Pair : IAsyncDisposable
    {
        public required V4Connection Sender { get; init; }
        public required V4Connection Receiver { get; init; }
        public required TcpClient Client { get; init; }
        public required TcpClient Accepted { get; init; }
        public List<V3File> Prompts { get; } = [];
        public List<(V3File File, string Path)> Deliveries { get; } = [];
        public Task[] Loops { get; set; } = [];

        public async ValueTask DisposeAsync()
        {
            Sender.Close(); Receiver.Close();
            foreach (Task loop in Loops) { try { await loop; } catch (Exception) { } }
            Client.Dispose(); Accepted.Dispose();
        }
    }

    private async Task<Pair> Connect(ushort version = V4Offer.ResumableVersion)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient(AddressFamily.InterNetwork);
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        Task<V4Connection> responder = V4Connection.EstablishAsync(accepted.GetStream(), Receiver, false, "Win-B",
            (_, _) => Task.FromResult(true));
        V4Connection initiator = await V4Connection.EstablishAsync(client.GetStream(), Sender, true, "Mac-A",
            (_, _) => throw new InvalidOperationException("initiator is not asked"), protocolVersion: version);
        var pair = new Pair { Sender = initiator, Receiver = await responder, Client = client, Accepted = accepted };
        pair.Receiver.FileReceiving = file => { lock (pair.Prompts) pair.Prompts.Add(file); return Task.FromResult(true); };
        pair.Receiver.FileReceived = (file, path) => { lock (pair.Deliveries) pair.Deliveries.Add((file, path)); return Task.CompletedTask; };
        string receive = ReceiveDirectory;
        pair.Loops = [Task.Run(() => pair.Receiver.RunAsync(() => receive)), Task.Run(() => pair.Sender.RunAsync())];
        return pair;
    }

    private string PartPath(string source, string receiverPeer)
    {
        var info = new FileInfo(source);
        Guid id = ResumeIdentifiers.ResumeID(source, info.Length, info.LastWriteTimeUtc);
        return Path.Combine(ReceiveDirectory, $".feige-resume-{ResumeIdentifiers.StorageKey(receiverPeer, id)}.part");
    }

    private static byte[] Bytes(int count, byte seed = 1)
        => Enumerable.Range(0, count).Select(i => unchecked((byte)(i * 31 + seed))).ToArray();

    [Fact]
    public async Task SendResumesFromRetainedPrefix()
    {
        byte[] content = Bytes(300_000);
        string source = Path.Combine(SendDirectory, "big.bin");
        await File.WriteAllBytesAsync(source, content);
        await using var pair = await Connect();
        await File.WriteAllBytesAsync(PartPath(source, pair.Receiver.PeerFingerprint), content[..120_000]);
        var offsets = new List<long>();
        V3File sent = await pair.Sender.SendFileAsync(source, progress: (count, _) => { lock (offsets) offsets.Add(count); });
        Assert.NotNull(sent.ResumeID);
        Assert.Equal(120_000, offsets[0]);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(ReceiveDirectory, "big.bin")));
        Assert.False(File.Exists(PartPath(source, pair.Receiver.PeerFingerprint)));
    }

    [Fact]
    public async Task CorruptPrefixFailsOnceAndThenRestartsFromZero()
    {
        byte[] content = Bytes(200_000);
        string source = Path.Combine(SendDirectory, "c.bin");
        await File.WriteAllBytesAsync(source, content);
        string part;
        await using (var pair = await Connect())
        {
            part = PartPath(source, pair.Receiver.PeerFingerprint);
            await File.WriteAllBytesAsync(part, Bytes(50_000, 9));
            await Assert.ThrowsAnyAsync<Exception>(() => pair.Sender.SendFileAsync(source));
        }
        Assert.False(File.Exists(part), "a mismatching partial file is deleted");
        Assert.False(File.Exists(Path.Combine(ReceiveDirectory, "c.bin")));
        await using (var pair = await Connect())
        {
            await pair.Sender.SendFileAsync(source);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(ReceiveDirectory, "c.bin")));
    }

    [Fact]
    public async Task OversizedPartialIsDiscarded()
    {
        byte[] content = Bytes(10_000);
        string source = Path.Combine(SendDirectory, "s.bin");
        await File.WriteAllBytesAsync(source, content);
        await using var pair = await Connect();
        await File.WriteAllBytesAsync(PartPath(source, pair.Receiver.PeerFingerprint), Bytes(20_000, 3));
        var offsets = new List<long>();
        await pair.Sender.SendFileAsync(source, progress: (count, _) => { lock (offsets) offsets.Add(count); });
        Assert.Equal(0, offsets[0]);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(ReceiveDirectory, "s.bin")));
    }

    private string MakeFolder()
    {
        string folder = Path.Combine(SendDirectory, "Photos");
        string nested = Path.Combine(folder, "2026", "May");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(folder, "a.jpg"), Bytes(70_000));
        File.WriteAllBytes(Path.Combine(folder, "empty.txt"), []);
        File.WriteAllBytes(Path.Combine(nested, "b.jpg"), Bytes(90_000, 5));
        File.WriteAllText(Path.Combine(folder, "Thumbs.db"), "x");
        string outside = Path.Combine(SendDirectory, "secret.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), outside);
        return folder;
    }

    [Fact]
    public async Task FolderIsApprovedOnceAndNeverMergedIntoExistingFolder()
    {
        string folder = MakeFolder();
        Directory.CreateDirectory(Path.Combine(ReceiveDirectory, "Photos"));
        await using var pair = await Connect();
        FolderSendResult result = await pair.Sender.SendFolderAsync(folder);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(160_000, result.TotalBytes);
        Assert.Equal(0, result.SkippedFiles);
        string received = Path.Combine(ReceiveDirectory, "Photos (1)");
        Assert.Equal(Bytes(70_000), await File.ReadAllBytesAsync(Path.Combine(received, "a.jpg")));
        Assert.Equal(Bytes(90_000, 5), await File.ReadAllBytesAsync(Path.Combine(received, "2026", "May", "b.jpg")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(received, "empty.txt")));
        Assert.False(File.Exists(Path.Combine(received, "link.txt")));
        Assert.False(File.Exists(Path.Combine(received, "Thumbs.db")));
        Assert.Single(pair.Prompts);
        Assert.Equal("Photos", pair.Prompts[0].DisplayName);
        Assert.Equal(160_000, pair.Prompts[0].DisplaySize);
        var delivery = Assert.Single(pair.Deliveries);
        Assert.Equal(received, delivery.Path);
        Assert.Empty(Directory.GetFiles(ReceiveDirectory, ".feige-*"));
    }

    [Fact]
    public async Task FolderResumeSkipsCompletedFiles()
    {
        string folder = MakeFolder();
        string first = Path.Combine(folder, "2026", "May", "b.jpg");
        Guid completed = ResumeIdentifiers.ResumeID(first, 90_000, new FileInfo(first).LastWriteTimeUtc);
        await using var pair = await Connect();
        string key = ResumeIdentifiers.StorageKey(pair.Receiver.PeerFingerprint, ResumeIdentifiers.FolderID(folder));
        string existing = Path.Combine(ReceiveDirectory, "Photos");
        Directory.CreateDirectory(Path.Combine(existing, "2026", "May"));
        await File.WriteAllBytesAsync(Path.Combine(existing, "2026", "May", "b.jpg"), Bytes(90_000, 5));
        await File.WriteAllTextAsync(Path.Combine(ReceiveDirectory, $".feige-folder-{key}.state"), $"Photos\n{completed:D}\n");
        FolderSendResult result = await pair.Sender.SendFolderAsync(folder);
        Assert.Equal(1, result.SkippedFiles);
        string[] names = Directory.GetFiles(existing, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(existing, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["2026/May/b.jpg", "a.jpg", "empty.txt"], names);
    }

    [Fact]
    public async Task OlderChannelsRefuseFoldersAndOmitV6Fields()
    {
        string folder = MakeFolder();
        await using var pair = await Connect(V4Offer.DurableAbortVersion);
        Assert.False(pair.Sender.SupportsResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Sender.SendFolderAsync(folder));
        V3File sent = await pair.Sender.SendFileAsync(Path.Combine(folder, "a.jpg"));
        Assert.Null(sent.ResumeID);
        string json = System.Text.Encoding.UTF8.GetString(V3Wire.Encode(sent));
        Assert.DoesNotContain("resumeID", json);
        Assert.DoesNotContain("folder", json);
    }

    [Fact]
    public void FolderInfoRejectsTraversalAndLimits()
    {
        Guid id = Guid.NewGuid();
        V6FolderInfo Info(string path, string root = "R", int count = 1) => new(root, path, id, count, 10);
        Info("a/b.txt").Validate();
        foreach (string bad in new[] { "../a", "a/../b", "/a", "a//b", "a/./b", "a\\b", "a/\u0001", "" })
            Assert.ThrowsAny<Exception>(() => Info(bad).Validate());
        Assert.ThrowsAny<Exception>(() => Info("a", root: "..").Validate());
        Assert.ThrowsAny<Exception>(() => Info("a", count: 0).Validate());
        Assert.ThrowsAny<Exception>(() => Info("a", count: 10_001).Validate());
        Assert.ThrowsAny<Exception>(() => Info(string.Join('/', Enumerable.Repeat("d", 33))).Validate());
        Assert.ThrowsAny<Exception>(() => Info(string.Concat(Enumerable.Repeat("a/", 400)) + "b").Validate());
        Assert.ThrowsAny<Exception>(() => new V3File(Guid.NewGuid(), "x.txt", 1, "file", Guid.NewGuid(), Info("a/b.txt")).Validate());
        Assert.ThrowsAny<Exception>(() => new V3File(Guid.NewGuid(), "b.txt", 11, "file", Guid.NewGuid(), Info("a/b.txt")).Validate());
        Assert.ThrowsAny<Exception>(() => new V3File(Guid.NewGuid(), "b.txt", 1, "file", null, Info("a/b.txt")).Validate());
    }

    [Fact]
    public void MacDecisionAndManifestJsonDecode()
    {
        var manifest = V3Wire.Decode<V3File>(System.Text.Encoding.UTF8.GetBytes(
            """{"transferID":"9B0F3C2E-2A47-4D7E-8C66-0F1D2E3A4B5C","fileName":"a.jpg","size":3,"kind":"file","resumeID":"1B0F3C2E-2A47-5D7E-8C66-0F1D2E3A4B5C","folder":{"rootName":"P","relativePath":"x/a.jpg","folderID":"2B0F3C2E-2A47-5D7E-8C66-0F1D2E3A4B5C","fileCount":2,"totalBytes":9}}"""));
        manifest.Validate();
        Assert.Equal("x/a.jpg", manifest.Folder!.RelativePath);
        Assert.Equal("P", manifest.DisplayName);
    }

    [Fact]
    public void ExpiredResumeStorageIsRemoved()
    {
        string part = Path.Combine(ReceiveDirectory, ".feige-resume-0123.part");
        string state = Path.Combine(ReceiveDirectory, ".feige-folder-0123.state");
        string fresh = Path.Combine(ReceiveDirectory, ".feige-resume-4567.part");
        string user = Path.Combine(ReceiveDirectory, "keep.part");
        foreach (string path in new[] { part, state, fresh, user }) File.WriteAllText(path, "x");
        DateTime old = DateTime.UtcNow.AddDays(-8);
        foreach (string path in new[] { part, state, user }) File.SetLastWriteTimeUtc(path, old);
        var removed = ResumableStorageCleanup.RemoveExpired(ReceiveDirectory);
        Assert.Equal(new[] { Path.GetFileName(part), Path.GetFileName(state) }.Order(), removed.Order());
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(user));
    }
}
