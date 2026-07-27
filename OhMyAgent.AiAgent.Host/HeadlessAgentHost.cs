using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 트랜스포트 무관 실행 코어. orchestrator 를 보유하고 프롬프트 1건(<see cref="RunOnceAsync"/>) 또는
/// 표준입력 루프(<see cref="RunInteractiveAsync"/>)로 실행한다.
///
/// A2A 확장점: 이후 서버 트랜스포트가 요청마다 <see cref="RunOnceAsync"/> 를 호출하면 된다.
/// 세션 격리는 요청마다 <see cref="AgentSession"/> 을 새로 만든다.
///
/// <paramref name="authFailures"/> 는 오류 이벤트 중 인증 실패(401/403)를 걸러 종료로 이어지게 한다 —
/// 헤드리스는 토큰 갱신 경로가 없어 401 이 나면 재시도가 무의미하기 때문이다.
/// </summary>
public sealed class HeadlessAgentHost(IAgentOrchestrator orchestrator, AuthFailureReporter authFailures)
{
    /// <summary>
    /// headless-safe 도구 세트. App.xaml.cs 등록 배열에서 UI 전용 3종
    /// (clipboard_read/clipboard_write/screenshot)만 제외하고 순서를 유지한다(스펙 §1-D).
    /// task 도구는 Program.cs 에서 subOrchestrator 조립 후 별도 추가.
    /// </summary>
    public static ITool[] BuildHeadlessTools(IScriptExecutor exec, System.Net.Http.HttpClient toolHttp, TodoService todos) =>
    new ITool[]
    {
        new RunCommandTool(exec),
        new ReadFileTool(), new WriteFileTool(), new EditFileTool(),
        new ListDirectoryTool(), new GlobTool(), new GrepTool(),
        new CreateDirectoryTool(), new MoveTool(), new CopyTool(), new DeleteTool(),
        // ── 시스템 경량 (Clipboard 2종 제외) ──
        new GetEnvironmentTool(),
        new ListProcessesTool(), new ListProcessesMemoryKbTool(),
        new StartProcessTool(), new KillProcessTool(),
        new HttpFetchTool(toolHttp),
        // ScreenshotTool 제외
        // ── 에이전트 메타 ──
        new ManageTodosTool(todos),
        // ── 문서·데이터 (전부 크로스플랫폼) ──
        new ReadCsvTool(), new WriteCsvTool(),
        new ReadExcelTool(), new WriteExcelTool(),
        new ReadPdfTool(), new ReadDocumentTool(),
        new ReadPptxTool(), new WritePptxTool(), new ReadHwpxTool(),
        // ── 압축 ──
        new CompressFilesTool(), new ExtractArchiveTool(),
    };

    /// <summary>프롬프트 1건을 새 세션으로 처리(파이프/인자 1회 처리, A2A 요청 핸들러 재사용점).</summary>
    public async Task RunOnceAsync(string prompt, CancellationToken ct)
    {
        var session = new AgentSession();
        await RunSessionAsync(prompt, session, ct).ConfigureAwait(false);
    }

    /// <summary>표준입력 프롬프트 루프. 한 줄씩 읽어 동일 세션으로 이어서 처리한다.</summary>
    public async Task RunInteractiveAsync(CancellationToken ct)
    {
        var session = new AgentSession();
        Console.Error.WriteLine("헤드리스 대화 모드 — 프롬프트를 입력하세요(빈 줄 또는 Ctrl+C 로 종료).");
        while (!ct.IsCancellationRequested)
        {
            Console.Error.Write("> ");
            string? line;
            try { line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            await RunSessionAsync(line, session, ct).ConfigureAwait(false);
        }
    }

    /// <summary>orchestrator 이벤트 스트림을 소비해 콘솔에 렌더링한다.
    /// 어시스턴트 텍스트는 stdout, 진행/도구/오류 로그는 stderr 로 분리한다.</summary>
    private async Task RunSessionAsync(string prompt, AgentSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in orchestrator.RunAsync(prompt, session, ct: ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case AgentTextDelta d:
                        Console.Out.Write(d.Text);
                        Console.Out.Flush();
                        break;
                    case AgentToolCallStarted t:
                        Console.Error.WriteLine($"\n[도구 호출] {t.ToolName} (risk={t.Risk})");
                        break;
                    case AgentAwaitingApproval a:
                        Console.Error.WriteLine($"[승인 대기] {a.ToolName} (risk={a.Risk})");
                        break;
                    case AgentToolCallResult r:
                        Console.Error.WriteLine($"[도구 결과] {r.ToolName} (error={r.Result.IsError})");
                        break;
                    case AgentIterationAdvanced it:
                        Console.Error.WriteLine($"[반복 {it.Iteration}/{it.MaxIterations}]");
                        break;
                    case AgentNotice n:
                        Console.Error.WriteLine($"[알림] {n.Text}");
                        break;
                    case AgentError e:
                        Console.Error.WriteLine($"[오류] {e.Code}: {e.Message}");
                        // 인증 오류만 별도 집계 — 임계값에 닿으면 종료 콜백이 프로세스를 접는다.
                        authFailures.ObserveErrorCode("Host", e.Code, e.Message);
                        break;
                    case AgentDone done:
                        authFailures.RecordSuccess();   // 턴이 끝났다 = 인증이 통했다.
                        Console.Out.WriteLine();
                        if (done.LastUsage is { } u)
                            Console.Error.WriteLine($"[완료] 사용 토큰 prompt={u.PromptTokens} completion={u.CompletionTokens} total={u.TotalTokens}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("\n[취소됨]");
        }
    }
}
