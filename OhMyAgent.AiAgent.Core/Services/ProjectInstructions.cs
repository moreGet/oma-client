using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 워크스페이스 루트의 <c>AGENT.md</c> 를 읽어 시스템 프롬프트에 덧붙일 "프로젝트 지침"을 만든다.
/// (Claude Code 의 CLAUDE.md 에 해당하는 개념.)
///
/// 왜 가능한가: 서버는 시스템 프롬프트를 주입하지 않는 순수 중계기이고, 시스템 프롬프트는
/// <see cref="AgentSession.DefaultSystemPrompt"/> 가 만들어 messages[0] 에 넣는다 —
/// 즉 클라이언트가 완전히 소유하므로 서버 변경 없이 프로젝트별 지침을 얹을 수 있다.
///
/// 지침 파일은 사용자가 자기 워크스페이스에 두는 것이므로 신뢰 입력으로 다룬다. 다만 무한정 신뢰하진 않는다:
/// 크기를 제한하고(컨텍스트 예산 보호), 활성 루트 안의 파일만 읽는다.
/// </summary>
public static class ProjectInstructions
{
    /// <summary>지침 파일명. 루트마다 이 이름의 파일을 찾는다.</summary>
    public const string FileName = "AGENT.md";

    /// <summary>루트 하나당 상한. 초과분은 잘라낸다 — 지침이 대화 예산을 잠식하면 본말전도다.</summary>
    private const int MaxCharsPerRoot = 32 * 1024;

    /// <summary>
    /// 활성 루트들에서 지침을 모아 하나의 블록으로 만든다. 없으면 null.
    /// 루트가 여러 개면 주 루트(첫 항목)부터 순서대로 이어붙이고, 어느 파일에서 왔는지 표기한다.
    /// </summary>
    public static string? Load(IReadOnlyList<string> roots)
    {
        if (roots is null || roots.Count == 0)
            return null;

        var blocks = new List<string>();

        foreach (var root in roots)
        {
            var text = TryReadFrom(root);
            if (string.IsNullOrWhiteSpace(text)) continue;

            blocks.Add($"--- {Path.Combine(root, FileName)} ---\n{text.Trim()}");
        }

        if (blocks.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("Project instructions:");
        sb.AppendLine("The user placed these instructions in the workspace. Follow them; they take precedence");
        sb.AppendLine("over your default habits, but never over the safety rules above.");
        sb.AppendLine();
        sb.Append(string.Join("\n\n", blocks));
        return sb.ToString();
    }

    private static string? TryReadFrom(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path);
            if (text.Length <= MaxCharsPerRoot)
                return text;

            AppLog.Info("ProjectInstructions",
                $"{path} 이(가) 상한({MaxCharsPerRoot:N0}자)을 넘어 잘라냈습니다({text.Length:N0}자).");
            return text[..MaxCharsPerRoot] + "\n\n[... 지침이 상한을 넘어 잘렸습니다 ...]";
        }
        catch (Exception ex)
        {
            // 읽기 실패가 대화를 막을 이유는 없다 — 지침 없이 진행한다.
            AppLog.Warn("ProjectInstructions", $"'{root}' 의 {FileName} 읽기 실패", ex);
            return null;
        }
    }
}
