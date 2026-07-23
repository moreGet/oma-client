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
        // 플래그는 인자 어디에나 올 수 있다 — `del *.* /q` 처럼 대상 뒤에 붙는 형태도 잡아야 한다.
        (Rx(@"\bdel\b(?=[^\r\n]*\s/[fqs]\b)"), "강제 파일 삭제 금지"),
        (Rx(@"\berase\b(?=[^\r\n]*\s/[fqs]\b)"), "강제 파일 삭제 금지"),
        (Rx(@"\breg\s+(delete|add)\b.*HKLM[\\/]SYSTEM"), "시스템 레지스트리 변조 금지"),
        (Rx(@"\bshutdown\b"), "시스템 종료 명령 금지"),

        // 셸 갈아타기로 다른 셸의 블랙리스트를 통째로 우회하는 것을 막는다.
        // 예: shell="cmd" 로 `powershell -enc <base64>` 를 던지면 PowerShell 패턴이 전혀 적용되지 않았다.
        (Rx(@"\b(powershell|pwsh)(\.exe)?\b[^\r\n]*\s-(e|en|enc|enco|encod|encode|encoded|encodedcommand)\b"),
            "인코딩된 PowerShell 명령 실행 금지(우회 방지)"),
        (Rx(@"\b(powershell|pwsh)(\.exe)?\b[^\r\n]*\s-(c|com|comm|comma|comman|command)\b"),
            "중첩 셸로 명령 실행 금지(우회 방지)"),
        (Rx(@"\bcmd(\.exe)?\b[^\r\n]*\s/(c|k)\b"), "중첩 셸로 명령 실행 금지(우회 방지)"),
    };

    /// <summary>PowerShell 전용 차단 패턴.</summary>
    private static readonly (Regex Regex, string Reason)[] PowerShellBlacklist =
    {
        (Rx(@"\bStop-Computer\b"), "시스템 종료 금지"),
        (Rx(@"\bRestart-Computer\b"), "재부팅 금지"),
        // Remove-Item 은 별칭이 많다(rm/ri/del/erase/rd/rmdir) — 이름만 막으면 `rm -r -force` 로 그대로 통과했다.
        // 플래그 순서도 자유라 순열을 나열하는 대신 선행 탐색으로 둘 다 있는지 본다.
        (Rx(@"\b(Remove-Item|rm|ri|rd|rmdir)\b(?=[^\r\n]*\s-r(ecurse)?\b)(?=[^\r\n]*\s-f(orce)?\b)"),
            "재귀 강제 삭제 금지"),
        (Rx(@"\bInvoke-Expression\b"), "동적 코드 실행 금지"),
        (Rx(@"\biex\s"), "Invoke-Expression 별칭 금지"),
        (Rx(@"\bSet-ExecutionPolicy\b"), "실행 정책 변조 금지"),
        (Rx(@"\bFromBase64String\b"), "인코딩된 페이로드 실행 금지(우회 방지)"),
    };

    /// <summary>CMD 전용 차단 패턴 (현재는 비어 있음, CommonBlacklist만 적용).</summary>
    private static readonly (Regex Regex, string Reason)[] CmdBlacklist =
    {
    };

    /// <summary>bash 전용 차단 패턴 (최소 방어선 — 파괴적·자기실행·권한상승).</summary>
    private static readonly (Regex Regex, string Reason)[] BashBlacklist =
    {
        // rm 재귀+강제 (플래그 순서·결합 자유: -rf, -fr, -r -f). 선행탐색 2개로 둘 다 확인.
        (Rx(@"\brm\b(?=[^\r\n]*\s-[a-z]*r)(?=[^\r\n]*\s-[a-z]*f)"), "재귀 강제 삭제 금지"),
        // 루트/홈/와일드카드 대량 삭제 (rm -rf / , rm -rf ~, rm -rf /*)
        (Rx(@"\brm\b[^\r\n]*\s-[a-z]*[rf][a-z]*\s+(/|~|/\*|\.\s*$)"), "위험 경로 대량 삭제 금지"),
        // 디스크 파괴: dd of=/dev/sdX , mkfs, 블록장치 리다이렉트
        (Rx(@"\bdd\b[^\r\n]*\bof=\s*/dev/"), "블록 디바이스 직접 쓰기 금지"),
        (Rx(@"\bmkfs(\.[a-z0-9]+)?\b"), "파일시스템 포맷 금지"),
        (Rx(@">\s*/dev/(sd|nvme|hd)[a-z0-9]*"), "블록 디바이스 리다이렉트 금지"),
        // 파이프-투-셸 원격 실행 (curl|sh, wget|bash, 중간 sudo 포함)
        (Rx(@"\b(curl|wget)\b[^\r\n|]*\|\s*(sudo\s+)?(sh|bash|zsh)\b"), "원격 스크립트 직접 실행 금지(우회 방지)"),
        // 포크폭탄
        (Rx(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:"), "포크 폭탄 금지"),
        // 시스템 종료/재부팅
        (Rx(@"\b(shutdown|reboot|halt|poweroff)\b"), "시스템 종료 명령 금지"),
        (Rx(@"\binit\s+0\b"), "시스템 종료 명령 금지"),
        // 루트 소유권/권한 광역 변경
        (Rx(@"\bchmod\b[^\r\n]*\s-R[^\r\n]*\s(/|/etc|/usr|/bin)\b"), "시스템 경로 권한 변경 금지"),
        // 권한 상승 (옵트인 자율모드에서 sudo 봉쇄 — 헤드리스는 무인이므로 상승 금지가 안전)
        (Rx(@"\bsudo\b"), "권한 상승(sudo) 금지"),
    };

    /// <summary>Linux 시스템 경로 차단(쓰기·삭제 대상으로 자주 등장).</summary>
    private static readonly (Regex Regex, string Reason)[] LinuxBlockedPaths =
    {
        (Rx(@"(^|\s)/etc(/|\s|$)"),  "시스템 설정 디렉토리 접근 금지"),
        (Rx(@"(^|\s)/boot(/|\s|$)"), "부트 디렉토리 접근 금지"),
        (Rx(@"(^|\s)/sys(/|\s|$)"),  "커널 sysfs 접근 금지"),
        (Rx(@"(^|\s)/proc(/|\s|$)"), "procfs 접근 금지"),
        (Rx(@"(^|\s)/dev/(sd|nvme|hd|mem)"), "블록/메모리 디바이스 접근 금지"),
    };

    /// <summary>
    /// 차단 디렉토리 (절대 경로 인자).
    /// Windows 는 <c>/</c> 도 경로 구분자로 받아들이므로 <c>[\\/]</c> 로 둘 다 잡는다 —
    /// 역슬래시만 보면 <c>C:/Windows/System32</c> 가 그대로 통과했다.
    /// PowerShell 의 <c>$env:SystemRoot</c> 형태도 <c>%SystemRoot%</c> 와 함께 막는다.
    /// </summary>
    private static readonly (Regex Regex, string Reason)[] BlockedPaths =
    {
        (Rx(@"C:[\\/]Windows[\\/]System32"), "시스템 디렉토리 접근 금지"),
        (Rx(@"C:[\\/]Windows[\\/]SysWOW64"), "시스템 디렉토리 접근 금지"),
        (Rx(@"C:[\\/]Program Files"), "프로그램 디렉토리 접근 금지"),
        (Rx(@"C:[\\/]ProgramData"), "프로그램 데이터 접근 금지"),
        (Rx(@"(%SystemRoot%|\$env:SystemRoot)"), "시스템 환경변수 경로 접근 금지"),
        (Rx(@"(%WinDir%|\$env:WinDir)"), "Windows 경로 접근 금지"),
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
           || ruleType == scriptType switch
              {
                  ScriptType.PowerShell => "powershell",
                  ScriptType.Cmd        => "cmd",
                  ScriptType.Bash       => "bash",
                  _                     => "cmd",
              };

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
        var typed = scriptType switch
        {
            ScriptType.PowerShell => PowerShellBlacklist,
            ScriptType.Cmd        => CmdBlacklist,
            ScriptType.Bash       => BashBlacklist,
            _                     => CmdBlacklist,
        };
        foreach (var (regex, reason) in typed)
        {
            if (IsMatchOrTimeout(regex, script))
                return ValidationResult.Invalid(reason, regex.ToString());
        }

        // 5. 차단 디렉토리 매칭 — Bash 는 Linux 경로, 그 외는 기존 Windows 경로.
        var blockedPaths = scriptType == ScriptType.Bash ? LinuxBlockedPaths : BlockedPaths;
        foreach (var (regex, reason) in blockedPaths)
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
