using System;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 슬래시 커맨드 파서. 모델에 보낼지(None) 클라이언트가 처리할지를 가른다 — 오분류하면
/// 명령이 그대로 에이전트에게 새거나(예: "/clear" 를 실행 요청으로 보냄) 일반 입력이 삼켜진다.
/// </summary>
public class SlashCommandsTests
{
    [Theory]
    [InlineData("/clear", SlashCommandKind.Clear)]
    [InlineData("/new", SlashCommandKind.Clear)]
    [InlineData("/help", SlashCommandKind.Help)]
    [InlineData("/?", SlashCommandKind.Help)]
    [InlineData("/retry", SlashCommandKind.Retry)]
    [InlineData("/loop", SlashCommandKind.Loop)]
    [InlineData("/exit", SlashCommandKind.Exit)]
    [InlineData("/quit", SlashCommandKind.Exit)]
    public void Parse_KnownCommands(string input, SlashCommandKind expected)
        => Assert.Equal(expected, SlashCommands.Parse(input).Kind);

    [Fact]
    public void Parse_IsCaseInsensitive()
        => Assert.Equal(SlashCommandKind.Clear, SlashCommands.Parse("/CLEAR").Kind);

    [Fact]
    public void Parse_IgnoresLeadingWhitespace()
        => Assert.Equal(SlashCommandKind.Help, SlashCommands.Parse("   /help").Kind);

    [Fact]
    public void Parse_OnlyFirstTokenIsCommand()
        => Assert.Equal(SlashCommandKind.Clear, SlashCommands.Parse("/clear 나머지는 무시").Kind);

    [Theory]
    [InlineData("/unknown")]
    [InlineData("/xyz")]
    public void Parse_UnknownSlashCommand(string input)
        => Assert.Equal(SlashCommandKind.Unknown, SlashCommands.Parse(input).Kind);

    [Theory]
    [InlineData("일반 메시지")]
    [InlineData("파일을 / 로 구분해서 보여줘")]   // 슬래시가 중간에 있으면 커맨드 아님
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Parse_NotACommand_IsNone(string? input)
        => Assert.Equal(SlashCommandKind.None, SlashCommands.Parse(input).Kind);

    [Fact]
    public void Parse_JustSlash_IsUnknown()
        => Assert.Equal(SlashCommandKind.Unknown, SlashCommands.Parse("/").Kind);

    [Fact]
    public void HelpText_ListsEveryCommand()
    {
        // 안내문이 실제 커맨드를 빠짐없이 나열하는지 — 새 커맨드 추가 시 안내 누락을 막는다.
        Assert.Contains("/clear", SlashCommands.HelpText);
        Assert.Contains("/retry", SlashCommands.HelpText);
        Assert.Contains("/help", SlashCommands.HelpText);
        Assert.Contains("/loop", SlashCommands.HelpText);
    }

    [Fact]
    public void HeadlessHelpExtra_IsSeparate()
    {
        // /exit 은 CLI 전용 의미라 GUI 도움말에 섞이면 안 된다.
        Assert.Contains("/exit", SlashCommands.HeadlessHelpExtra);
        Assert.DoesNotContain("/exit", SlashCommands.HelpText);
    }

    // ── /loop 인자 파싱 ──

    [Theory]
    [InlineData("/loop")]
    [InlineData("/loop status")]
    [InlineData("/loop state")]
    public void Loop_StatusForms(string input)
    {
        var cmd = SlashCommands.Parse(input);
        Assert.Equal(SlashCommandKind.Loop, cmd.Kind);
        Assert.Equal(LoopAction.Status, cmd.Loop!.Action);
    }

    [Theory]
    [InlineData("/loop stop")]
    [InlineData("/loop off")]
    [InlineData("/loop cancel")]
    [InlineData("/loop STOP")]
    public void Loop_StopForms(string input)
        => Assert.Equal(LoopAction.Stop, SlashCommands.Parse(input).Loop!.Action);

    [Theory]
    [InlineData("/loop 5m 빌드 확인", 300, "빌드 확인")]
    [InlineData("/loop 30s x", 30, "x")]
    [InlineData("/loop 1.5h x", 5400, "x")]
    [InlineData("/LOOP 5M x", 300, "x")]
    public void Loop_StartWithInterval(string input, double seconds, string prompt)
    {
        var args = SlashCommands.Parse(input).Loop!;
        Assert.Equal(LoopAction.Start, args.Action);
        Assert.Equal(seconds, args.Interval!.Value.TotalSeconds, 3);
        Assert.Equal(prompt, args.Prompt);
    }

    [Fact]
    public void Loop_IntervalWithoutPrompt_LeavesPromptEmpty()
    {
        // 소비처가 "프롬프트가 필요합니다"를 안내하도록 여기서는 판단하지 않는다.
        var args = SlashCommands.Parse("/loop 5m").Loop!;
        Assert.Equal(LoopAction.Start, args.Action);
        Assert.Equal(TimeSpan.FromMinutes(5), args.Interval);
        Assert.Equal("", args.Prompt);
    }

    [Theory]
    [InlineData("/loop PR 상태 확인해줘", "PR 상태 확인해줘")]
    [InlineData("/loop 5 분마다 확인", "5 분마다 확인")]   // A5 — 단위 없는 5 는 프롬프트의 일부다
    public void Loop_StartWithoutInterval_IsAutonomous(string input, string prompt)
    {
        var args = SlashCommands.Parse(input).Loop!;
        Assert.Equal(LoopAction.Start, args.Action);
        Assert.Null(args.Interval);
        Assert.Equal(prompt, args.Prompt);
    }

    [Fact]
    public void Loop_MultilineInput_IsNotACommand()
    {
        // A7 회귀 방지 — 여러 줄은 커맨드가 아니다(붙여넣기가 통째로 막히는 것을 피한다).
        var cmd = SlashCommands.Parse("/loop 5m x\n두번째줄");
        Assert.Equal(SlashCommandKind.None, cmd.Kind);
        Assert.Null(cmd.Loop);
    }

    [Fact]
    public void NonLoopCommands_CarryNoLoopArgs()
        => Assert.Null(SlashCommands.Parse("/clear").Loop);
}
