using System;
using OhMyAgent.AiAgent.Client.Services.Loop;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>입력창에 친 슬래시 커맨드의 종류.</summary>
public enum SlashCommandKind
{
    /// <summary>슬래시 커맨드가 아님 — 그대로 모델에 보낸다.</summary>
    None,
    /// <summary>대화 초기화(새 세션).</summary>
    Clear,
    /// <summary>사용 가능한 커맨드 안내.</summary>
    Help,
    /// <summary>마지막으로 보낸 메시지를 입력창에 되불러 편집.</summary>
    Retry,
    /// <summary>같은 프롬프트를 반복 실행하는 루프 제어(시작/중지/상태).</summary>
    Loop,
    /// <summary>헤드리스 대화 모드 종료. GUI 에는 대응 동작이 없다(안내만).</summary>
    Exit,
    /// <summary>슬래시로 시작하나 알 수 없는 커맨드.</summary>
    Unknown,
}

/// <summary>/loop 의 하위 동작.</summary>
public enum LoopAction
{
    Start,
    Stop,
    Status,
}

/// <summary>
/// /loop 인자. Action=Start 일 때만 Interval/Prompt 가 의미를 갖는다.
/// Interval=null → 자율 페이싱(모델이 매 턴 다음 시점을 정함).
/// </summary>
public sealed record LoopArgs(LoopAction Action, TimeSpan? Interval, string Prompt);

/// <summary>파싱된 슬래시 커맨드. <paramref name="Loop"/> 는 Kind==Loop 일 때만 non-null.</summary>
public sealed record SlashCommand(SlashCommandKind Kind, string Raw, LoopArgs? Loop = null);

/// <summary>
/// 입력창 슬래시 커맨드 파서(순수 함수 — WPF 비의존). 모델에 보내지 않고 클라이언트가 직접 처리하는
/// 명령을 식별한다(Claude Code 의 /clear 등에 대응). 실행은 ViewModel/헤드리스 호스트가, 여기서는 식별만.
/// </summary>
public static class SlashCommands
{
    public static SlashCommand Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new SlashCommand(SlashCommandKind.None, input ?? string.Empty);

        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('/'))
            return new SlashCommand(SlashCommandKind.None, input);

        // 여러 줄이면 커맨드로 보지 않는다. 입력창이 Shift+Enter 줄바꿈을 받으면서 '/' 로 시작하는
        // 여러 줄 본문(경로 붙여넣기 등)이 들어올 수 있는데, 첫 토큰만 떼는 아래 로직으로는
        // "/clear\n나머지" 가 "알 수 없는 명령"이 되어 전송 자체가 막힌다. 커맨드는 한 줄 전용으로 둔다.
        var body = trimmed.TrimEnd();
        if (body.Contains('\n') || body.Contains('\r'))
            return new SlashCommand(SlashCommandKind.None, input);

        // 첫 토큰만 커맨드로 본다("/clear foo" → "/clear").
        var space = trimmed.IndexOf(' ');
        var word = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();

        var kind = word switch
        {
            "/clear" or "/new"  => SlashCommandKind.Clear,
            "/help" or "/?"     => SlashCommandKind.Help,
            "/retry"            => SlashCommandKind.Retry,
            "/loop"             => SlashCommandKind.Loop,
            "/exit" or "/quit"  => SlashCommandKind.Exit,
            _                   => SlashCommandKind.Unknown,
        };

        if (kind != SlashCommandKind.Loop)
            return new SlashCommand(kind, trimmed);

        var rest = space < 0 ? "" : trimmed[(space + 1)..];
        return new SlashCommand(kind, trimmed, ParseLoopArgs(rest));
    }

    /// <summary>"/loop " 이후 문자열만 받는 순수 함수(파싱 테스트 진입점).</summary>
    public static LoopArgs ParseLoopArgs(string? rest)
    {
        var text = (rest ?? string.Empty).Trim();

        // 인자 없는 /loop 는 상태 조회 — 파괴적이지 않은 기본 동작을 고른다(A6).
        if (text.Length == 0)
            return new LoopArgs(LoopAction.Status, null, "");

        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        var tail = space < 0 ? "" : text[(space + 1)..].Trim();

        switch (first.ToLowerInvariant())
        {
            case "stop" or "off" or "cancel":
                return new LoopArgs(LoopAction.Stop, null, "");
            case "status" or "state":
                return new LoopArgs(LoopAction.Status, null, "");
        }

        // 단위 접미사가 있는 토큰만 간격으로 본다(A5). "5 분마다 확인" 의 "5" 를 간격으로 삼키면
        // 사용자가 쓴 문장이 잘린 채 모델에 전달된다.
        if (LoopIntervalParser.TryParse(first, out var interval))
            return new LoopArgs(LoopAction.Start, interval, tail);

        return new LoopArgs(LoopAction.Start, null, text);
    }

    /// <summary>/help 로 보여줄 안내문(GUI·헤드리스 공용).</summary>
    public static string HelpText =>
        "사용 가능한 커맨드:\n" +
        "  /clear (또는 /new) — 대화를 초기화하고 새 세션을 시작합니다.\n" +
        "  /retry — 마지막으로 보낸 메시지를 입력창에 되불러 편집합니다.\n" +
        "  /loop <간격> <프롬프트> — 지정 간격마다 같은 프롬프트를 반복 실행합니다(예: /loop 5m 빌드 상태 확인).\n" +
        "  /loop <프롬프트> — 간격을 생략하면 매 턴 모델이 다음 실행 시점을 스스로 정합니다.\n" +
        "  /loop stop / status — 반복 실행을 중지하거나 현재 상태를 봅니다.\n" +
        "  /help (또는 /?) — 이 도움말을 표시합니다.\n" +
        "그 외 입력은 그대로 에이전트에게 전달됩니다.";

    /// <summary>헤드리스 전용 추가 안내 — GUI 에는 대응 동작이 없어 공용 HelpText 에 넣지 않는다.</summary>
    public static string HeadlessHelpExtra =>
        "  /exit (또는 /quit) — 헤드리스 대화 모드를 종료합니다.";

    /// <summary>알 수 없는 커맨드 안내.</summary>
    public static string UnknownText(string raw) =>
        $"알 수 없는 커맨드입니다: {raw}\n/help 로 사용 가능한 커맨드를 확인하세요.";
}
