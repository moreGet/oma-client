namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>(선택) Authenticode 서명 검증 상태. 해시 검증과 독립적인 부가 정보.</summary>
public enum SignatureStatus
{
    /// <summary>서명 검사를 하지 않음(옵션 꺼짐 또는 비대상 확장자).</summary>
    NotChecked,
    /// <summary>유효하게 서명되고 신뢰 체인 검증 통과.</summary>
    Valid,
    /// <summary>서명이 있으나 무효(체인 실패/만료/변조).</summary>
    Invalid,
    /// <summary>서명 없음(unsigned).</summary>
    Unsigned
}
