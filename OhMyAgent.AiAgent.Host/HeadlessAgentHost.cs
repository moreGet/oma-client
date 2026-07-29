using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.Services.Tools;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 트랜스포트 무관 실행 코어. orchestrator 를 보유하고 프롬프트 1건(<see cref="RunOnceAsync"/>) 또는
/// 표준입력 루프(<see cref="RunInteractiveAsync"/>)로 실행한다.
///
/// A2A 확장점: 이후 서버 트랜스포트가 요청마다 <see cref="RunOnceAsync"/> 를 호출하면 된다.
/// 세션 격리는 요청마다 <see cref="AgentSession"/> 을 새로 만든다.
///
/// authFailures 는 오류 이벤트 중 인증 실패(401/403)를 걸러 종료로 이어지게 한다 —
/// 헤드리스는 토큰 갱신 경로가 없어 401 이 나면 재시도가 무의미하기 때문이다.
///
/// loop 는 GUI 와 공유하는 /loop 엔진이다. 진행 상황은 stdout(어시스턴트 응답)을 오염시키지 않도록
/// 전부 stderr 로 나간다.
/// </summary>
public sealed class HeadlessAgentHost
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly AuthFailureReporter _authFailures;
    private readonly ILoopController _loop;

    /// <summary>루프 턴과 REPL 턴의 동시 실행 봉쇄(A1/A2) — 같은 세션에 두 턴이 겹치면 대화 이력이 꼬인다.</summary>
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>대화 모드의 현재 세션. 루프와 REPL 이 같은 세션을 공유해야 반복 턴이 맥락을 이어받는다.</summary>
    private AgentSession _session = new();

    /// <summary>/retry 용 마지막 프롬프트. CLI 는 입력창이 없어 되불러 편집 대신 재실행한다.</summary>
    private string _lastPrompt = "";

    /// <summary>LoopTick stderr 출력 스로틀 기준(마지막으로 출력한 남은 초).</summary>
    private long _lastTickSecond = -1;

    public HeadlessAgentHost(IAgentOrchestrator orchestrator, AuthFailureReporter authFailures, ILoopController loop)
    {
        _orchestrator = orchestrator;
        _authFailures = authFailures;
        _loop = loop;
        _loop.LoopChanged += OnLoopChanged;
    }

    /// <summary>
    /// headless-safe 도구 세트. App.xaml.cs 등록 배열에서 UI 전용 3종
    /// (clipboard_read/clipboard_write/screenshot)만 제외하고 순서를 유지한다(스펙 §1-D).
    /// task 도구는 Program.cs 에서 subOrchestrator 조립 후 별도 추가.
    /// </summary>
    public static ITool[] BuildHeadlessTools(
        IScriptExecutor exec, System.Net.Http.HttpClient toolHttp, TodoService todos, IWakeupSink wakeups) =>
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
        new ScheduleWakeupTool(wakeups),
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
        _ = await RunSessionAsync(prompt, session, ct).ConfigureAwait(false);
    }

    /// <summary>표준입력 프롬프트 루프. 한 줄씩 읽어 동일 세션으로 이어서 처리한다.</summary>
    public async Task RunInteractiveAsync(CancellationToken ct)
    {
        _session = new AgentSession();
        Console.Error.WriteLine("헤드리스 대화 모드 — 프롬프트를 입력하세요(/help 로 커맨드 목록, Ctrl+C 로 종료).");
        while (!ct.IsCancellationRequested)
        {
            Console.Error.Write("> ");
            string? line;
            try { line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;                       // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cmd = SlashCommands.Parse(line);
            if (cmd.Kind != SlashCommandKind.None)
            {
                if (!await HandleSlashAsync(cmd, ct).ConfigureAwait(false)) break;
                continue;
            }

            // 루프 실행 중에는 일반 프롬프트를 막는다(A2). 슬래시 커맨드는 위에서 이미 처리됐다.
            if (_loop.IsRunning)
            {
                Console.Error.WriteLine("[루프] 반복 실행 중입니다. /loop stop 으로 중지한 뒤 입력하세요.");
                continue;
            }

            _lastPrompt = line;
            _ = await RunSessionAsync(line, _session, ct).ConfigureAwait(false);
        }

        // REPL 을 빠져나가면 루프도 의미가 없다 — 남겨 두면 stdin 없는 프로세스가 계속 서버를 호출한다.
        await _loop.StopAndWaitAsync(LoopStopReason.HostShutdown).ConfigureAwait(false);
    }

    /// <summary>슬래시 커맨드 실행. 반환값은 "대화 모드 계속" 여부(false 면 REPL 탈출).</summary>
    private async Task<bool> HandleSlashAsync(SlashCommand cmd, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            case SlashCommandKind.Clear:
                _loop.Stop(LoopStopReason.UserStopped);   // 새 세션 = 기존 루프의 맥락이 사라진다.
                _session = new AgentSession();
                _lastPrompt = "";
                Console.Error.WriteLine("[초기화] 새 세션을 시작합니다.");
                return true;

            case SlashCommandKind.Help:
                Console.Error.WriteLine(SlashCommands.HelpText + "\n" + SlashCommands.HeadlessHelpExtra);
                return true;

            case SlashCommandKind.Retry:
                // /retry 는 결국 일반 프롬프트를 한 번 더 던지는 것이라 A2 가 그대로 적용된다.
                // _turnGate 덕에 동시 실행은 안 되지만, 루프 턴 사이에 끼어들면 반복 대화에
                // 사용자가 의도하지 않은 턴이 삽입되어 이후 반복의 맥락이 달라진다.
                if (_loop.IsRunning)
                {
                    Console.Error.WriteLine("[루프] 반복 실행 중입니다. /loop stop 으로 중지한 뒤 재실행하세요.");
                    return true;
                }
                if (string.IsNullOrWhiteSpace(_lastPrompt))
                {
                    Console.Error.WriteLine("[안내] 다시 보낼 이전 프롬프트가 없습니다.");
                    return true;
                }
                // CLI 에는 되불러 편집할 입력창이 없으므로 곧바로 재실행한다.
                Console.Error.WriteLine($"[재실행] {_lastPrompt}");
                _ = await RunSessionAsync(_lastPrompt, _session, ct).ConfigureAwait(false);
                return true;

            case SlashCommandKind.Exit:
                Console.Error.WriteLine("[종료]");
                return false;

            case SlashCommandKind.Loop:
                HandleLoopCommand(cmd.Loop, ct);
                return true;

            default:
                Console.Error.WriteLine(SlashCommands.UnknownText(cmd.Raw));
                return true;
        }
    }

    private void HandleLoopCommand(LoopArgs? args, CancellationToken ct)
    {
        if (args is null) return;

        switch (args.Action)
        {
            case LoopAction.Stop:
                if (!_loop.IsRunning) { Console.Error.WriteLine("[루프] 실행 중인 반복 작업이 없습니다."); return; }
                _loop.Stop(LoopStopReason.UserStopped);
                return;

            case LoopAction.Status:
                Console.Error.WriteLine(LoopStatusFormatter.DescribeDetailed(_loop.Status));
                return;

            default:
                if (string.IsNullOrWhiteSpace(args.Prompt))
                {
                    Console.Error.WriteLine("[루프] 반복할 프롬프트가 필요합니다. 예: /loop 5m 빌드 상태 확인");
                    return;
                }

                var request = new LoopStartRequest(
                    args.Interval is null ? LoopMode.Autonomous : LoopMode.FixedInterval,
                    args.Interval,
                    args.Prompt,
                    LoopPolicy.DefaultMaxIterations);

                if (!_loop.TryStart(request, RunLoopTurnAsync, ct, out var error))
                    Console.Error.WriteLine($"[루프] {error}");
                return;
        }
    }

    /// <summary>헤드리스 LoopTurnRunner — 동일 세션에 프롬프트를 다시 던진다.</summary>
    private async Task<LoopTurnOutcome> RunLoopTurnAsync(LoopTurnContext ctx, CancellationToken ct)
    {
        Console.Error.WriteLine($"\n[루프 {ctx.Iteration}/{ctx.MaxIterations}] {ctx.Prompt}");
        return await RunSessionAsync(ctx.Prompt, _session, ct).ConfigureAwait(false);
    }

    /// <summary>orchestrator 이벤트 스트림을 소비해 콘솔에 렌더링한다.
    /// 어시스턴트 텍스트는 stdout, 진행/도구/오류 로그는 stderr 로 분리한다.
    /// 반환값은 루프 판정용 성공 여부 — 오류 이벤트나 예외가 있었으면 실패다.</summary>
    private async Task<LoopTurnOutcome> RunSessionAsync(string prompt, AgentSession session, CancellationToken ct)
    {
        try { await _turnGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return LoopTurnOutcome.Fail("취소됨"); }

        string? failure = null;
        try
        {
            await foreach (var ev in _orchestrator.RunAsync(prompt, session, ct: ct).ConfigureAwait(false))
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
                        failure ??= $"{e.Code}: {e.Message}";
                        // 인증 오류만 별도 집계 — 임계값에 닿으면 종료 콜백이 프로세스를 접는다.
                        _authFailures.ObserveErrorCode("Host", e.Code, e.Message);
                        break;
                    case AgentDone done:
                        _authFailures.RecordSuccess();   // 턴이 끝났다 = 인증이 통했다.
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
            return LoopTurnOutcome.Fail("취소됨");
        }
        catch (Exception ex)
        {
            // 턴 하나의 예외로 REPL/루프 전체가 죽으면 안 된다 — 실패로 환원해 루프 정책이 판단하게 한다.
            Console.Error.WriteLine($"[오류] {ex.Message}");
            return LoopTurnOutcome.Fail(ex.Message);
        }
        finally
        {
            _turnGate.Release();
        }

        return failure is null ? LoopTurnOutcome.Ok() : LoopTurnOutcome.Fail(failure);
    }

    /// <summary>루프 진행 상황을 stderr 로 흘린다(stdout 은 어시스턴트 응답 전용).</summary>
    private void OnLoopChanged(object? sender, LoopEvent ev)
    {
        switch (ev)
        {
            case LoopStarted s:
                Console.Error.WriteLine($"[루프] 시작 — {LoopStatusFormatter.DescribeDetailed(s.Status)}");
                break;
            case LoopWaiting w:
                _lastTickSecond = -1;
                var reason = string.IsNullOrWhiteSpace(w.Reason) ? "" : $" ({w.Reason})";
                Console.Error.WriteLine($"[루프] 다음 실행: {LoopIntervalParser.Format(w.Delay)} 뒤{reason}");
                break;
            case LoopTick t:
                // 매초 출력은 로그 폭주라 10초 배수에서만 찍는다.
                var sec = (long)t.Remaining.TotalSeconds;
                if (sec <= 0 || sec % 10 != 0 || sec == _lastTickSecond) break;
                _lastTickSecond = sec;
                Console.Error.WriteLine($"[루프] 반복 {t.Iteration}/{t.MaxIterations} · 남은 시간 {LoopIntervalParser.Format(t.Remaining)}");
                break;
            case LoopNotice n:
                Console.Error.WriteLine($"[루프] {n.Text}");
                break;
            case LoopStopped st:
                Console.Error.WriteLine($"[루프] 종료 — {LoopStatusFormatter.StopReasonText(st.Reason)}, 총 {st.Iterations}회");
                break;
        }
    }
}
