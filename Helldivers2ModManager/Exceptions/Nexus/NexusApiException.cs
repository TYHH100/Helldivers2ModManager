namespace Helldivers2ModManager.Exceptions.Nexus
{
    internal class NexusApiException : Exception
    {
        public int? StatusCode { get; }
        public string? ErrorType { get; }

        public NexusApiException(string message) : base(message)
        {
        }

        public NexusApiException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public NexusApiException(string message, int statusCode, string? errorType) : base(message)
        {
            StatusCode = statusCode;
            ErrorType = errorType;
        }

        public NexusApiException(string message, int statusCode, string? errorType, Exception innerException) 
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ErrorType = errorType;
        }
    }

    internal class NexusModNotFoundException : NexusApiException
    {
        public string ModId { get; }

        public NexusModNotFoundException(string modId) 
            : base($"Mod with ID '{modId}' not found.", 404, "NotFound")
        {
            ModId = modId;
        }

        public NexusModNotFoundException(string modId, Exception innerException) 
            : base($"Mod with ID '{modId}' not found.", 404, "NotFound", innerException)
        {
            ModId = modId;
        }
    }

    internal class NexusApiKeyInvalidException : NexusApiException
    {
        public NexusApiKeyInvalidException() 
            : base("Invalid or missing API Key. Please check your API Key configuration.", 403, "Unauthorized")
        {
        }

        public NexusApiKeyInvalidException(string message) 
            : base(message, 403, "Unauthorized")
        {
        }

        public NexusApiKeyInvalidException(string message, Exception innerException) 
            : base(message, 403, "Unauthorized", innerException)
        {
        }
    }

    internal class NexusRateLimitException : NexusApiException
    {
        public TimeSpan? RetryAfter { get; }

        public NexusRateLimitException() 
            : base("Rate limit exceeded. Please try again later.", 429, "RateLimit")
        {
        }

        public NexusRateLimitException(TimeSpan retryAfter) 
            : base($"Rate limit exceeded. Please retry after {retryAfter.TotalSeconds} seconds.", 429, "RateLimit")
        {
            RetryAfter = retryAfter;
        }

        public NexusRateLimitException(string message, Exception innerException) 
            : base(message, 429, "RateLimit", innerException)
        {
        }
    }

    internal class NexusValidationException : NexusApiException
    {
        public IReadOnlyList<string> Errors { get; }

        public NexusValidationException(IReadOnlyList<string> errors) 
            : base("Validation failed. Please check your request parameters.", 422, "Validation")
        {
            Errors = errors;
        }

        public NexusValidationException(string message, IReadOnlyList<string> errors) 
            : base(message, 422, "Validation")
        {
            Errors = errors;
        }
    }
}