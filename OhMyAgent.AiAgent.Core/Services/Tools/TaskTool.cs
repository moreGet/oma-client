using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 서브에이전트. 격리된 대화(<see cref="AgentSession"/>)에서 에이전트 루프를 한 번 더 돌리고
/// <b>최종 텍스트만</b> 부모에게 돌려준다.
///
/// 왜 필요한가 — 컨텍스트 격리다. 파일 200개를 훑어 답 한 줄을 얻는 조사를 메인 대화에서 하면
/// 그 200개의 내용이 전부 이력에 쌓이고, 매 턴 재전송되며(서버는 stateless), 결국 본 작업이 밀려난다.
/// 서브에이전트는 그 잡일을 별도 컨텍스트에서 처리하고 결론만 건넨다.
///
/// 왜 읽기 전용인가:
///  - 안전: 서브에이전트는 사용자가 직접 지시하지 않은 하위 루프다. 파일을 고치거나 명령을 실행하면
///    사용자가 승인한 적 없는 변경이 일어난다.
///  - 실용: ReadOnly 도구는 승인 게이트를 타지 않으므로(AgentOrchestrator 의 gated 조건) 승인 대화상자가
///    쏟아지지 않는다. 쓰기 도구를 넣는 순간 하위 루프마다 승인 팝업이 뜬다.
///  - 서버 도구 정책은 그대로 적용된다 — 하위 루프도 같은 policy.EvaluateAsync 게이트를 지난다.
///
/// 순환 의존이 없는 이유: 서브 오케스트레이터는 읽기 전용 레지스트리만 알면 되고, 그 레지스트리는
/// 이 도구가 메인 레지스트리에 등록되기 <b>전에</b> 만들 수 있다(App.StartupAsync 배선 참조).
/// </summary>
public sealed class TaskTool : ITool
{
    /// <summary>
    /// 서브에이전트에게 노출할 도구의 <b>명시적 허용목록</b>.
    ///
    /// <see cref="ToolRisk.ReadOnly"/> 로 거르면 안 된다. Risk 는 "이 도구가 사용자 머신을 해칠 수 있나"에
    /// 답하는 분류이지 "서브에이전트에게 안전한가"에 답하지 않는다 — 두 질문은 다르다. 실제로 ReadOnly 로
    /// 걸렀다면 아래 셋이 딸려 들어간다:
    ///  - manage_todos : 파일시스템을 안 건드려 ReadOnly 지만, TodoService 싱글톤을 통해
    ///                   <b>부모의 할 일 목록을 덮어쓴다</b>. 하위 루프가 사용자의 계획 카드를 날린다.
    ///  - task         : 자기 자신. 서브에이전트가 서브에이전트를 무한 재귀로 띄운다.
    ///  - clipboard_read / screenshot : 조사에 불필요한데 사용자 화면·클립보드를 읽는다(프라이버시).
    ///
    /// 추가할 때는 "이 도구가 부모/사용자 상태를 바꾸거나 엿보는가"를 먼저 물을 것.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 파일 탐색·읽기
        "read_file", "list_directory", "glob", "grep",
        // 문서·데이터 읽기
        "read_csv", "read_excel", "read_pdf", "read_document", "read_pptx", "read_hwpx",
        // 환경 파악(조사 맥락에서 유용, 부작용 없음)
        "get_environment", "list_processes", "list_processes_memory_kb",
    };

    /// <summary>서브에이전트 반복 상한. 메인(기본 25)보다 낮게 — 좁은 조사 임무만 맡기기 때문이다.</summary>
    private const int SubagentMaxIterations = 12;

    /// <summary>반환 텍스트 상한. 결론만 받자는 도구인데 장문을 그대로 부모 컨텍스트에 부으면 목적이 무너진다.</summary>
    private const int MaxResultChars = 20_000;

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{
          "description":{"type":"string","description":"3~5 단어의 짧은 작업명(진행 표시용)"},
          "prompt":{"type":"string","description":"서브에이전트가 수행할 임무. 서브에이전트는 이 대화의 맥락을 모르므로 자립적으로(self-contained) 작성할 것. 무엇을 조사하고 무엇을 반환할지 명시."}
        },"required":["description","prompt"]}
        """);

    private readonly IAgentOrchestrator _subOrchestrator;
    private readonly IWorkspaceContext _workspace;

    public TaskTool(IAgentOrchestrator subOrchestrator, IWorkspaceContext workspace)
    {
        _subOrchestrator = subOrchestrator;
        _workspace = workspace;
    }

    public string Name => "task";

    public string Description =>
        "Delegate a read-only investigation to a subagent that runs in its own isolated context and returns only its conclusion. " +
        "Use this when answering would require reading many files, or when a search is broad and you only need the finding — " +
        "it keeps that bulk out of this conversation. The subagent cannot modify anything (read-only tools only) and cannot " +
        "see this conversation, so make 'prompt' self-contained and state exactly what to return. " +
        "Do NOT use it for a single known file (just read it) or for anything that must write/execute.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>읽기 전용 도구만 돌리므로 이 도구 자체도 ReadOnly — 승인 게이트를 타지 않는다.</summary>
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var prompt = ToolSchemas.GetString(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt 가 비어 있습니다. 서브에이전트가 수행할 임무를 자립적으로 서술하세요.");

        var description = ToolSchemas.GetString(args, "description") ?? "subagent";

        // 격리된 세션 — 부모 이력을 넘기지 않는다(그게 이 도구의 존재 이유다).
        // 시스템 프롬프트를 미리 넣어 두면 오케스트레이터가 기본 프롬프트를 시드하지 않는다.
        var session = new AgentSession();
        session.Messages.Add(AgentMessage.System(BuildSubagentPrompt()));

        var finalText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await foreach (var evt in _subOrchestrator
                               .RunAsync(prompt, session, attachments: null, ct: ct, maxIterations: SubagentMaxIterations)
                               .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentDone done:
                        finalText.Append(done.FinalText);
                        break;

                    // 하위 루프의 실패는 부모에게 사실대로 전달한다 — 조용히 빈 결과를 주면
                    // 부모가 "조사했는데 아무것도 없다"로 오해한다.
                    case AgentError err:
                        errorMessage = $"[{err.Code}] {err.Message}";
                        break;

                    // 그 외(텍스트 델타·도구 호출·반복)는 의도적으로 버린다. 부모가 알 필요가 없고,
                    // 흘려보내면 컨텍스트 격리라는 목적 자체가 사라진다.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("TaskTool", $"서브에이전트 '{description}' 실행 실패", ex);
            return ToolResult.Fail($"서브에이전트 실행 실패: {ex.Message}");
        }

        var text = finalText.ToString().Trim();

        if (errorMessage is not null && text.Length == 0)
            return ToolResult.Fail($"서브에이전트가 완료하지 못했습니다: {errorMessage}");

        if (text.Length == 0)
            return ToolResult.Fail(
                "서브에이전트가 빈 결과를 반환했습니다. prompt 가 무엇을 반환해야 하는지 명시하는지 확인하세요.");

        if (text.Length > MaxResultChars)
            text = text[..MaxResultChars] + "\n\n[... 서브에이전트 결과가 상한을 초과해 잘렸습니다 ...]";

        // 부분 실패(결과는 있으나 오류도 있었음)는 결과에 덧붙여 부모가 판단하게 한다.
        if (errorMessage is not null)
            text += $"\n\n[주의: 서브에이전트가 완주하지 못했습니다 — {errorMessage}. 위 결과는 불완전할 수 있습니다.]";

        return ToolResult.Ok(text);
    }

    /// <summary>
    /// 서브에이전트 전용 시스템 프롬프트. 메인과 다른 점:
    /// 도구가 읽기 전용뿐이고, 대화 상대가 사람이 아니라 부모 에이전트이며, 되묻기가 불가능하다.
    /// </summary>
    private string BuildSubagentPrompt()
    {
        var rootsBlock = string.Join("\n", _workspace.Roots.Select(r => $"  - {r}"));

        return
        $"""
        You are a read-only investigation subagent inside a Windows desktop agent.
        A parent agent delegated one narrow task to you. You run in an isolated context: you cannot see the
        parent's conversation, and the parent will only ever see your FINAL message — nothing else.

        Environment:
        - OS: Windows
        - Primary workspace root: {_workspace.Root}
        - Allowed workspace roots (read access only):
        {rootsBlock}

        Your constraints:
        - You have READ-ONLY tools. You cannot modify files, run commands, or change any state. Do not plan to.
        - You cannot ask questions. There is no human to answer you. If the task is ambiguous, investigate the most
          reasonable interpretation and say in your final message which interpretation you chose and why.
        - Be efficient: you have a small iteration budget. Search deliberately rather than reading everything.

        Finding things:
        - Treat names in the task as hints, not addresses. If an exact name misses, widen with glob wildcards,
          then search file content with grep.
        - read_file and glob return near-miss candidates when nothing matches — use them instead of retrying blindly.

        Your final message IS the return value:
        - Answer the task directly and completely in your final message. The parent sees nothing else — no tool
          output, no intermediate reasoning. If you found it but do not state it, it is lost.
        - Include the concrete evidence the parent needs: file paths, line numbers, exact snippets, counts.
        - Report honestly: state what you could not determine rather than guessing or padding.
        - Do not address the user or write a chat-style reply. Write the finding as data for the parent agent.
        - Write in Korean.
        """;
    }
}
