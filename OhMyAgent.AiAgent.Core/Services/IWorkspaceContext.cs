using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>작업 디렉토리 샌드박스. 모든 파일 경로 인자는 이곳을 통과한다. 다중 루트(v5) 지원.</summary>
public interface IWorkspaceContext
{
    /// <summary>절대·정규화된 주 작업 루트(첫 활성 루트, 비면 Desktop 폴백). 셸 cwd·메타데이터 기준.</summary>
    string Root { get; }

    /// <summary>활성 루트 전체(정규화). 비면 Root 단독.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>상대/절대 경로를 절대 경로로 변환. 어떤 활성 루트에도 속하지 않으면 throw.</summary>
    string ResolvePath(string relativeOrAbsolute);

    /// <summary>활성 루트 중 하나라도 내부면 true.</summary>
    bool IsInsideWorkspace(string path);

    /// <summary>설정 변경 시 호출(단일 주 루트 설정).</summary>
    void SetRoot(string root);

    /// <summary>설정 변경 시 호출(다중 루트 설정). 빈 목록이면 Desktop 폴백.</summary>
    void SetRoots(IReadOnlyList<string> roots);
}
