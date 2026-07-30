using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// AgentSessionViewModel 의 /loop 배선 회귀 테스트 — 폭주를 막는 게이트들이 여기 걸려 있다.
/// (창 상태 "항상 위" 영속도 여기 얹었다 — 같은 VM 조립 스텁 한 벌을 공유하기 위해서다.)
/// 루프 엔진 자체(LoopController/LoopPolicy)는 별도 테스트가 덮으므로, 여기서는 VM 이
/// 컨트롤러에 "무엇을 시켰는지"와 이벤트를 "화면 상태로 어떻게 옮겼는지"만 본다.
/// (테스트 프로세스에는 Application.Current 가 없어 UiDispatch 가 인라인 실행된다 → 동기 검증 가능.)
/// </summary>
public class AgentSessionLoopTests
{
    private static (AgentSessionViewModel Vm, FakeLoopController Loop) Build()
    {
        var (vm, loop, _) = BuildWith(null);
        return (vm, loop);
    }

    /// <summary>설정 초기값을 미리 심어 VM 을 만든다(복원 경로 검증용). seed 는 생성자 실행 전에 적용된다.</summary>
    private static (AgentSessionViewModel Vm, FakeLoopController Loop, FakeSettingsService Settings) BuildWith(
        Action<AppSettings>? seed)
    {
        var settings = new FakeSettingsService();
        seed?.Invoke(settings.Current);
        var loop = new FakeLoopController();
        var vm = new AgentSessionViewModel(
            new SilentOrchestrator(),
            new UnusedAgentApi(),
            new NoopPermissionService(),
            new WorkspaceContext(settings),
            settings,
            new EmptyChatHistory(),
            new UnusedAttachmentService(),
            new EmptySuggestions(),
            new AllowAllPolicy(),
            new NoopSessionSync(),
            new TodoService(),
            loop);
        return (vm, loop, settings);
    }

    [Fact]
    public async Task Start_ParsesIntervalAndPrompt_IntoFixedIntervalRequest()
    {
        var (vm, loop) = Build();
        vm.InputText = "/loop 5m 빌드 상태 확인";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.NotNull(loop.LastRequest);
        Assert.Equal(LoopMode.FixedInterval, loop.LastRequest!.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), loop.LastRequest.Interval);
        Assert.Equal("빌드 상태 확인", loop.LastRequest.Prompt);
        Assert.Equal(string.Empty, vm.InputText);   // 커맨드는 모델로 흘러가지 않는다
    }

    [Fact]
    public async Task Start_WithoutInterval_UsesAutonomousPacing()
    {
        var (vm, loop) = Build();
        vm.InputText = "/loop PR 상태 확인해줘";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(LoopMode.Autonomous, loop.LastRequest!.Mode);
        Assert.Null(loop.LastRequest.Interval);
    }

    [Fact]
    public async Task Start_WithEmptyPrompt_DoesNotTouchController()
    {
        var (vm, loop) = Build();
        vm.InputText = "/loop 5m";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Null(loop.LastRequest);
        Assert.Contains(vm.Transcript.OfType<SystemNoticeViewModel>(), n => n.Text.Contains("프롬프트가 필요"));
    }

    [Fact]
    public async Task Stop_Command_StopsControllerWithUserReason()
    {
        var (vm, loop) = Build();
        vm.InputText = "/loop stop";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(LoopStopReason.UserStopped, loop.LastStopReason);
    }

    [Fact]
    public void RunningLoop_BlocksPlainPrompt_ButAllowsSlashCommands()
    {
        var (vm, loop) = Build();
        loop.Raise(new LoopStarted(LoopStatusSnapshot.Idle));

        vm.InputText = "일반 프롬프트";
        Assert.False(vm.SendCommand.CanExecute(null));   // A2 — 세션이 겹치면 이력이 오염된다

        vm.InputText = "/loop stop";
        Assert.True(vm.SendCommand.CanExecute(null));    // 빠져나갈 길은 항상 열려 있어야 한다
    }

    [Fact]
    public void RunningLoop_KeepsStopAffordanceVisible_WhileIdleBetweenTurns()
    {
        var (vm, loop) = Build();
        loop.Raise(new LoopStarted(LoopStatusSnapshot.Idle));

        Assert.False(vm.IsBusy);           // 대기 중 — 턴이 돌고 있지 않다
        Assert.True(vm.ShowStopButton);
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void RunningLoop_KeepsSendAffordanceVisible_SoSlashCommandsStayClickable()
    {
        var (vm, loop) = Build();

        // 평상시 — 전송만.
        Assert.True(vm.ShowSendButton);
        Assert.False(vm.ShowStopButton);

        // 루프 대기 중 — 전송과 중지가 함께 보여야 한다. 전송이 사라지면 CanSend 가 허용하는
        // "/loop stop" 을 마우스로 보낼 수 없어 루프를 멈출 길이 Enter 하나로 줄어든다.
        loop.Raise(new LoopStarted(LoopStatusSnapshot.Idle));
        Assert.True(vm.ShowSendButton);
        Assert.True(vm.ShowStopButton);

        // 턴이 실제로 도는 동안에는 중지만.
        vm.IsBusy = true;
        Assert.False(vm.ShowSendButton);
        Assert.True(vm.ShowStopButton);
    }

    [Fact]
    public void Stopped_Event_ClearsStatus_AndReportsReason()
    {
        var (vm, loop) = Build();
        loop.Raise(new LoopStarted(LoopStatusSnapshot.Idle));
        loop.Raise(new LoopStopped(LoopStopReason.MaxIterationsReached, 50));

        Assert.False(vm.IsLoopRunning);
        Assert.Equal(string.Empty, vm.LoopStatusText);
        Assert.Contains(vm.Transcript.OfType<SystemNoticeViewModel>(),
            n => n.Text.Contains("최대 반복 횟수 도달") && n.Text.Contains("50회"));
    }

    [Fact]
    public void StartLoopCommand_PrefillsComposer_AndAsksForFocus()
    {
        var (vm, _) = Build();
        var focusRequested = false;
        vm.ComposerFocusRequested += (_, _) => focusRequested = true;

        vm.StartLoopCommand.Execute(null);

        Assert.Equal("/loop ", vm.InputText);   // 간격·프롬프트는 사용자가 이어 친다
        Assert.True(focusRequested);            // 프리필만 하고 포커스를 안 넘기면 이어 칠 수 없다
    }

    [Fact]
    public void RequestAttachFileCommand_AsksViewForDialog()
    {
        var (vm, _) = Build();
        var asked = false;
        vm.AttachFileRequested += (_, _) => asked = true;

        vm.RequestAttachFileCommand.Execute(null);

        Assert.True(asked);
    }

    // ── 창 상태(항상 위) 영속 ──────────────────────────────────────────

    [Fact]
    public void ToggleAlwaysOnTop_PersistsToSettings()
    {
        var (vm, _, settings) = BuildWith(null);
        Assert.False(vm.IsAlwaysOnTop);

        vm.ToggleAlwaysOnTopCommand.Execute(null);

        Assert.True(vm.IsAlwaysOnTop);
        Assert.True(settings.Current.AlwaysOnTop);   // 저장이 빠지면 재시작 때 꺼진 채로 뜬다

        vm.ToggleAlwaysOnTopCommand.Execute(null);

        Assert.False(vm.IsAlwaysOnTop);
        Assert.False(settings.Current.AlwaysOnTop);
    }

    [Fact]
    public void AlwaysOnTop_RestoresFromSettings_OnConstruction()
    {
        // 세션 간 유지가 요구사항 — SeedFromSettings 에서 읽지 않으면 조용히 항상 꺼진 상태로 시작한다.
        var (vm, _, _) = BuildWith(s => s.AlwaysOnTop = true);

        Assert.True(vm.IsAlwaysOnTop);
    }

    [Fact]
    public async Task AlwaysOnTop_FollowsExternalSettingsChange()
    {
        // 설정창/다른 경로로 설정이 바뀌면 SettingsChanged 로 따라와야 한다(복원 경로 재사용 확인).
        var (vm, _, settings) = BuildWith(null);

        await settings.UpdateAlwaysOnTopAsync(true);

        Assert.True(vm.IsAlwaysOnTop);
    }

    // ── 에이전트 런처 창(별도 창) 배선 ────────────────────────────────
    //
    // 런처는 타일을 누르면 "창을 닫고 나서" 실행해야 한다. 순서가 뒤집히면
    //   · 첨부 대화상자(메인 창 소유)가 런처 뒤로 숨고,
    //   · /loop·/retry 의 포커스 요청이 비활성 창으로 날아가 사용자가 이어 칠 수 없다.
    // 둘 다 실행해 봐야만 보이는 무증상 결함이라 순서를 여기서 못박는다.

    private static (AgentSessionViewModel Session, AgentLauncherViewModel Launcher) BuildLauncher()
    {
        var (vm, _) = Build();
        return (vm, new AgentLauncherViewModel(vm));
    }

    [Fact]
    public void OpenAgentLauncher_RaisesRequest_ForTheViewLayer()
    {
        var (vm, _) = Build();
        var asked = 0;
        vm.AgentLauncherRequested += (_, _) => asked++;

        vm.OpenAgentLauncherCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.Empty(vm.Transcript);   // 정상 경로에서는 안내를 남기지 않는다
    }

    [Fact]
    public void OpenAgentLauncher_WithoutSubscriber_LeavesVisibleNotice()
    {
        // App 배선(코디네이터)이 빠지면 조용히 아무 일도 안 나는 대신 화면에 흔적이 남아야 한다.
        var (vm, _) = Build();

        vm.OpenAgentLauncherCommand.Execute(null);

        var notice = Assert.IsType<SystemNoticeViewModel>(Assert.Single(vm.Transcript));
        Assert.Contains("런처", notice.Text);
    }

    [Fact]
    public void LaunchAttach_ClosesLauncherBeforeAskingForDialog()
    {
        var (session, launcher) = BuildLauncher();
        var order = new List<string>();
        launcher.CloseRequested += (_, _) => order.Add("close");
        session.AttachFileRequested += (_, _) => order.Add("dialog");

        launcher.LaunchAttachCommand.Execute(null);

        // 순서가 계약이다 — 대화상자가 런처 뒤에 숨는 것을 막는 유일한 장치.
        Assert.Equal(new[] { "close", "dialog" }, order);
    }

    [Fact]
    public void LaunchLoop_ClosesLauncherBeforeAskingForComposerFocus()
    {
        var (session, launcher) = BuildLauncher();
        var order = new List<string>();
        launcher.CloseRequested += (_, _) => order.Add("close");
        session.ComposerFocusRequested += (_, _) => order.Add("focus");

        launcher.LaunchLoopCommand.Execute(null);

        Assert.Equal(new[] { "close", "focus" }, order);
        Assert.Equal("/loop ", session.InputText);   // 세션 커맨드 재사용 확인(런처가 동작을 복제하지 않는다)
    }

    [Fact]
    public void LaunchRetry_ReusesSessionCommand()
    {
        var (session, launcher) = BuildLauncher();
        var closed = false;
        launcher.CloseRequested += (_, _) => closed = true;

        launcher.LaunchRetryCommand.Execute(null);

        Assert.True(closed);
        // 되불러올 메시지가 없으면 세션 VM 이 안내만 남긴다 — 런처가 그 판단을 가로채지 않는다.
        Assert.Single(session.Transcript);
    }

    [Fact]
    public void LaunchHelp_ClosesLauncher_AndWritesHelpToTranscript()
    {
        var (session, launcher) = BuildLauncher();
        var closed = false;
        launcher.CloseRequested += (_, _) => closed = true;

        launcher.LaunchHelpCommand.Execute(null);

        Assert.True(closed);
        Assert.Single(session.Transcript);
    }

    [Fact]
    public async Task LaunchNewChat_ClosesLauncher_AndClearsSession()
    {
        var (session, launcher) = BuildLauncher();
        session.Transcript.Add(new SystemNoticeViewModel { Text = "이전 대화" });
        var closed = false;
        launcher.CloseRequested += (_, _) => closed = true;

        launcher.LaunchNewChatCommand.Execute(null);
        await session.ClearCommand.ExecutionTask!;   // ClearCommand 는 비동기 — 완료를 기다려야 단정이 성립한다

        Assert.True(closed);
        Assert.Empty(session.Transcript);
    }

    [Fact]
    public void LaunchAttach_IsDisabledWhileBusy_AndDoesNotCloseLauncher()
    {
        var (session, launcher) = BuildLauncher();
        var canExecuteChanged = 0;
        launcher.LaunchAttachCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        var closed = false;
        launcher.CloseRequested += (_, _) => closed = true;
        var asked = false;
        session.AttachFileRequested += (_, _) => asked = true;

        Assert.True(launcher.LaunchAttachCommand.CanExecute(null));

        session.IsBusy = true;

        // 안쪽 커맨드(!IsBusy)의 판정이 타일까지 전달되지 않으면 타일이 눌리는 것처럼 보인다.
        Assert.False(launcher.LaunchAttachCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);

        launcher.LaunchAttachCommand.Execute(null);

        Assert.False(closed);   // 실행 못 할 타일은 창도 닫지 않는다
        Assert.False(asked);
    }

    // ── 작업 관리(태스크 매니저) 진입 경로 2개 ────────────────────────────
    //
    // 창을 여는 방법은 세션 VM 한 곳(OpenTaskManagerCommand)에만 둔다. 런처 6번째 타일은 그 커맨드를
    // Launch 래퍼로 감싸 재사용하므로 App 배선도 한 곳이고, 배선 누락 안내도 한 번만 정의된다.
    // 상단바 칩은 등기소의 구조 변화만 구독한다(경과 시간용 1초 타이머는 창의 몫이다).

    [Fact]
    public void OpenTaskManager_RaisesRequest_ForTheViewLayer()
    {
        var (vm, _) = Build();
        var asked = 0;
        vm.TaskManagerRequested += (_, _) => asked++;

        vm.OpenTaskManagerCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.Empty(vm.Transcript);
    }

    [Fact]
    public void OpenTaskManager_WithoutSubscriber_LeavesVisibleNotice()
    {
        // App 배선(코디네이터)이 빠지면 조용히 아무 일도 안 나는 대신 화면에 흔적이 남아야 한다.
        var (vm, _) = Build();

        vm.OpenTaskManagerCommand.Execute(null);

        var notice = Assert.IsType<SystemNoticeViewModel>(Assert.Single(vm.Transcript));
        Assert.Contains("작업 관리", notice.Text);
    }

    [Fact]
    public void LaunchTaskManager_ClosesLauncherBeforeOpeningTheWindow()
    {
        var (session, launcher) = BuildLauncher();
        var order = new List<string>();
        launcher.CloseRequested += (_, _) => order.Add("close");
        session.TaskManagerRequested += (_, _) => order.Add("open");

        launcher.LaunchTaskManagerCommand.Execute(null);

        // 런처가 열린 채면 두 소유 창이 겹쳐 목록 창을 덮는다 — 순서가 계약이다(앞의 5개와 동일).
        Assert.Equal(new[] { "close", "open" }, order);
    }

    [Fact]
    public void ActiveTaskChip_TracksTheRegistry_AndHidesWhenIdle()
    {
        var (vm, _) = Build();
        var registry = new AgentActivityRegistry();
        vm.AttachActivityRegistry(registry);

        Assert.False(vm.HasActiveTasks);   // 평소 "진행 0" 이 상주하면 칩이 정보가 아니라 배경이 된다

        using (registry.BeginTurn(CancellationToken.None, 25))
        {
            Assert.Equal(1, vm.ActiveTaskCount);
            Assert.True(vm.HasActiveTasks);
            Assert.Equal("진행 1", vm.ActiveTaskChipText);
        }

        // 구조 변화(해제)가 즉시 반영돼야 한다 — 안 그러면 끝난 뒤에도 칩이 남는다.
        Assert.Equal(0, vm.ActiveTaskCount);
        Assert.False(vm.HasActiveTasks);
    }

    [Fact]
    public void ActiveTaskChip_WithoutRegistry_StaysHidden()
    {
        // 등기소 배선이 없어도(테스트·헤드리스 조립) 기존 동작이 깨지지 않아야 한다.
        var (vm, _) = Build();

        Assert.Equal(0, vm.ActiveTaskCount);
        Assert.False(vm.HasActiveTasks);
    }

    [Fact]
    public void Dispose_DisposesController_AndStopsListening()
    {
        var (vm, loop) = Build();
        vm.Dispose();

        Assert.True(loop.Disposed);

        // 구독이 남아 있으면 종료 후 이벤트가 죽은 VM 의 전사를 건드린다.
        loop.Raise(new LoopStopped(LoopStopReason.Disposed, 1));
        Assert.Empty(vm.Transcript);
    }

    // ── 이 테스트 전용 스텁들 (Fakes.cs 와 충돌하지 않도록 여기 둔다) ──

    private sealed class FakeLoopController : ILoopController
    {
        public LoopStartRequest? LastRequest { get; private set; }
        public LoopStopReason? LastStopReason { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsRunning { get; private set; }
        public LoopStatusSnapshot Status { get; private set; } = LoopStatusSnapshot.Idle;
        public event EventHandler<LoopEvent>? LoopChanged;

        public bool TryStart(LoopStartRequest request, LoopTurnRunner runner, CancellationToken externalCt, out string? error)
        {
            LastRequest = request;
            IsRunning = true;
            error = null;
            return true;
        }

        public void Stop(LoopStopReason reason) { LastStopReason = reason; IsRunning = false; }
        public Task StopAndWaitAsync(LoopStopReason reason, TimeSpan? timeout = null) { Stop(reason); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;

        /// <summary>컨트롤러가 백그라운드에서 발화하는 이벤트를 흉내낸다.</summary>
        public void Raise(LoopEvent e) => LoopChanged?.Invoke(this, e);
    }

    private sealed class SilentOrchestrator : IAgentOrchestrator
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(
            string userGoal, AgentSession session, IReadOnlyList<Attachment>? attachments = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            int? maxIterations = null)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnusedAgentApi : StubAgentApi;

    private sealed class NoopPermissionService : IPermissionService
    {
        public Task<PermissionDecision> RequestAsync(ToolCall call, ToolRisk risk, ToolContext ctx, CancellationToken ct = default)
            => Task.FromResult(PermissionDecision.Allow);
        public void SetApprovalHandler(Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>> handler) { }
        public void ClearSessionRules() { }
    }

    private sealed class EmptyChatHistory : IChatHistoryService
    {
        public Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatSessionSummary>>([]);
        public Task<ChatSessionRecord?> LoadAsync(string id, CancellationToken ct = default)
            => Task.FromResult<ChatSessionRecord?>(null);
        public Task SaveAsync(ChatSessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public ChatSessionRecord CreateNew(string? workspaceRoot = null, string? projectId = null)
            => new() { Id = Guid.NewGuid().ToString(), Title = "새 대화" };
        public string BuildTitle(IReadOnlyList<AgentMessage> messages) => "제목";
    }

    private sealed class UnusedAttachmentService : IFileAttachmentService
    {
        public Attachment CreateFromPath(string path) => throw new NotSupportedException();
        public Task<string> ReadAsBase64Async(Attachment attachment, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class EmptySuggestions : ISuggestionService
    {
        public Task<IReadOnlyList<Suggestion>> GetSuggestionsAsync(string workspaceRoot, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Suggestion>>([]);
    }

    private sealed class NoopSessionSync : ISessionSyncService
    {
        public Task PushAsync(ChatSessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PullMergeAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
