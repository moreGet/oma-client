using System;
using System.Text.Json;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 항목 강조 판정. 시간이 흐르면 답이 바뀌는 파생값이라 저장하지 않고 매 스냅샷마다 다시 계산한다.
/// 순수 함수로 떼어 둔 이유는 단위 테스트로 임계값과 우선순위를 못박기 위해서다.
/// </summary>
public static class ActivityHealthRules
{
    /// <summary>도구 1건이 이 시간을 넘기면 "오래 걸림"으로 강조한다. 대다수 도구는 1초 안에 끝난다.</summary>
    public static readonly TimeSpan ToolLongRunning = TimeSpan.FromSeconds(60);

    /// <summary>턴은 모델 왕복 + 도구 여러 개를 포함하므로 임계값이 훨씬 크다.</summary>
    public static readonly TimeSpan TurnLongRunning = TimeSpan.FromMinutes(5);

    /// <summary>자식 프로세스는 원래 오래 사는 것도 정상(에디터·서버)이라 가장 관대하게 본다.</summary>
    public static readonly TimeSpan ProcessLongRunning = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 취소 요청 후 이만큼 지나도 안 끝나면 "취소가 안 먹는 항목"으로 표시한다.
    ///
    /// 왜 5초인가: 협조적 취소는 다음 <c>await</c>/<c>ThrowIfCancellationRequested</c> 에서 풀리므로
    /// 정상 코드라면 1~2초 안에 접힌다. 그보다 오래 남는다는 것은 토큰을 보지 않는 동기 블로킹 구간
    /// (예: 응답 없는 자식 프로세스 대기)에 갇힌 것이고, 그때 실제로 손쓸 수 있는 유일한 수단은
    /// 그 아래 자식 프로세스를 강제 종료하는 것이다. 사용자가 그 사실을 알아야 하므로 표시한다.
    /// </summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 우선순위가 중요하다 — 취소 상태가 소요 시간보다 앞선다. 취소를 눌렀는데 "오래 걸림"만 보이면
    /// 사용자는 자기 클릭이 접수됐는지 알 수 없다.
    /// </summary>
    public static AgentActivityHealth Classify(
        AgentActivityKind kind, TimeSpan elapsed, TimeSpan? cancelRequestedFor)
    {
        if (cancelRequestedFor is { } since)
            return since >= CancelGrace ? AgentActivityHealth.CancelStalled : AgentActivityHealth.CancelPending;

        var threshold = kind switch
        {
            AgentActivityKind.Tool => ToolLongRunning,
            AgentActivityKind.Turn => TurnLongRunning,
            _ => ProcessLongRunning,
        };

        return elapsed >= threshold ? AgentActivityHealth.LongRunning : AgentActivityHealth.Normal;
    }
}

/// <summary>
/// 추적 중인 PID 의 동일성 판정 — <b>이 파일이 "남의 프로세스를 죽이지 않는다"는 보증의 전부다.</b>
/// 순수 함수라 프로세스를 하나도 띄우지 않고 전수 테스트할 수 있다.
/// </summary>
public static class ProcessIdentityRules
{
    /// <summary>
    /// 시작 시각 비교 허용 오차. <c>Process.StartTime</c> 은 조회 경로·시간대 변환에 따라 밀리초 단위가
    /// 미세하게 흔들릴 수 있어 정확히 같기를 요구하면 자기 프로세스도 "재사용"으로 오판한다.
    /// 반대로 크게 잡으면 안 된다 — PID 재사용은 보통 초 단위 이상 떨어져 일어나므로 1초면 충분히 안전하다.
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 관측 결과와 기록을 대조한다. 판정 규칙(엄한 쪽으로만 기운다):
    ///  - 관측이 없다 → <see cref="TrackedProcessVerdict.Exited"/> (우리 프로세스가 끝난 것)
    ///  - 기록 또는 관측의 시작 시각이 없다 → <see cref="TrackedProcessVerdict.Unverifiable"/> (손대지 않는다)
    ///  - 이름이 다르거나 시작 시각이 허용 오차를 벗어난다 → <see cref="TrackedProcessVerdict.Recycled"/> (남의 것이다)
    ///  - 전부 일치 → <see cref="TrackedProcessVerdict.Alive"/>
    /// </summary>
    public static TrackedProcessVerdict Verify(TrackedProcessIdentity recorded, ProcessObservation? observed)
    {
        if (recorded is null) return TrackedProcessVerdict.Unverifiable;
        if (observed is null) return TrackedProcessVerdict.Exited;
        if (observed.Pid != recorded.Pid) return TrackedProcessVerdict.Recycled;

        // 시작 시각을 한쪽이라도 모르면 "같다"고 단정할 수 없다. 모르면 죽이지 않는다.
        if (recorded.StartedAtUtc is null || observed.StartedAtUtc is null)
            return TrackedProcessVerdict.Unverifiable;

        if (!string.Equals(recorded.ProcessName, observed.ProcessName, StringComparison.OrdinalIgnoreCase))
            return TrackedProcessVerdict.Recycled;

        var drift = recorded.StartedAtUtc.Value - observed.StartedAtUtc.Value;
        if (drift < TimeSpan.Zero) drift = -drift;

        return drift <= StartTimeTolerance
            ? TrackedProcessVerdict.Alive
            : TrackedProcessVerdict.Recycled;
    }

    /// <summary>강제 종료해도 되는가 — <see cref="TrackedProcessVerdict.Alive"/> 하나뿐이다.</summary>
    public static bool MayKill(TrackedProcessVerdict verdict) => verdict == TrackedProcessVerdict.Alive;
}

/// <summary>
/// 고아 프로세스 판정. "고아"의 정의를 코드 한 곳에 못박아 둔다 —
/// <b>우리가 띄웠고, 소유 작업은 이미 사라졌고, 프로세스는 아직 우리 것으로 살아 있다.</b>
/// 세 조건이 모두 성립할 때만 정리 대상이다.
/// </summary>
public static class OrphanRules
{
    /// <summary>
    /// <paramref name="ownerAlive"/> 는 소유 작업(도구/턴)이 레지스트리에 아직 있는지다.
    /// 도구가 끝나면 그 노드는 즉시 제거되므로 "소유자 없음"은 곧 "그 작업은 이미 끝났다"를 뜻한다.
    ///
    /// <c>run_command</c> 가 띄운 프로세스는 도구가 끝날 때 함께 등록 해제되므로 여기 걸리지 않는다.
    /// 실제 대상은 <c>start_process</c> 처럼 도구가 먼저 반환하고 프로세스만 남는 경우다.
    /// </summary>
    public static bool IsOrphan(AgentActivityKind kind, bool ownerAlive, TrackedProcessVerdict verdict)
        => kind == AgentActivityKind.ChildProcess
           && !ownerAlive
           && verdict == TrackedProcessVerdict.Alive;
}

/// <summary>
/// 항목 표시명·부가설명 생성. 병렬로 <c>read_file</c> 8개가 도는 화면에서 이름만 8줄 같으면
/// 사용자는 무엇을 취소하는지 알 수 없다 — 그래서 인자에서 식별 가능한 한 조각을 뽑아 붙인다.
/// </summary>
public static class ActivityLabel
{
    /// <summary>부가설명 길이 상한. 여기서 자르지 않으면 write_file 의 본문 전체가 UI 로 흘러든다.</summary>
    public const int MaxDetailChars = 120;

    /// <summary>
    /// 도구 인자에서 사람이 알아볼 한 조각을 고르는 우선순위. 도구마다 분기하지 않고 이름 목록으로 처리한다 —
    /// 도구가 새로 생겨도 이 목록에 걸리면 자동으로 쓸 만한 설명이 나오고, 안 걸리면 그냥 비어 있을 뿐이다.
    /// </summary>
    private static readonly string[] PreferredKeys =
    [
        "command",      // run_command
        "path",         // read_file / write_file / start_process ...
        "file_path",
        "pattern",      // glob / grep
        "description",  // task(서브에이전트 작업명)
        "url",          // http_fetch
        "name",         // kill_process
        "source",       // move / copy
        "query",
    ];

    /// <summary>
    /// 값이 있는 첫 우선순위 키를 쓴다. 없으면 짧은 문자열 속성 중 첫 번째.
    /// 그래도 없으면 null — <b>인자 전체를 직렬화해 폴백하지 않는다</b>(대용량 인자를 그대로 문자열화하면
    /// 메모리를 쓸데없이 튀게 만든다).
    /// </summary>
    public static string? Summarize(JsonElement args, int maxChars = MaxDetailChars)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in PreferredKeys)
        {
            if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return Truncate(s, maxChars);
            }
        }

        foreach (var p in args.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var s = p.Value.GetString();
            // 긴 값(파일 본문 등)은 식별에 도움이 안 되면서 자르는 비용만 든다 — 건너뛴다.
            if (string.IsNullOrWhiteSpace(s) || s.Length > 200) continue;
            return Truncate(s, maxChars);
        }

        return null;
    }

    /// <summary>줄바꿈을 공백으로 눕히고 상한에서 자른다 — 여러 줄 명령이 UI 한 줄을 무너뜨리지 않게.</summary>
    public static string Truncate(string value, int maxChars = MaxDetailChars)
    {
        var flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (flat.Length <= maxChars) return flat;
        return flat[..maxChars] + "…";
    }
}
