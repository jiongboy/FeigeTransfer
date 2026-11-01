using System.Net;
using System.Net.Sockets;
using Feige.Core.Conversations;

namespace Feige.Core.Net;

/// <summary>Dials local network endpoints and performs only the authenticated v4 handshake.</summary>
public static class V4PeerConnector
{
    public const int MaxCandidates = 16;

    public static IPEndPoint ParseTarget(string address, int port)
    {
        if (port is < 1 or > 65535 || !IPAddress.TryParse(address.Trim(), out IPAddress? ip))
            throw new ArgumentException("请输入局域网数字 IP 和 1–65535 的端口。");
        var endpoint = new IPEndPoint(ip, port);
        RequireLocal(endpoint);
        return endpoint;
    }

    public static Task<V4Connection> ConnectAsync(IPEndPoint target, LocalDevice local,
        CancellationToken cancellationToken = default)
        => ConnectAsync([target], local, V4Offer.CurrentVersion, cancellationToken);

    public static Task<V4Connection> ConnectAsync(IPEndPoint target, LocalDevice local, ushort protocolVersion,
        CancellationToken cancellationToken = default)
        => ConnectAsync([target], local, protocolVersion, cancellationToken);

    /// <summary>Races a bounded set of TCP candidates, then performs one authenticated group-capable handshake.</summary>
    public static Task<V4Connection> ConnectAsync(IReadOnlyList<IPEndPoint> candidates, LocalDevice local,
        CancellationToken cancellationToken = default)
        => ConnectAsync(candidates, local, V4Offer.CurrentVersion, cancellationToken);

    public static async Task<V4Connection> ConnectAsync(IReadOnlyList<IPEndPoint> candidates, LocalDevice local,
        ushort protocolVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(candidates);
        if (protocolVersion is not (V4Offer.CurrentVersion or V4Offer.DurableAbortVersion))
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        if (candidates.Count is < 1 or > MaxCandidates)
            throw new ArgumentException("没有可用地址，或候选地址过多，请刷新设备后重试。", nameof(candidates));
        foreach (IPEndPoint endpoint in candidates) RequireLocal(endpoint);

        IPEndPoint[] targets = candidates.Distinct().ToArray();
        using var dial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dial.CancelAfter(TimeSpan.FromSeconds(10));
        List<Task<TcpClient>> pending = targets
            .Select((target, index) => DialAsync(target, TimeSpan.FromMilliseconds(index * 200), dial.Token))
            .ToList();

        TcpClient? client = null;
        Exception? lastError = null;
        try
        {
            while (pending.Count > 0 && client is null)
            {
                Task<TcpClient> completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                try
                {
                    client = await completed.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
                {
                    lastError = exception;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (client is null)
            {
                if (dial.IsCancellationRequested)
                    throw new TimeoutException("无法建立直连，请检查对方地址、路由、VPN/TUN 和防火墙。", lastError);
                throw lastError ?? new IOException("没有可连接的局域网地址。");
            }
        }
        finally
        {
            // Only the winning socket proceeds to authentication or approval.
            await dial.CancelAsync().ConfigureAwait(false);
            foreach (Task<TcpClient> attempt in pending)
            {
                try { (await attempt.ConfigureAwait(false)).Dispose(); }
                catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException) { }
            }
            if (cancellationToken.IsCancellationRequested) client?.Dispose();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireLocal((IPEndPoint)client.Client.RemoteEndPoint!);
            return await V4Connection.EstablishAsync(client.GetStream(), local.Identity, initiator: true, local.DeviceName,
                (_, _) => throw new InvalidOperationException("发起方无需再次同意自己发起的连接。"), cancellationToken, protocolVersion)
                .ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static void RequireLocal(IPEndPoint endpoint) => LanAddressPolicy.Require(endpoint);

    private static async Task<TcpClient> DialAsync(IPEndPoint target, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        var client = new TcpClient(target.AddressFamily) { NoDelay = true };
        Task? connecting = null;
        try
        {
            connecting = client.ConnectAsync(target);
            await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            client.Dispose();
            if (connecting is not null)
                _ = connecting.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }
}
