using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 블랙리스트 RegEx 기반 사전 검증기. PowerShell/CMD 스크립트 실행 전에 호출.
/// </summary>
public static class SecurityValidator
{
    private const int MaxScriptLength = 65536; // 64KB

    private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    /// <summary>
    /// 내장 패턴 매치 타임아웃. `.*` 중첩 수량자에 64KB 입력이 들어오면 백트래킹이 폭주하므로 필수.
    /// 서버 패턴(<see cref="RegexTimeout"/>)과 달리 타임아웃은 차단으로 처리한다(디폴트가 최후 방어선이므로).
    /// </summary>
    private static readonly TimeSpan BuiltinRegexTimeout = TimeSpan.FromMilliseconds(100);

    private static Regex Rx(string pattern) => new(pattern, DefaultOptions, BuiltinRegexTimeout);

    /// <summary>공통(PowerShell + CMD) 차단 패턴.</summary>
    private static readonly (Regex Regex, string Reason)[] CommonBlacklist =
    {
        (Rx(@"\brmdir\s+/s\b"), "재귀 디렉토리 삭제 금지"),
        (Rx(@"\brd\s+/s\b"), "재귀 디렉토리 삭제 금지"),
        (Rx(@"\bformat\s+[a-z]:"), "디스크 포맷 명령 금지"),
        (Rx(@"\bdel\s+/[fqs]"), "강제 파일 삭제 금지"),
        (Rx(@"\berase\s+/[fqs]"), "강제 파일 삭제 금지"),
        (Rx(@"\breg\s+(delete|add)\b.*HKLM\\SYSTEM"), "시스템 레지스트리 변조 금지"),
        (Rx(@"\bshutdown\b"), "시스템 종료 명령 금지"),
    };

    /// <summary>PowerShell 전용 차단 패턴.</summary>
    private static readonly (Regex Regex, string Reason)[] PowerShellBlacklist =
    {
        (Rx(@"\bStop-Computer\b"), "시스템 종료 금지"),
        (Rx(@"\bRestart-Computer\b"), "재부팅 금지"),
        (Rx(@"\bRemove-Item\b.*-Recurse\b.*-Force\b"), "재귀 강제 삭제 금지"),
        (Rx(@"\bRemove-Item\b.*-Force\b.*-Recurse\b"), "재귀 강제 삭제 금지"),
        (Rx(@"\bInvoke-Expression\b"), "동적 코드 실행 금지"),
        (Rx(@"\biex\s"), "Invoke-Expression 별칭 금지"),
        (Rx(@"\bSet-ExecutionPolicy\b"), "실행 정책 변조 금지"),
    };

    /// <summary>CMD 전용 차단 패턴 (현재는 비어 있음, CommonBlacklist만 적용).</summary>
    private static readonly (Regex Regex, string Reason)[] CmdBlacklist =
    {
    };

    /// <summary>차단 디렉토리 (절대 경로 인자).</summary>
    private static readonly (Regex Regex, string Reason)[] BlockedPaths =
    {
        (Rx(@"C:\\Windows\\System32"), "시스템 디렉토리 접근 금지"),
        (Rx(@"C:\\Windows\\SysWOW64"), "시스템 디렉토리 접근 금지"),
        (Rx(@"C:\\Program Files"), "프로그램 디렉토리 접근 금지"),
        (Rx(@"C:\\ProgramData"), "프로그램 데이터 접근 금지"),
        (Rx(@"%SystemRoot%"), "시스템 환경변수 경로 접근 금지"),
        (Rx(@"%WinDir%"), "Windows 경로 접근 금지"),
    };

    /// <summary>내장 패턴 매치. 타임아웃(ReDoS 의심 입력)은 차단으로 간주한다.</summary>
    private static bool IsMatchOrTimeout(Regex regex, string script)
    {
        try { return regex.IsMatch(script); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    // ── 서버 추가 패턴 (2중 안전: 위 디폴트에 "더해진다". 서버는 추가만, 디폴트 제거 불가) ──
    private sealed record ServerRule(bool IsRegex, Regex? Rx, string Substring, string Reason, string ScriptType);

    private static volatile ServerRule[] _serverPatterns = Array.Empty<ServerRule>();
    private static volatile ServerRule[] _serverPaths = Array.Empty<ServerRule>();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 서버 명령 보안 정책을 주입한다(로그인 시). null이면 서버 패턴을 비워 디폴트만 적용한다.
    /// 잘못된 정규식은 skip(디폴트는 그대로 유지되므로 보안 약화 없음). 스레드 안전(스냅샷 통째 교체).
    /// </summary>
    public static void SetServerPatterns(CommandSecurityPolicyResponse? policy)
    {
        _serverPatterns = Compile(policy?.BlockedPatterns);
        _serverPaths    = Compile(policy?.BlockedPaths);
    }

    private static ServerRule[] Compile(IReadOnlyList<CommandPattern>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
            return Array.Empty<ServerRule>();

        var rules = new List<ServerRule>(patterns.Count);
        foreach (var p in patterns)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Pattern)) continue;

            var reason = string.IsNullOrWhiteSpace(p.Reason) ? "서버 정책에 의해 차단" : p.Reason!;
            var stype  = string.IsNullOrWhiteSpace(p.ScriptType) ? "any" : p.ScriptType!.ToLowerInvariant();

            if (string.Equals(p.Type, "regex", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var rx = new Regex(p.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                    rules.Add(new ServerRule(true, rx, string.Empty, reason, stype));
                }
                catch (ArgumentException) { /* 잘못된 정규식 → 무시 */ }
            }
            else
            {
                rules.Add(new ServerRule(false, null, p.Pattern, reason, stype));
            }
        }
        return rules.ToArray();
    }

    private static bool MatchesType(string ruleType, ScriptType scriptType)
        => ruleType == "any"
           || (scriptType == ScriptType.PowerShell ? ruleType == "powershell" : ruleType == "cmd");

    private static ValidationResult? CheckServerRules(ServerRule[] rules, string script, ScriptType type, bool typeAware)
    {
        foreach (var r in rules)
        {
            if (typeAware && !MatchesType(r.ScriptType, type)) continue;

            bool hit;
            if (r.IsRegex)
            {
                try { hit = r.Rx!.IsMatch(script); }
                catch (RegexMatchTimeoutException) { hit = false; }   // 타임아웃 → 미매치(디폴트가 바닥 방어)
            }
            else
            {
                hit = script.Contains(r.Substring, StringComparison.OrdinalIgnoreCase);
            }

            if (hit)
                return ValidationResult.Invalid(r.Reason, r.IsRegex ? r.Rx!.ToString() : r.Substring);
        }
        return null;
    }

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
            if (IsMatchOrTimeout(regex, script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 4. 타입별 패턴 매칭
        var typed = scriptType == ScriptType.PowerShell ? PowerShellBlacklist : CmdBlacklist;
        foreach (var (regex, reason) in typed)
        {
            if (IsMatchOrTimeout(regex, script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 5. 차단 디렉토리 매칭
        foreach (var (regex, reason) in BlockedPaths)
        {
            if (IsMatchOrTimeout(regex, script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 6. 서버 추가 패턴(명령) — script_type 필터링하여 검사.
        var serverCmd = CheckServerRules(_serverPatterns, script, scriptType, typeAware: true);
        if (serverCmd is not null) return serverCmd;

        // 7. 서버 추가 경로 — 모든 셸 공통 적용.
        var serverPath = CheckServerRules(_serverPaths, script, scriptType, typeAware: false);
        if (serverPath is not null) return serverPath;

        return ValidationResult.Valid();
    }
}
