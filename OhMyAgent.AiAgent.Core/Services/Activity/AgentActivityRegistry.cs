using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// <see cref="IAgentActivityRegistry"/> 기본 구현.
///
/// 자료구조 선택 근거 — 등록·해제가 <b>도구 병렬 실행 8개 + 턴 스레드 + 루프 스레드</b>에서 동시에 들어온다:
///  - 노드 표 = <see cref="ConcurrentDictionary{TKey,TValue}"/>. 등록/해제가 락 없이 끝나야 계측이
///    실행 경로의 병목이 되지 않는다(도구 실행마다 최소 2회 쓰기). 열거는 약한 일관성이지만
///    스냅샷은 "그 순간의 화면"이면 충분하므로 그 성질이 오히려 맞다.
///  - 노드 내부의 변하는 값(반복 횟수·취소 요청 시각·상태·CTS)만 노드별 락으로 보호한다.
///    표 전체를 잠그면 8개 병렬 도구가 서로를 기다리고, 락 없이 두면 취소와 해제가 경합해
///    <see cref="ObjectDisposedException"/> 이 미관측 예외로 튄다.
///  - 완료 이력 = 고정 용량 큐(<see cref="ConcurrentQueue{T}"/> + 상한 트림). 무한히 자라지 않아야 한다.
///
/// 파생값(경과·건강도·고아 여부)은 <b>저장하지 않고</b> 스냅샷마다 다시 계산한다 — 시간이 흐르면 답이
/// 바뀌는 값을 상태로 두면 최신화 누락이 반드시 생긴다.
/// </summary>
public sealed class AgentActivityRegistry : IAgentActivityRegistry
{
    /// <summary>완료 이력 보관 개수. "내 중지가 먹었나"를 확인하는 용도라 최근 것만 있으면 된다.</summary>
    private const int MaxHistory = 30;

    /// <summary>
    /// 프로세스 추적 상한. 헤드리스처럼 <see cref="Snapshot"/> 을 아무도 호출하지 않는 호스트에서도
    /// 표가 무한히 자라지 않도록, 등록 시점에 상한을 넘으면 죽은 항목을 먼저 걷어낸다.
    /// </summary>
    private const int MaxTrackedProcesses = 64;

    private readonly ConcurrentDictionary<Guid, Node> _nodes = new();
    private readonly ConcurrentQueue<AgentActivityHistoryEntry> _history = new();
    private readonly AsyncLocal<Guid?> _owner = new();
    private readonly IProcessProbe _probe;
    private readonly Func<DateTimeOffset> _now;
    private long _seq;

    /// <summary>
    /// <paramref name="probe"/> 와 <paramref name="clock"/> 를 주입 가능하게 둔 이유는 테스트다 —
    /// 실제 프로세스를 죽이지 않고 고아 판정·정리 전 과정을 검증할 수 있어야 한다
    /// (<see cref="Loop.LoopController"/> 가 <see cref="Loop.ILoopClock"/> 을 받는 것과 같은 관례).
    /// </summary>
    public AgentActivityRegistry(IProcessProbe? probe = null, Func<DateTimeOffset>? clock = null)
    {
        _probe = probe ?? new SystemProcessProbe();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? Changed;

    // ── 계측 ────────────────────────────────────────────────────────────────

    public IAgentActivityScope BeginTurn(CancellationToken parentToken, int maxIterations)
    {
        var node = new Node(
            NextSeq(), AgentActivityKind.Turn, _owner.Value, "에이전트 턴", null,
            risk: null, process: null, _now())
        {
            MaxIterations = maxIterations,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken),
        };

        _nodes[node.Id] = node;
        Raise();
        return new Scope(this, node);
    }

    public IAgentActivityScope BeginTool(
        Guid? parentId, CancellationToken parentToken, string toolName, ToolRisk risk, JsonElement arguments)
    {
        var node = new Node(
            NextSeq(), AgentActivityKind.Tool, parentId,
            string.IsNullOrWhiteSpace(toolName) ? "(이름 없는 도구)" : toolName,
            ActivityLabel.Summarize(arguments), risk, process: null, _now())
        {
            Cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken),
        };

        _nodes[node.Id] = node;
        Raise();
        return new Scope(this, node);
    }

    public IAgentActivityScope TrackChildProcess(TrackedProcessIdentity identity, string? detail)
    {
        var node = NewProcessNode(identity, detail);
        _nodes[node.Id] = node;
        Raise();
        return new Scope(this, node);
    }

    public void TrackDetachedProcess(TrackedProcessIdentity identity, string? detail)
    {
        // 해제 시점이 없는 항목이라 등록 자체가 누수 위험이다 — 상한을 넘으면 먼저 죽은 것을 걷어낸다.
        if (CountProcesses() >= MaxTrackedProcesses)
            PruneProcesses();

        var node = NewProcessNode(identity, detail);
        _nodes[node.Id] = node;
        Raise();
    }

    public IDisposable EnterOwner(Guid activityId)
    {
        var previous = _owner.Value;
        _owner.Value = activityId;
        return new OwnerScope(this, previous);
    }

    // ── 제어 ────────────────────────────────────────────────────────────────

    public bool RequestCancel(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node)) return false;
        if (!node.TryRequestCancel(_now(), out var changed)) return false;

        if (changed) Raise();
        return true;
    }

    public int CancelAll()
    {
        var signalled = 0;
        // 턴을 먼저 취소하면 연결된 도구 토큰이 함께 풀린다. 그래도 전부 순회하는 이유는
        // 연결 밖에서 등록된 항목(주변 소유자 없이 시작된 턴 등)을 빠뜨리지 않기 위해서다.
        foreach (var node in _nodes.Values.OrderBy(n => n.Kind))
        {
            if (node.TryRequestCancel(_now(), out _)) signalled++;
        }

        if (signalled > 0) Raise();
        return signalled;
    }

    public ProcessKillOutcome KillChildProcess(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node) || node.Process is null)
            return ProcessKillOutcome.NotTracked;

        var identity = node.Process;
        var verdict = ProcessIdentityRules.Verify(identity, _probe.Observe(identity.Pid));

        switch (verdict)
        {
            case TrackedProcessVerdict.Exited:
                Finish(node, AgentActivityState.Completed, "이미 종료된 프로세스였습니다.");
                return ProcessKillOutcome.AlreadyExited;

            case TrackedProcessVerdict.Recycled:
                // PID 가 다른 프로세스에 재사용됐다 — 남의 프로세스이므로 손대지 않고 추적만 버린다.
                Finish(node, AgentActivityState.Completed, "PID 가 재사용되어 추적만 해제했습니다(종료하지 않음).");
                return ProcessKillOutcome.IdentityMismatch;

            case TrackedProcessVerdict.Unverifiable:
                return ProcessKillOutcome.Unverifiable;
        }

        if (!_probe.TryKill(identity.Pid, out var error))
        {
            AppLog.Warn("Activity", $"자식 프로세스 종료 실패(pid {identity.Pid}): {error}");
            return ProcessKillOutcome.Failed;
        }

        Finish(node, AgentActivityState.Canceled, $"사용자가 프로세스를 강제 종료했습니다(pid {identity.Pid}).");
        return ProcessKillOutcome.Killed;
    }

    public OrphanSweepResult SweepOrphans()
    {
        var notes = new List<string>();
        int inspected = 0, killed = 0, exited = 0, skipped = 0;

        foreach (var node in _nodes.Values.Where(n => n.Process is not null).OrderBy(n => n.Seq))
        {
            var identity = node.Process!;
            var ownerAlive = node.ParentId is { } p && _nodes.ContainsKey(p);
            var verdict = ProcessIdentityRules.Verify(identity, _probe.Observe(identity.Pid));

            if (verdict == TrackedProcessVerdict.Exited)
            {
                exited++;
                Finish(node, AgentActivityState.Completed, "프로세스가 이미 종료되었습니다.");
                continue;
            }

            if (verdict == TrackedProcessVerdict.Recycled)
            {
                skipped++;
                notes.Add($"pid {identity.Pid}: PID 가 재사용됨 — 다른 프로세스이므로 종료하지 않고 추적만 해제했습니다.");
                Finish(node, AgentActivityState.Completed, "PID 재사용 — 추적만 해제(종료하지 않음).");
                continue;
            }

            if (!OrphanRules.IsOrphan(node.Kind, ownerAlive, verdict))
            {
                // 소유 작업이 아직 살아 있거나 신원을 확인할 수 없는 항목은 고아가 아니다 — 남겨 둔다.
                if (verdict == TrackedProcessVerdict.Unverifiable)
                {
                    skipped++;
                    notes.Add($"pid {identity.Pid} ({identity.ProcessName}): 시작 시각을 확인할 수 없어 건드리지 않았습니다.");
                }
                continue;
            }

            inspected++;
            if (_probe.TryKill(identity.Pid, out var error))
            {
                killed++;
                notes.Add($"pid {identity.Pid} ({identity.ProcessName}): 정리했습니다.");
                Finish(node, AgentActivityState.Canceled, "고아 프로세스 정리로 종료했습니다.");
            }
            else
            {
                skipped++;
                notes.Add($"pid {identity.Pid} ({identity.ProcessName}): 종료 실패 — {error}");
            }
        }

        return new OrphanSweepResult(inspected, killed, exited, skipped, notes);
    }

    // ── 스냅샷 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 화면 1장을 만든다. 부수효과가 하나 있다 — <b>이미 종료된 프로세스 항목을 걷어낸다</b>.
    /// 여기서 <see cref="Changed"/> 를 발화하지 않는 이유: 구독자가 다시 스냅샷을 뜨면 무한 왕복이 된다.
    /// 반환값에 이미 정리 결과가 반영돼 있으므로 알릴 필요도 없다.
    /// </summary>
    public AgentActivityView Snapshot()
    {
        var now = _now();

        // 프로세스 항목은 관측을 한 번만 한다 — 아래 판정(생존·고아)이 같은 관측을 공유해야
        // 같은 화면 안에서 서로 다른 결론이 나오지 않는다.
        var verdicts = new Dictionary<Guid, TrackedProcessVerdict>();
        foreach (var node in _nodes.Values)
        {
            if (node.Process is null) continue;
            var verdict = ProcessIdentityRules.Verify(node.Process, _probe.Observe(node.Process.Pid));
            if (verdict is TrackedProcessVerdict.Exited or TrackedProcessVerdict.Recycled)
            {
                Finish(node, AgentActivityState.Completed, verdict == TrackedProcessVerdict.Exited
                    ? "프로세스가 종료되었습니다."
                    : "PID 재사용 — 추적만 해제(종료하지 않음).", raise: false);
                continue;
            }
            verdicts[node.Id] = verdict;
        }

        var all = _nodes.Values.OrderBy(n => n.Seq).ToList();
        var byId = new Dictionary<Guid, Node>(all.Count);
        foreach (var n in all) byId[n.Id] = n;

        var children = new Dictionary<Guid, List<Node>>();
        var roots = new List<Node>();
        foreach (var n in all)
        {
            // 부모가 이미 사라진 항목(= 고아 후보)도 목록에서 빠지면 안 된다 — 루트로 올려 반드시 보이게 한다.
            if (n.ParentId is { } pid && byId.ContainsKey(pid))
            {
                if (!children.TryGetValue(pid, out var list))
                {
                    list = [];
                    children[pid] = list;
                }
                list.Add(n);
            }
            else
            {
                roots.Add(n);
            }
        }

        var items = new List<AgentActivitySnapshot>(all.Count);
        var visited = new HashSet<Guid>();
        foreach (var root in roots)
            Emit(root, 0);

        // 이력은 최신이 위로 — 방금 끝난 것(특히 방금 취소한 것)을 사용자가 가장 먼저 봐야 한다.
        return new AgentActivityView(items, _history.Reverse().ToArray(), now);

        void Emit(Node node, int depth)
        {
            // 부모 체인이 깨져 순환이 생기면 UI 가 멈춘다 — 값싼 보호막을 둔다(정상 경로에서는 걸리지 않는다).
            if (!visited.Add(node.Id)) return;

            items.Add(node.ToSnapshot(
                now, depth,
                ownerAlive: node.ParentId is { } p && byId.ContainsKey(p),
                verdict: verdicts.TryGetValue(node.Id, out var v) ? v : (TrackedProcessVerdict?)null));

            if (!children.TryGetValue(node.Id, out var list)) return;
            foreach (var child in list) Emit(child, depth + 1);
        }
    }

    // ── 내부 ────────────────────────────────────────────────────────────────

    private long NextSeq() => Interlocked.Increment(ref _seq);

    private int CountProcesses()
    {
        var n = 0;
        foreach (var node in _nodes.Values)
            if (node.Process is not null) n++;
        return n;
    }

    /// <summary>죽은 프로세스 항목을 걷어낸다(상한 도달 시). 살아 있는 것은 절대 건드리지 않는다.</summary>
    private void PruneProcesses()
    {
        foreach (var node in _nodes.Values)
        {
            if (node.Process is null) continue;
            var verdict = ProcessIdentityRules.Verify(node.Process, _probe.Observe(node.Process.Pid));
            if (verdict is TrackedProcessVerdict.Exited or TrackedProcessVerdict.Recycled)
                Finish(node, AgentActivityState.Completed, "추적 상한 정리 — 이미 우리 프로세스가 아닙니다.", raise: false);
        }
    }

    /// <summary>항목을 제거하고 이력에 남긴다. 두 번 불려도 안전하다(스코프 Dispose 와 경합할 수 있다).</summary>
    private void Finish(Node node, AgentActivityState state, string? note, bool raise = true)
    {
        if (!node.TryClose()) return;
        _nodes.TryRemove(node.Id, out _);

        var finished = _now();
        _history.Enqueue(new AgentActivityHistoryEntry(
            node.Id, node.Kind, node.Title, node.Detail, node.StartedAtUtc, finished,
            finished - node.StartedAtUtc, state, note));

        while (_history.Count > MaxHistory) _history.TryDequeue(out _);

        node.DisposeCts();
        if (raise) Raise();
    }

    private Node NewProcessNode(TrackedProcessIdentity identity, string? detail)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var title = string.IsNullOrWhiteSpace(identity.ProcessName)
            ? $"pid {identity.Pid}"
            : $"{identity.ProcessName} (pid {identity.Pid})";

        return new Node(
            NextSeq(), AgentActivityKind.ChildProcess, _owner.Value, title,
            detail is null ? null : ActivityLabel.Truncate(detail),
            risk: null, process: identity, _now());
    }

    private void Raise()
    {
        var handler = Changed;
        if (handler is null) return;

        // 구독자(UI 마샬 등) 예외가 실행 경로를 죽이면 안 된다 — 표시 실패는 작업 중단 사유가 아니다
        // (LoopController.Emit 과 같은 정책).
        try { handler(this, EventArgs.Empty); }
        catch (Exception ex) { AppLog.Warn("Activity", $"태스크 매니저 구독자 예외: {ex.Message}"); }
    }

    /// <summary>주변 소유자 복원 핸들. Dispose 로 이전 값을 되돌린다.</summary>
    private sealed class OwnerScope(AgentActivityRegistry registry, Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            registry._owner.Value = previous;
        }
    }

    /// <summary>레지스트리 내부 노드. 절대 밖으로 노출하지 않는다(밖에는 불변 스냅샷만 나간다).</summary>
    private sealed class Node
    {
        private readonly object _gate = new();
        private int _iteration;
        private DateTimeOffset? _cancelRequestedAt;
        private AgentActivityState _state = AgentActivityState.Running;
        private bool _closed;
        private bool _ctsDisposed;

        public Node(
            long seq, AgentActivityKind kind, Guid? parentId, string title, string? detail,
            ToolRisk? risk, TrackedProcessIdentity? process, DateTimeOffset startedAtUtc)
        {
            Seq = seq;
            Kind = kind;
            ParentId = parentId;
            Title = title;
            Detail = detail;
            Risk = risk;
            Process = process;
            StartedAtUtc = startedAtUtc;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public long Seq { get; }
        public AgentActivityKind Kind { get; }
        public Guid? ParentId { get; }
        public string Title { get; }
        public string? Detail { get; }
        public ToolRisk? Risk { get; }
        public TrackedProcessIdentity? Process { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public int MaxIterations { get; init; }
        public CancellationTokenSource? Cts { get; init; }

        public CancellationToken Token
        {
            get
            {
                lock (_gate)
                {
                    // 이미 정리된 CTS 의 Token 을 읽으면 던진다 — 경합 시 None 으로 환원한다.
                    if (Cts is null || _ctsDisposed) return CancellationToken.None;
                    try { return Cts.Token; }
                    catch (ObjectDisposedException) { return CancellationToken.None; }
                }
            }
        }

        public void ReportIteration(int iteration)
        {
            lock (_gate) _iteration = iteration;
        }

        /// <summary>
        /// 취소를 요청한다. 첫 요청 시각만 남긴다 — 연타로 시각이 갱신되면 "얼마나 안 멈추고 있는지"가 리셋된다.
        /// 토큰이 없는 항목(자식 프로세스)은 false(= 취소 불가, 강제 종료만 가능).
        /// </summary>
        public bool TryRequestCancel(DateTimeOffset now, out bool changed)
        {
            changed = false;
            CancellationTokenSource? cts;
            lock (_gate)
            {
                if (Cts is null || _closed) return false;
                if (_cancelRequestedAt is null)
                {
                    _cancelRequestedAt = now;
                    _state = AgentActivityState.CancelRequested;
                    changed = true;
                }
                if (_ctsDisposed) return true;
                cts = Cts;
            }

            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* 스코프가 막 정리됨 — 취소는 무의미하다 */ }
            catch (AggregateException ex)
            {
                // 등록된 콜백이 던진 예외까지 여기로 온다. 취소 요청 자체는 성공했으므로 삼키고 기록만 한다.
                AppLog.Warn("Activity", $"취소 콜백 예외: {ex.Message}");
            }

            return true;
        }

        /// <summary>제거를 한 번만 진행시킨다(스코프 Dispose 와 프루닝이 동시에 들어올 수 있다).</summary>
        public bool TryClose()
        {
            lock (_gate)
            {
                if (_closed) return false;
                _closed = true;
                return true;
            }
        }

        public void DisposeCts()
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                if (_ctsDisposed) return;
                _ctsDisposed = true;
                cts = Cts;
            }
            try { cts?.Dispose(); }
            catch (Exception) { /* 정리 경로 — 더 할 일이 없다 */ }
        }

        /// <summary>토큰 상태로 종료 상태를 추정한다(명시 호출이 없었을 때).</summary>
        public AgentActivityState InferFinalState()
        {
            lock (_gate)
            {
                if (_cancelRequestedAt is not null) return AgentActivityState.Canceled;
                if (_ctsDisposed || Cts is null) return AgentActivityState.Completed;
            }
            try { return Cts!.IsCancellationRequested ? AgentActivityState.Canceled : AgentActivityState.Completed; }
            catch (ObjectDisposedException) { return AgentActivityState.Completed; }
        }

        public AgentActivitySnapshot ToSnapshot(
            DateTimeOffset now, int depth, bool ownerAlive, TrackedProcessVerdict? verdict)
        {
            int iteration;
            DateTimeOffset? cancelAt;
            AgentActivityState state;
            lock (_gate)
            {
                iteration = _iteration;
                cancelAt = _cancelRequestedAt;
                state = _state;
            }

            var elapsed = now - StartedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            var cancelFor = cancelAt is { } at ? now - at : (TimeSpan?)null;
            if (cancelFor is { } cf && cf < TimeSpan.Zero) cancelFor = TimeSpan.Zero;

            return new AgentActivitySnapshot(
                Id, ParentId, Kind, Title, Detail, Risk,
                Pid: Process?.Pid,
                Iteration: Kind == AgentActivityKind.Turn ? iteration : null,
                MaxIterations: Kind == AgentActivityKind.Turn ? MaxIterations : null,
                StartedAtUtc: StartedAtUtc,
                Elapsed: elapsed,
                CancelRequestedFor: cancelFor,
                State: state,
                Health: ActivityHealthRules.Classify(Kind, elapsed, cancelFor),
                CanCancel: Cts is not null && cancelAt is null,
                // 신원이 확인된 프로세스만 강제 종료 대상이다 — 확인 불가/재사용은 버튼을 주지 않는다.
                CanKill: Process is not null && verdict == TrackedProcessVerdict.Alive,
                IsOrphanCandidate: verdict is { } v && OrphanRules.IsOrphan(Kind, ownerAlive, v),
                Depth: depth);
        }
    }

    /// <summary>등록 해제 핸들. Dispose 를 빠뜨리면 유령 항목이 남으므로 호출자는 반드시 <c>using</c> 을 쓴다.</summary>
    private sealed class Scope(AgentActivityRegistry registry, Node node) : IAgentActivityScope
    {
        private AgentActivityState? _explicitState;
        private string? _note;
        private bool _disposed;

        public Guid Id => node.Id;
        public CancellationToken Token => node.Token;

        public void ReportIteration(int iteration)
        {
            node.ReportIteration(iteration);
            registry.Raise();
        }

        public void Complete(AgentActivityState state, string? note = null)
        {
            _explicitState = state;
            _note = note;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            registry.Finish(node, _explicitState ?? node.InferFinalState(), _note);
        }
    }
}
