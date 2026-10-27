using System.Net.Sockets;
using System.Security.Cryptography;
using Feige.Core.Net;
using Feige.Core.Protocol;

namespace Feige.Core.Tests;

public sealed class NetworkFailureAdviceTests
{
    [Theory]
    [InlineData(SocketError.TimedOut)] [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.HostUnreachable)] [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.ConnectionRefused)] [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.AccessDenied)] [InlineData(SocketError.AddressNotAvailable)]
    public void ConnectionSocketFailure_SuggestsConditionalNetworkChecks(SocketError code)
        => Assert.True(NetworkFailureAdvice.IsApplicable(new SocketException((int)code)));

    [Theory]
    [InlineData(ProtocolError.Timeout, true)] [InlineData(ProtocolError.UnexpectedEndOfStream, true)]
    [InlineData(ProtocolError.UserRejected, false)] [InlineData(ProtocolError.PeerRejected, false)]
    [InlineData(ProtocolError.SignatureFailed, false)] [InlineData(ProtocolError.FingerprintMismatch, false)]
    [InlineData(ProtocolError.InvalidHandshakeFrame, false)] [InlineData(ProtocolError.StorageFailure, false)]
    public void ProtocolFailure_DistinguishesTransportFromConsentIdentityAndStorage(ProtocolError code, bool advice)
        => Assert.Equal(advice, NetworkFailureAdvice.IsApplicable(new ProtocolException(code)));

    [Fact] public void CancellationInvalidInputAndLocalFiles_AreNotProxyDiagnoses()
    {
        Exception[] errors = [new OperationCanceledException(), new InvalidOperationException("对方拒绝了连接。"),
            new ArgumentException("无效地址"), new CryptographicException(), new InvalidDataException("握手信息无效"),
            new IOException("本机磁盘写入失败"), new UnauthorizedAccessException()];
        Assert.All(errors, error => Assert.False(NetworkFailureAdvice.IsApplicable(error)));
    }
    [Fact] public void TimeoutEofAndWrappedSocketFailure_AreActionableNetworkFailures()
    {
        Assert.True(NetworkFailureAdvice.IsApplicable(new TimeoutException()));
        Assert.True(NetworkFailureAdvice.IsApplicable(new EndOfStreamException()));
        Assert.True(NetworkFailureAdvice.IsApplicable(new IOException("连接失败", new SocketException((int)SocketError.ConnectionReset))));
    }
}
