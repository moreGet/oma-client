using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>새 빈 세션. (기존 경로 보존)</summary>
    public AgentSession() { }

    /// <summary>
    /// 디스크에서 복원한 세션 재구성용. Id가 get-only이므로 ctor로 주입한다.
    /// (ChatSessionRecord → VM RestoreSession 경로에서 사용.)
    /// </summary>
    public AgentSession(string id, IEnumerable<AgentMessage> messages)
    {
        Id = id;
        Messages.AddRange(messages);
    }

    public static string DefaultSystemPrompt(string workspaceRoot, PermissionMode mode) =>
        DefaultSystemPrompt(workspaceRoot, mode, null);

    public static string DefaultSystemPrompt(string workspaceRoot, PermissionMode mode, IReadOnlyList<string>? roots)
    {
        // 활성 루트가 여러 개면 전체 허용 루트를 줄바꿈 목록으로 고지한다(주 루트 = 첫 항목 = 셸 cwd).
        var allRoots = (roots != null && roots.Count > 0) ? roots : [workspaceRoot];
        var rootsBlock = string.Join("\n", allRoots.Select(r => $"  - {r}"));

        return
        $"""
        You are a Windows desktop automation agent embedded in a WPF client.
        You accomplish the user's goal by calling the provided tools in a loop until the task is done.

        Environment:
        - OS: Windows
        - Primary workspace root (shell cwd): {workspaceRoot}
        - Permission mode: {mode}
        - Allowed workspace roots (file access is permitted in any of these):
        {rootsBlock}

        Rules:
        - All file operations are sandboxed to the allowed workspace roots above. Never attempt to access paths outside them.
        - Prefer the most specific tool (read_file/write_file/edit_file/glob/grep/...) over run_command when possible.
        - Use run_command only for tasks no dedicated tool covers; specify shell as "powershell" or "cmd".
        - Tool arguments must strictly follow each tool's JSON Schema.
        - When the task is complete, stop calling tools and reply with a concise summary in Korean.
        - If a tool returns an error, read it, adjust, and retry; do not loop indefinitely.
        """;
    }
}
