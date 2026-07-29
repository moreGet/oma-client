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
/// 루프 엔진 자체(LoopController/LoopPolicy)는 별도 테스트가 덮으므로, 여기서는 VM 이
/// 컨트롤러에 "무엇을 시켰는지"와 이벤트를 "화면 상태로 어떻게 옮겼는지"만 본다.
/// (테스트 프로세스에는 Application.Current 가 없어 UiDispatch 가 인라인 실행된다 → 동기 검증 가능.)
/// </summary>
public class AgentSessionLoopTests
{
    private static (AgentSessionViewModel Vm, FakeLoopController Loop) Build()
    {
        var settings = new FakeSettingsService();
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
        return (vm, loop);
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
    public void StartLoopCommand_PrefillsComposer_AndClosesDrawer()
    {
        var (vm, _) = Build();
        vm.IsAgentDrawerOpen = true;
        var focusRequested = false;
        vm.ComposerFocusRequested += (_, _) => focusRequested = true;

        vm.StartLoopCommand.Execute(null);

        Assert.Equal("/loop ", vm.InputText);   // 간격·프롬프트는 사용자가 이어 친다
        Assert.False(vm.IsAgentDrawerOpen);
        Assert.True(focusRequested);
    }

    [Fact]
    public void RequestAttachFileCommand_AsksViewForDialog_AndClosesDrawer()
    {
        var (vm, _) = Build();
        vm.IsAgentDrawerOpen = true;
        var asked = false;
        vm.AttachFileRequested += (_, _) => asked = true;

        vm.RequestAttachFileCommand.Execute(null);

        Assert.True(asked);
        Assert.False(vm.IsAgentDrawerOpen);
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
