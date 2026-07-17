using System.Security.Cryptography;
using System.Text;

namespace Helldivers2ModManager.Core.BrowserIntegration;

public sealed record BrowserPairingResult(bool IsSuccess, string? BearerToken, string? ErrorCode);

public sealed class BrowserPairingCoordinator
{
    private static readonly TimeSpan s_pairingCodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_failureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_requestLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_replayWindow = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failedPairingAttempts = new();
    private readonly Dictionary<Guid, DateTimeOffset> _requestIds = [];
    private string? _pairingCodeHash;
    private DateTimeOffset _pairingCodeExpiresAt;
    private string? _tokenHash;
    private string? _pairedOrigin;

    public BrowserPairingCoordinator(string? tokenHash = null, string? pairedOrigin = null)
    {
        if (IsSha256Hash(tokenHash) && TryNormalizeExtensionOrigin(pairedOrigin, out var origin))
        {
            _tokenHash = tokenHash;
            _pairedOrigin = origin;
        }
    }

    public string? TokenHash
    {
        get { lock (_gate) return _tokenHash; }
    }

    public string? PairedOrigin
    {
        get { lock (_gate) return _pairedOrigin; }
    }

    public string GeneratePairingCode(DateTimeOffset now)
    {
        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        lock (_gate)
        {
            _pairingCodeHash = Hash(code);
            _pairingCodeExpiresAt = now + s_pairingCodeLifetime;
            _failedPairingAttempts.Clear();
        }
        return code;
    }

    public BrowserPairingResult Pair(string code, string? origin, DateTimeOffset now)
    {
        if (!TryNormalizeExtensionOrigin(origin, out var normalizedOrigin))
            return new BrowserPairingResult(false, null, "Pair.InvalidOrigin");

        lock (_gate)
        {
            PruneFailures(now);
            if (_failedPairingAttempts.Count >= 5)
                return new BrowserPairingResult(false, null, "Pair.RateLimited");
            if (_pairingCodeHash is null || now > _pairingCodeExpiresAt)
                return FailedPairing(now, "Pair.CodeExpired");
            if (!FixedTimeEquals(_pairingCodeHash, Hash(code)))
                return FailedPairing(now, "Pair.InvalidCode");

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _tokenHash = Hash(token);
            _pairedOrigin = normalizedOrigin;
            _pairingCodeHash = null;
            _pairingCodeExpiresAt = default;
            _failedPairingAttempts.Clear();
            return new BrowserPairingResult(true, token, null);
        }
    }

    public bool Authenticate(
        string? bearerToken,
        string? origin,
        DateTimeOffset requestTimestamp,
        Guid requestId,
        DateTimeOffset now,
        out string? errorCode)
    {
        lock (_gate)
        {
            if (_tokenHash is null || _pairedOrigin is null)
                return Fail("Auth.NotPaired", out errorCode);
            if (!TryNormalizeExtensionOrigin(origin, out var normalizedOrigin) ||
                !string.Equals(normalizedOrigin, _pairedOrigin, StringComparison.Ordinal))
                return Fail("Auth.OriginMismatch", out errorCode);
            if (string.IsNullOrWhiteSpace(bearerToken) || !FixedTimeEquals(_tokenHash, Hash(bearerToken)))
                return Fail("Auth.InvalidToken", out errorCode);
            if ((now - requestTimestamp).Duration() > s_requestLifetime)
                return Fail("Auth.ExpiredRequest", out errorCode);

            PruneRequestIds(now);
            if (_requestIds.ContainsKey(requestId))
                return Fail("Auth.ReplayedRequest", out errorCode);
            _requestIds[requestId] = now;
            errorCode = null;
            return true;
        }
    }

    public void Unpair()
    {
        lock (_gate)
        {
            _pairingCodeHash = null;
            _tokenHash = null;
            _pairedOrigin = null;
            _requestIds.Clear();
            _failedPairingAttempts.Clear();
        }
    }

    public static bool TryNormalizeExtensionOrigin(string? origin, out string? normalizedOrigin)
    {
        normalizedOrigin = null;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "chrome-extension" && uri.Scheme != "moz-extension") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
        {
            return false;
        }
        normalizedOrigin = $"{uri.Scheme}://{uri.Host}";
        return true;
    }

    private BrowserPairingResult FailedPairing(DateTimeOffset now, string errorCode)
    {
        _failedPairingAttempts.Enqueue(now);
        return new BrowserPairingResult(false, null, errorCode);
    }

    private void PruneFailures(DateTimeOffset now)
    {
        while (_failedPairingAttempts.TryPeek(out var attemptedAt) && now - attemptedAt > s_failureWindow)
            _failedPairingAttempts.Dequeue();
    }

    private void PruneRequestIds(DateTimeOffset now)
    {
        foreach (var requestId in _requestIds
            .Where(pair => now - pair.Value > s_replayWindow)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _requestIds.Remove(requestId);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string expectedHex, string actualHex) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), Convert.FromHexString(actualHex));

    private static bool IsSha256Hash(string? value)
    {
        if (value is not { Length: 64 })
            return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private static bool Fail(string code, out string? errorCode)
    {
        errorCode = code;
        return false;
    }
}
