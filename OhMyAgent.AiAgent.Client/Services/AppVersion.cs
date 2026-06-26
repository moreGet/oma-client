using System;
using System.Reflection;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 앱 버전 단일 헬퍼. 한 번만 계산해 캐싱한다.
/// <list type="bullet">
///   <item><see cref="Semantic"/> — 깔끔한 SemVer("1.3.0"). 서버 전송·버전 비교용.</item>
///   <item><see cref="Full"/> — InformationalVersion("1.3.0+abc1234"). UI 표시용.</item>
/// </list>
/// </summary>
public static class AppVersion
{
    /// <summary>Major.Minor.Build 조합(끝의 .0 Revision 제거). 예: "1.3.0".</summary>
    public static string Semantic { get; } = ComputeSemantic();

    /// <summary>AssemblyInformationalVersion(있으면 git 해시 포함), 없으면 Semantic 폴백. 예: "1.3.0+abc1234".</summary>
    public static string Full { get; } = ComputeFull();

    private static string ComputeSemantic()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null
            ? "1.0.0"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static string ComputeFull()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(info) ? Semantic : info;
    }
}
