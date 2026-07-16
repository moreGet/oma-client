using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// http_fetch 의 SSRF 방어. 종전에는 Uri.TryCreate 로 문법만 확인해, 모델이
/// http://169.254.169.254/latest/meta-data/ (클라우드 자격증명)나 http://127.0.0.1:&lt;port&gt;/ (로컬 관리 API)에
/// 그대로 접근할 수 있었고, 공개 URL이 302로 그쪽으로 유도할 수도 있었다.
///
/// 사설 대역(10/8, 192.168/16, 172.16/12)은 <b>막지 않는다</b>: 이 도구의 목적 자체가 사내망 접근이라
/// (Description 참조) 사설 대역을 막으면 도구가 무의미해진다. 실제 위험은 아래 둘이다.
///   - 루프백: 사용자 PC에서만 열려 있는 관리/디버그 API
///   - 링크로컬(169.254/16, fe80::): 클라우드 메타데이터 서비스
/// 그 외 특수 대역(멀티캐스트 등)도 의도된 대상이 아니므로 함께 막는다.
///
/// 한계: 이름 해석 후 실제 연결까지의 사이에 DNS가 바뀌면(DNS rebinding) 우회 가능하다.
/// 완전한 차단은 해석된 IP로 직접 연결하고 Host 헤더를 붙이는 방식이 필요하며, 여기서는 다루지 않는다.
/// </summary>
internal static class UrlGuard
{
    /// <summary>검증 결과. <see cref="Reason"/> 는 거부 사유(허용이면 null).</summary>
    internal readonly record struct Result(bool Allowed, string? Reason)
    {
        public static Result Allow() => new(true, null);
        public static Result Deny(string reason) => new(false, reason);
    }

    public static async Task<Result> CheckAsync(Uri uri, CancellationToken ct)
    {
        // 스킴 화이트리스트 — file://, ftp://, gopher:// 등을 배제한다.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Result.Deny($"http/https 만 허용됩니다: {uri.Scheme}");

        IPAddress[] addresses;
        try
        {
            // 리터럴 IP면 해석 없이 그대로, 호스트명이면 DNS 조회.
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Deny($"호스트를 확인할 수 없습니다: {uri.Host} ({ex.Message})");
        }

        if (addresses.Length == 0)
            return Result.Deny($"호스트를 확인할 수 없습니다: {uri.Host}");

        // 하나라도 금지 대역이면 거부한다(라운드로빈으로 일부만 루프백인 경우 방지).
        var blocked = addresses.FirstOrDefault(IsBlocked);
        if (blocked is not null)
            return Result.Deny($"차단된 대상입니다({blocked}): 루프백·링크로컬 등 내부 주소에는 접근할 수 없습니다");

        return Result.Allow();
    }

    private static bool IsBlocked(IPAddress ip)
    {
        // IPv4 매핑된 IPv6(::ffff:127.0.0.1)로 우회하는 것을 막기 위해 먼저 펼친다.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))                 return true;   // 127.0.0.0/8, ::1
        if (ip.Equals(IPAddress.Any))                 return true;   // 0.0.0.0 → 로컬로 해석됨
        if (ip.Equals(IPAddress.IPv6Any))             return true;   // ::

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();

            if (b[0] == 169 && b[1] == 254) return true;   // 169.254/16 링크로컬(클라우드 메타데이터)
            if (b[0] == 0)                  return true;   // 0/8 예약
            if (b[0] >= 224)                return true;   // 멀티캐스트(224/4) + 예약(240/4)

            return false;   // 사설 대역(10/8, 172.16/12, 192.168/16)은 의도된 사용처 — 허용.
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal)  return true;   // fe80::/10
            if (ip.IsIPv6Multicast)  return true;
            return false;                           // ULA(fc00::/7)는 사설 대역에 해당 — 허용.
        }

        return true;   // 알 수 없는 주소 체계 → 안전 우선 차단.
    }
}
