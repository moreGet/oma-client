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
    }
}
