namespace Helldivers2ModManager.Core.Nexus;

public class NexusApiException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public NexusApiException(string message, int statusCode, string code) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

public sealed class NexusRateLimitException : NexusApiException
{
    public TimeSpan? RetryAfter { get; }

    public NexusRateLimitException(TimeSpan? retryAfter = null)
        : base("Nexus API rate limit exceeded.", 429, "RateLimited")
    {
        RetryAfter = retryAfter;
    }
}

