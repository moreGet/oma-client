namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>POST /api/v1/auth/login 결과. 성공 시 Token 보유.</summary>
public sealed record LoginResult(bool Success, string? Token, string? ErrorMessage)
{
    public static LoginResult Ok(string token)     => new(true, token, null);
    public static LoginResult Fail(string message) => new(false, null, message);
}
