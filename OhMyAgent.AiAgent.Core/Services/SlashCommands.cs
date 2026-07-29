using System;

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
    /// <summary>슬래시로 시작하나 알 수 없는 커맨드.</summary>
    Unknown,
}

/// <summary>파싱된 슬래시 커맨드.</summary>
public sealed record SlashCommand(SlashCommandKind Kind, string Raw);

/// <summary>
/// 입력창 슬래시 커맨드 파서(순수 함수 — WPF 비의존). 모델에 보내지 않고 클라이언트가 직접 처리하는
/// 명령을 식별한다(Claude Code 의 /clear 등에 대응). 실행은 ViewModel 이, 여기서는 식별만.
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
            "/clear" or "/new" => SlashCommandKind.Clear,
            "/help" or "/?"    => SlashCommandKind.Help,
            "/retry"           => SlashCommandKind.Retry,
            _                  => SlashCommandKind.Unknown,
        };

        return new SlashCommand(kind, trimmed);
    }

    /// <summary>/help 로 보여줄 안내문.</summary>
    public static string HelpText =>
        "사용 가능한 커맨드:\n" +
        "  /clear (또는 /new) — 대화를 초기화하고 새 세션을 시작합니다.\n" +
        "  /retry — 마지막으로 보낸 메시지를 입력창에 되불러 편집합니다.\n" +
        "  /help (또는 /?) — 이 도움말을 표시합니다.\n" +
        "그 외 입력은 그대로 에이전트에게 전달됩니다.";

    /// <summary>알 수 없는 커맨드 안내.</summary>
    public static string UnknownText(string raw) =>
        $"알 수 없는 커맨드입니다: {raw}\n/help 로 사용 가능한 커맨드를 확인하세요.";
}
