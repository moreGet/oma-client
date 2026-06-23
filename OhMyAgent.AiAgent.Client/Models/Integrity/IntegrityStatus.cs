namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>파일 단위 무결성 검증 결과 분류.</summary>
public enum IntegrityStatus
{
    /// <summary>기대 해시와 실제 해시가 일치.</summary>
    Ok,
    /// <summary>매니페스트에 있고 파일도 있으나 해시 불일치(내용 변경됨).</summary>
    Modified,
    /// <summary>파일이 존재하나 읽기 실패/I/O 오류 등으로 해시 산출 불가(손상 의심).</summary>
    Corrupted,
    /// <summary>매니페스트에 있으나 디스크에 파일 없음.</summary>
    Missing,
    /// <summary>디스크에 있으나 매니페스트에 없음(예상치 못한 추가 파일).</summary>
    Unexpected
}
