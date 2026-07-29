using System;
using System.Threading;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// 루프 엔진이 시간을 만나는 유일한 지점. 이 봉합점이 없으면 LoopController 테스트가 실제로 몇 분을 기다려야 한다
/// (그렇다고 새 테스트용 NuGet 패키지를 들일 만큼 큰 표면도 아니다).
/// </summary>
public interface ILoopClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>실시간 구현.</summary>
public sealed class SystemLoopClock : ILoopClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
