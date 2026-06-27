using CommunityToolkit.Mvvm.ComponentModel;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 결과 그리드 1행의 표시 전용 ViewModel. <see cref="FileIntegrityResult"/>를 래핑해
/// 한글 상태 텍스트·짧은 해시·상태색 리소스키 등 바인딩 친화적 표현 속성을 노출한다.
/// 컨버터를 최소화하기 위해 표현 로직을 여기서 미리 계산한다(불변, 읽기 전용).
/// </summary>
public sealed partial class FileIntegrityItemViewModel : ObservableObject
{
    /// <summary>원본 검증 결과 모델(필요 시 정렬/필터에서 참조).</summary>
    public FileIntegrityResult Model { get; }

    public FileIntegrityItemViewModel(FileIntegrityResult model)
    {
        Model = model;
    }

    /// <summary>매니페스트 루트 기준 상대경로('/' 구분).</summary>
    public string RelativePath => Model.RelativePath;

    /// <summary>분류 상태(정렬/필터/행 트리거용 원본 enum).</summary>
    public IntegrityStatus Status => Model.Status;

    /// <summary>한글화된 상태 라벨: 정상/변조/손상/누락/추가.</summary>
    public string StatusText => Status switch
    {
        IntegrityStatus.Ok         => "정상",
        IntegrityStatus.Modified   => "변조",
        IntegrityStatus.Corrupted  => "손상",
        IntegrityStatus.Missing    => "누락",
        IntegrityStatus.Unexpected => "추가",
        _ => Status.ToString(),
    };


    /// <summary>매니페스트 기대 해시 앞 12자(없으면 빈 문자열).</summary>
    public string ExpectedSha256Short => Shorten(Model.ExpectedSha256);

    /// <summary>디스크 실제 해시 앞 12자(없으면 빈 문자열).</summary>
    public string ActualSha256Short => Shorten(Model.ActualSha256);

    /// <summary>디스크 실제 크기(없으면 빈 문자열). 바이트를 사람이 읽기 쉬운 단위로.</summary>
    public string SizeText => Model.ActualSize is { } size ? FormatSize(size) : string.Empty;

    /// <summary>서명 상태 한글 라벨(부가 정보).</summary>
    public string SignatureText => Model.Signature switch
    {
        SignatureStatus.NotChecked => "검사 안 함",
        SignatureStatus.Valid      => "유효",
        SignatureStatus.Invalid    => "무효",
        SignatureStatus.Unsigned   => "서명 없음",
        _ => Model.Signature.ToString(),
    };

    /// <summary>오류/부가 설명(예: 파일 잠김, 접근 거부). 없으면 빈 문자열.</summary>
    public string Detail => Model.Detail ?? string.Empty;

    /// <summary>
    /// 정렬 우선순위. 문제 항목(변조/손상/누락/추가)을 먼저, 정상을 뒤로.
    /// VM이 컬렉션에 추가하기 전 정렬할 때 사용.
    /// </summary>
    public int SortPriority => Status switch
    {
        IntegrityStatus.Modified   => 0,
        IntegrityStatus.Corrupted  => 1,
        IntegrityStatus.Missing    => 2,
        IntegrityStatus.Unexpected => 3,
        IntegrityStatus.Ok         => 4,
        _ => 5,
    };

    private static string Shorten(string? hash)
        => string.IsNullOrEmpty(hash) ? string.Empty
           : hash.Length <= 12 ? hash
           : hash[..12];

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unit]}";
    }
}
