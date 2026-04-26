using System.Text.RegularExpressions;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 블랙리스트 RegEx 기반 사전 검증기. PowerShell/CMD 스크립트 실행 전에 호출.
/// </summary>
public static class SecurityValidator
{
    private const int MaxScriptLength = 65536; // 64KB

    private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    /// <summary>공통(PowerShell + CMD) 차단 패턴.</summary>
    private static readonly (Regex Regex, string Reason)[] CommonBlacklist =
    {
        (new Regex(@"\brmdir\s+/s\b", DefaultOptions), "재귀 디렉토리 삭제 금지"),
        (new Regex(@"\brd\s+/s\b", DefaultOptions), "재귀 디렉토리 삭제 금지"),
        (new Regex(@"\bformat\s+[a-z]:", DefaultOptions), "디스크 포맷 명령 금지"),
        (new Regex(@"\bdel\s+/[fqs]", DefaultOptions), "강제 파일 삭제 금지"),
        (new Regex(@"\berase\s+/[fqs]", DefaultOptions), "강제 파일 삭제 금지"),
        (new Regex(@"\breg\s+(delete|add)\b.*HKLM\\\\SYSTEM", DefaultOptions), "시스템 레지스트리 변조 금지"),
        (new Regex(@"\bshutdown\b", DefaultOptions), "시스템 종료 명령 금지"),
    };

    /// <summary>PowerShell 전용 차단 패턴.</summary>
    private static readonly (Regex Regex, string Reason)[] PowerShellBlacklist =
    {
        (new Regex(@"\bStop-Computer\b", DefaultOptions), "시스템 종료 금지"),
        (new Regex(@"\bRestart-Computer\b", DefaultOptions), "재부팅 금지"),
        (new Regex(@"\bRemove-Item\b.*-Recurse\b.*-Force\b", DefaultOptions), "재귀 강제 삭제 금지"),
        (new Regex(@"\bRemove-Item\b.*-Force\b.*-Recurse\b", DefaultOptions), "재귀 강제 삭제 금지"),
        (new Regex(@"\bInvoke-Expression\b", DefaultOptions), "동적 코드 실행 금지"),
        (new Regex(@"\biex\s", DefaultOptions), "Invoke-Expression 별칭 금지"),
        (new Regex(@"\bSet-ExecutionPolicy\b", DefaultOptions), "실행 정책 변조 금지"),
    };

    /// <summary>CMD 전용 차단 패턴 (현재는 비어 있음, CommonBlacklist만 적용).</summary>
    private static readonly (Regex Regex, string Reason)[] CmdBlacklist =
    {
    };

    /// <summary>차단 디렉토리 (절대 경로 인자).</summary>
    private static readonly (Regex Regex, string Reason)[] BlockedPaths =
    {
        (new Regex(@"C:\\Windows\\System32", DefaultOptions), "시스템 디렉토리 접근 금지"),
        (new Regex(@"C:\\Windows\\SysWOW64", DefaultOptions), "시스템 디렉토리 접근 금지"),
        (new Regex(@"C:\\Program Files", DefaultOptions), "프로그램 디렉토리 접근 금지"),
        (new Regex(@"C:\\ProgramData", DefaultOptions), "프로그램 데이터 접근 금지"),
        (new Regex(@"%SystemRoot%", DefaultOptions), "시스템 환경변수 경로 접근 금지"),
        (new Regex(@"%WinDir%", DefaultOptions), "Windows 경로 접근 금지"),
    };

    public static ValidationResult Validate(string script, ScriptType scriptType)
    {
        // 1. null/empty 체크
        if (string.IsNullOrWhiteSpace(script))
            return ValidationResult.Invalid("빈 스크립트");

        // 2. 길이 제한
        if (script.Length > MaxScriptLength)
            return ValidationResult.Invalid("스크립트 크기 초과 (최대 64KB)");

        // 3. 공통 패턴 매칭
        foreach (var (regex, reason) in CommonBlacklist)
        {
            if (regex.IsMatch(script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 4. 타입별 패턴 매칭
        var typed = scriptType == ScriptType.PowerShell ? PowerShellBlacklist : CmdBlacklist;
        foreach (var (regex, reason) in typed)
        {
            if (regex.IsMatch(script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 5. 차단 디렉토리 매칭
        foreach (var (regex, reason) in BlockedPaths)
        {
            if (regex.IsMatch(script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        return ValidationResult.Valid();
    }
}
