using System;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 입력창의 <c>@파일경로</c> 멘션 파싱(순수 함수 — WPF 비의존).
///
/// caret 직전의 <c>@토큰</c>을 찾아 자동완성 후보 필터에 쓴다. 채팅 멤버 멘션과 달리 경로 문자(/ . -)는
/// 토큰의 일부이므로 공백에서만 끊는다. 이메일 등 오탐을 막기 위해 <c>@</c> 바로 앞은 공백이거나 문두여야 한다.
///
/// 파싱만 분리해 단위 테스트로 고정한다(필터링은 검증된 <see cref="Tools.FuzzyMatch"/> 재사용).
/// </summary>
public static class FileMentions
{
    /// <summary>추출된 멘션 토큰. <see cref="Start"/> 는 '@' 의 인덱스, <see cref="Text"/> 는 '@' 뒤 부분(공백 없음).</summary>
    public sealed record Token(int Start, string Text);

    public static Token? ExtractToken(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var c = Math.Clamp(caret, 0, text.Length);

        // caret 에서 뒤로 가며 공백/‘@’ 를 만날 때까지 — 경로 문자는 통과.
        var start = c - 1;
        while (start >= 0 && !char.IsWhiteSpace(text[start]) && text[start] != '@')
            start--;

        if (start < 0 || text[start] != '@') return null;

        // '@' 앞이 공백이 아니면(예: 이메일 user@host) 멘션으로 보지 않는다.
        if (start > 0 && !char.IsWhiteSpace(text[start - 1])) return null;

        return new Token(start, text[(start + 1)..c]);
    }
}
