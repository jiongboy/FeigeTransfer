using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;

namespace Feige.Core.Net;

/// <summary>Direct LAN sockets do not use HTTP/PAC/SOCKS proxy settings; system routes still apply.</summary>
public static class V3PeerConnector
{
    public const int MaxCandidates = 16;
    public static IPEndPoint ParseTarget(string address, int port)
    {
        if (port is < 1 or > 65535 || !IPAddress.TryParse(address.Trim(), out var ip))
            throw new ArgumentException("请输入局域网数字 IP 和 1–65535 的端口。");
        var endpoint = new IPEndPoint(ip, port); RequireLocal(endpoint); return endpoint;
    }

    public static Task<V3Connection> ConnectAsync(IPEndPoint target, LocalDevice local, CancellationToken cancellationToken = default)
        => ConnectAsync(new[] { target }, local, cancellationToken);

    /// <summary>Race bounded TCP candidates, then perform exactly one authenticated handshake and consent exchange.</summary>
    public static async Task<V3Connection> ConnectAsync(IReadOnlyList<IPEndPoint> candidates, LocalDevice local,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > MaxCandidates) throw new ArgumentException("没有可用地址，或候选地址过多，请刷新设备后重试。");
        foreach (var endpoint in candidates) RequireLocal(endpoint);
        var targets = candidates.Distinct().ToArray();
        using var dial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dial.CancelAfter(TimeSpan.FromSeconds(10));
        var pending = targets.Select((target, index) => DialAsync(target, TimeSpan.FromMilliseconds(index * 200), dial.Token)).ToList();
        TcpClient? client = null;
        Exception? error = null;
        try
        {
            while (pending.Count > 0 && client is null)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false); pending.Remove(completed);
                try { client = await completed.ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException) { error = ex; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (client is null)
            {
                if (dial.IsCancellationRequested) throw new TimeoutException("无法建立直连，请检查对方地址、路由、VPN/TUN 和防火墙。", error);
                throw error ?? new IOException("没有可连接的局域网地址。");
            }
        }
        finally
        {
            // Never let a losing TCP connection become a second handshake or approval request.
            await dial.CancelAsync().ConfigureAwait(false);
            foreach (var task in pending)
                try { (await task.ConfigureAwait(false)).Dispose(); }
                catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested) client?.Dispose();
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireLocal((IPEndPoint)client.Client.RemoteEndPoint!);
            // A rejection, invalid identity or handshake failure must not trigger another candidate/approval.
            return await V3Connection.EstablishAsync(client.GetStream(), local.Identity, true, local.DeviceName,
                (_, _) => throw new InvalidOperationException("发起方无需再次同意自己发起的连接。"), cancellationToken).ConfigureAwait(false);
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<TcpClient> DialAsync(IPEndPoint target, TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        var client = new TcpClient(target.AddressFamily) { NoDelay = true };
        Task? connecting = null;
        try
        {
            // Keep an immediate SocketException (e.g. refused), and close pending sockets on cancellation.
            connecting = client.ConnectAsync(target);
            await connecting.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            client.Dispose();
            if (connecting is not null) _ = connecting.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }

    internal static void RequireLocal(IPEndPoint endpoint) => LanAddressPolicy.Require(endpoint);
}
