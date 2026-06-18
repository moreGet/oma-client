namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>대화 메시지의 역할. 와이어에서는 snake/lower-case로 직렬화된다 (system/user/assistant/tool).</summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>도구가 시스템에 끼칠 수 있는 위험도. 권한 게이트의 기준.</summary>
public enum ToolRisk
{
    ReadOnly,
    Write,
    Execute,
    Destructive
}

/// <summary>권한 승인 모드.</summary>
public enum PermissionMode
{
    Manual,
    AutoSafe,
    FullAuto
}

/// <summary>권한 승인 결정.</summary>
public enum PermissionDecision
{
    Allow,
    Deny,
    AlwaysAllow
}

/// <summary>응답 종료 사유. 와이어 stop_reason 문자열을 매핑.</summary>
public enum StopReason
{
    ToolUse,
    EndTurn,
    MaxTokens,
    Error,
    Unknown
}
