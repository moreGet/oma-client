namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? Reason { get; init; }
    public string? MatchedPattern { get; init; }

    public static ValidationResult Valid()
        => new() { IsValid = true };

    public static ValidationResult Invalid(string reason, string? pattern = null)
        => new() { IsValid = false, Reason = reason, MatchedPattern = pattern };
}
