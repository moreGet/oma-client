using System;
using System.Collections.Generic;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 클라이언트가 보유하는 세션 상태 (서버는 stateless).
/// 전체 대화 기록(시스템 프롬프트 포함)을 담는다.
/// </summary>
public sealed class AgentSession
{
    public string Id { get; } = Guid.NewGuid().ToString();

    public List<AgentMessage> Messages { get; } = [];

    public Usage? LastUsage { get; set; }

    public static string DefaultSystemPrompt(string workspaceRoot, PermissionMode mode) =>
        $"""
        You are a Windows desktop automation agent embedded in a WPF client.
        You accomplish the user's goal by calling the provided tools in a loop until the task is done.

        Environment:
        - OS: Windows
        - Workspace root: {workspaceRoot}
        - Permission mode: {mode}

        Rules:
        - All file operations are sandboxed to the workspace root. Never attempt to access paths outside it.
        - Prefer the most specific tool (read_file/write_file/edit_file/glob/grep/...) over run_command when possible.
        - Use run_command only for tasks no dedicated tool covers; specify shell as "powershell" or "cmd".
        - Tool arguments must strictly follow each tool's JSON Schema.
        - When the task is complete, stop calling tools and reply with a concise summary in Korean.
        - If a tool returns an error, read it, adjust, and retry; do not loop indefinitely.
        """;
}
