using System;
using System.Security.Cryptography;
using System.Text;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 인증 토큰의 디스크 저장용 DPAPI 래퍼(현재 사용자 범위).
///
/// 왜 필요한가: settings.json 에 JWT 가 평문으로 저장돼 있었다. 사용자 권한으로 도는 아무 프로세스나
/// (인포스틸러, 동기화 백업, 지원용 ZIP, 심지어 에이전트 자신의 read_file) 그 파일을 읽어 토큰 수명 내내
/// 사용자를 사칭할 수 있었다.
///
/// DPAPI(CurrentUser)를 쓰는 이유: 키를 앱이 들고 있을 필요가 없다(Windows 가 사용자 계정에 묶어 관리).
/// 다른 사용자 계정이나 다른 PC 로 파일을 복사해도 복호화되지 않는다.
///
/// 한계: 같은 사용자로 실행되는 코드는 여전히 복호화할 수 있다. 로컬 관리자/멀웨어에 대한 방어가 아니라
/// "평문 파일이 그대로 유출되는 것"에 대한 방어다. 그 이상이 필요하면 Windows 자격 증명 관리자나
/// OS 수준 격리가 필요하다.
/// </summary>
internal static class TokenProtector
{
    // 암호문에 함께 섞이는 추가 엔트로피. 같은 사용자의 다른 DPAPI 사용처와 암호문을 구분해준다.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OhMyAgent.AiAgent.Client/settings/v1");

    /// <summary>평문 → base64 암호문. 실패하면 null(호출자는 토큰을 저장하지 않는다 — 평문 폴백 금지).</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            // 암호화 실패 시 평문으로 되돌리면 이 클래스의 존재 의의가 사라진다.
            // 토큰을 저장하지 않고(= 다음 실행 때 재로그인) 사실을 남긴다.
            AppLog.Error("TokenProtector", "토큰 암호화 실패 — 토큰을 저장하지 않습니다.", ex);
            return null;
        }
    }

    /// <summary>base64 암호문 → 평문. 실패하면 빈 문자열(재로그인 유도).</summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return string.Empty;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // 다른 사용자/PC 의 settings.json 이거나 손상 → 복호화 불가. 예외 대신 재로그인으로 처리한다.
            AppLog.Warn("TokenProtector", "토큰 복호화 실패 — 재로그인이 필요합니다.", ex);
            return string.Empty;
        }
    }
}
