namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>작업 디렉토리 샌드박스. 모든 파일 경로 인자는 이곳을 통과한다.</summary>
public interface IWorkspaceContext
{
    /// <summary>절대·정규화된 작업 루트.</summary>
    string Root { get; }

    /// <summary>상대/절대 경로를 루트 기준 절대 경로로 변환. 루트를 벗어나면 throw.</summary>
    string ResolvePath(string relativeOrAbsolute);

    bool IsInsideWorkspace(string path);

    /// <summary>설정 변경 시 호출.</summary>
    void SetRoot(string root);
}
