using System;
using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Views.Markdown;

/// <summary>인라인 서식 플래그(중첩 없이 조합 가능).</summary>
[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>한 덩이의 서식 텍스트(문단·헤더·리스트 항목을 이룬다).</summary>
public sealed record MdRun(string Text, MdStyle Style);

/// <summary>블록 요소. 파서가 뱉고 렌더러가 WPF 로 옮긴다.</summary>
public abstract record MdBlock;

/// <summary>일반 문단.</summary>
public sealed record MdParagraph(IReadOnlyList<MdRun> Runs) : MdBlock;

/// <summary>ATX 헤더(#..######). <see cref="Level"/> 는 1~6.</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdRun> Runs) : MdBlock;

/// <summary>펜스 코드 블록. <see cref="Language"/> 는 정보용(없으면 빈 문자열).</summary>
public sealed record MdCode(string Language, string Text) : MdBlock;

/// <summary>불릿/번호 목록. 각 항목은 서식 런의 목록.</summary>
public sealed record MdList(bool Ordered, IReadOnlyList<IReadOnlyList<MdRun>> Items) : MdBlock;
