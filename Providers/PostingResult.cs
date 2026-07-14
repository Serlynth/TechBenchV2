namespace TechBench.Providers;

public sealed class PostingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string? ExternalReference { get; init; }
    public bool MarkPosted { get; init; } = true;
    public bool OutcomeUncertain { get; init; }

    public static PostingResult Succeeded(string message, string payload, string? externalReference = null, bool markPosted = true)
    {
        return new PostingResult
        {
            Success = true,
            Message = message,
            Payload = payload,
            ExternalReference = externalReference,
            MarkPosted = markPosted
        };
    }

    public static PostingResult Failed(string message, string payload = "")
    {
        return new PostingResult
        {
            Success = false,
            Message = message,
            Payload = payload,
            MarkPosted = false
        };
    }

    public static PostingResult Uncertain(string message, string payload = "", string? externalReference = null)
    {
        return new PostingResult
        {
            Success = false,
            Message = message,
            Payload = payload,
            ExternalReference = externalReference,
            MarkPosted = false,
            OutcomeUncertain = true
        };
    }
}
