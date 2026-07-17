using Helldivers2ModManager.Core.BrowserIntegration;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class BrowserPairingCoordinatorTests
{
    private static readonly DateTimeOffset s_now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PairingCodeIsSingleUseAndTokenAuthenticatesOnlyExactOrigin()
    {
        var coordinator = new BrowserPairingCoordinator();
        var code = coordinator.GeneratePairingCode(s_now);

        var pairing = coordinator.Pair(code, "chrome-extension://abcdefghijklmnop", s_now);

        Assert.True(pairing.IsSuccess);
        Assert.NotNull(pairing.BearerToken);
        Assert.False(coordinator.Pair(code, "chrome-extension://abcdefghijklmnop", s_now).IsSuccess);
        Assert.True(coordinator.Authenticate(
            pairing.BearerToken,
            "chrome-extension://abcdefghijklmnop",
            s_now,
            Guid.NewGuid(),
            s_now,
            out _));
        Assert.False(coordinator.Authenticate(
            pairing.BearerToken,
            "chrome-extension://different",
            s_now,
            Guid.NewGuid(),
            s_now,
            out var error));
        Assert.Equal("Auth.OriginMismatch", error);
    }

    [Fact]
    public void PairingExpiresAndLimitsFailedAttemptsToFivePerMinute()
    {
        var coordinator = new BrowserPairingCoordinator();
        var code = coordinator.GeneratePairingCode(s_now);

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal("Pair.InvalidCode", coordinator.Pair("00000000", "moz-extension://valid", s_now).ErrorCode);

        Assert.Equal("Pair.RateLimited", coordinator.Pair(code, "moz-extension://valid", s_now).ErrorCode);
        var freshCode = coordinator.GeneratePairingCode(s_now);
        Assert.Equal(
            "Pair.CodeExpired",
            coordinator.Pair(freshCode, "moz-extension://valid", s_now.AddMinutes(6)).ErrorCode);
    }

    [Fact]
    public void AuthenticationRejectsExpiredAndReplayedRequests()
    {
        var coordinator = new BrowserPairingCoordinator();
        var code = coordinator.GeneratePairingCode(s_now);
        var pairing = coordinator.Pair(code, "chrome-extension://valid", s_now);
        var requestId = Guid.NewGuid();

        Assert.True(coordinator.Authenticate(
            pairing.BearerToken,
            "chrome-extension://valid",
            s_now,
            requestId,
            s_now,
            out _));
        Assert.False(coordinator.Authenticate(
            pairing.BearerToken,
            "chrome-extension://valid",
            s_now,
            requestId,
            s_now,
            out var replayError));
        Assert.Equal("Auth.ReplayedRequest", replayError);
        Assert.False(coordinator.Authenticate(
            pairing.BearerToken,
            "chrome-extension://valid",
            s_now.AddMinutes(-6),
            Guid.NewGuid(),
            s_now,
            out var expiredError));
        Assert.Equal("Auth.ExpiredRequest", expiredError);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://nexusmods.com")]
    [InlineData("chrome-extension://")]
    [InlineData("chrome-extension://valid/path")]
    public void OrdinaryWebAndMalformedOriginsCannotPair(string origin)
    {
        var coordinator = new BrowserPairingCoordinator();
        var code = coordinator.GeneratePairingCode(s_now);
        Assert.Equal("Pair.InvalidOrigin", coordinator.Pair(code, origin, s_now).ErrorCode);
    }

    [Theory]
    [InlineData("https://files.nexus-cdn.com/6119/file.zip", true)]
    [InlineData("https://nexusmods.com/file.zip", true)]
    [InlineData("https://www.nexusmods.com/file.zip", true)]
    [InlineData("https://evilnexusmods.com/file.zip", false)]
    [InlineData("https://nexusmods.com.evil.example/file.zip", false)]
    [InlineData("http://files.nexus-cdn.com/6119/file.zip", false)]
    [InlineData("https://user:password@nexusmods.com/file.zip", false)]
    public void DownloadUrlPolicyMatchesOnlyApprovedHttpsHosts(string url, bool expected)
    {
        Assert.Equal(expected, NexusDownloadUrlPolicy.IsAllowed(new Uri(url)));
    }
}
