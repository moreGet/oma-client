namespace OhMyAgent.AiAgent.Client.Models;

public record DomainOptions(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
