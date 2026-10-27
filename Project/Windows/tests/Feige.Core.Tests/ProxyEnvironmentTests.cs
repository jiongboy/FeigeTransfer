using System.Net;
using System.Net.Sockets;
using Feige.Core.Crypto;
using Feige.Core.Net;

namespace Feige.Core.Tests;

[CollectionDefinition("Isolated proxy environment", DisableParallelization = true)]
public sealed class ProxyEnvironmentCollection { }

[Collection("Isolated proxy environment")]
public sealed class ProxyEnvironmentTests
{
    [Fact] public async Task HttpAndSocksProxyEnvironment_DoesNotInterceptLanTcpOrItsReceipt()
    {
        string[] keys = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];
        var before = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        using var a = IdentityKey.Create(); using var b = IdentityKey.Create();
        await using var listener = new V3PeerListener(new IPEndPoint(IPAddress.Loopback, 0)); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
        try
        {
            int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
            foreach (string key in keys)
                Environment.SetEnvironmentVariable(key, key.Contains("no_proxy", StringComparison.OrdinalIgnoreCase) ? "" :
                    key.Contains("all_proxy", StringComparison.OrdinalIgnoreCase) ? $"socks5://127.0.0.1:{proxyPort}" : $"http://127.0.0.1:{proxyPort}");
            var accepting = listener.AcceptAsync(new LocalDevice(b, "B"), (_, _) => Task.FromResult(true), ct);
            await using var sender = await V3PeerConnector.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.Port), new LocalDevice(a, "A"), ct);
            await using var receiver = await accepting;
            string? received = null; receiver.TextReceived = m => { received = m.Text; return Task.CompletedTask; };
            var runA = sender.RunAsync(() => throw new InvalidOperationException(), ct);
            var runB = receiver.RunAsync(() => throw new InvalidOperationException(), ct);
            await sender.SendTextAsync("直接 TCP 回执", cancellationToken: ct);
            Assert.Equal("直接 TCP 回执", received); Assert.False(proxy.Pending());
            sender.Close(); receiver.Close();
            foreach (var run in new[] { runA, runB })
                try { await run; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, before[key]);
            proxy.Stop();
        }
    }
}
