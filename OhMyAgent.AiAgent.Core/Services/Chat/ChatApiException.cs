using System;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// Chat REST 도메인 예외. HTTP 상태코드를 보존해 호출자(상위 파사드)가 401/403/404/429/5xx 분기하도록 한다.
/// AgentException 선례를 따른다(상태코드 보존이 추가된 형태).
/// </summary>
public sealed class ChatApiException(int statusCode, string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
