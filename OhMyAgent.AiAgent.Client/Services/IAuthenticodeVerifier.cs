using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>Authenticode 서명 검증(Windows 전용). 미주입(null)이면 서명 검사 비활성.</summary>
public interface IAuthenticodeVerifier
{
    /// <summary>파일의 Authenticode 서명 신뢰 상태를 반환. 예외 없이 SignatureStatus로 흡수.</summary>
    SignatureStatus Verify(string filePath);
}
