using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 설치 디렉토리 바이너리 무결성 검사 화면의 ViewModel.
/// <see cref="IBinaryIntegrityService"/>를 호출해 매니페스트 검증/기준 생성을 수행하고
/// 진행률·요약·파일별 결과를 바인딩 친화적으로 노출한다.
///
/// 스레드: 생성자는 UI 스레드에서 호출된다고 가정한다(수동 DI: 트레이/설정 진입점).
/// <see cref="_progress"/>를 UI 스레드에서 만든 <see cref="Progress{T}"/>로 두어
/// 서비스가 백그라운드에서 Report해도 콜백이 UI 스레드로 마샬링된다.
/// </summary>
public sealed partial class IntegrityViewModel : ObservableObject
{
    private readonly IBinaryIntegrityService _integrity;
    private readonly IProgress<IntegrityProgress> _progress;
    private CancellationTokenSource? _cts;

    // ── 대상/옵션 ──────────────────────────────────────────────────────

    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private bool _recursiveOption = true;
    [ObservableProperty] private bool _verifySignaturesOption;
    [ObservableProperty] private string _includeExtensionsText = "exe,dll";

    // ── 진행/상태 ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateBaselineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseTargetCommand))]
    private bool _isScanning;

    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── 오류 배너 (초기화 실패 등) ─────────────────────────────────────
    // AgentSessionViewModel 관례: HasError + ErrorMessage 로 배너 상태를 노출.
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenManifestLocationCommand))]
    private bool _hasManifest;

    // ── 결과 / 요약 ────────────────────────────────────────────────────

    /// <summary>
    /// 마지막 스캔 결과. 요약 카운트/무결성 배지의 파생 소스.
    /// 변경 시 파생 속성 알림을 함께 발생시킨다.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OkCount))]
    [NotifyPropertyChangedFor(nameof(ModifiedCount))]
    [NotifyPropertyChangedFor(nameof(CorruptedCount))]
    [NotifyPropertyChangedFor(nameof(MissingCount))]
    [NotifyPropertyChangedFor(nameof(UnexpectedCount))]
    [NotifyPropertyChangedFor(nameof(IsIntact))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private IntegrityScanResult? _result;

    /// <summary>결과 그리드 바인딩 소스. 스캔 완료 후 정렬되어 채워진다.</summary>
    public ObservableCollection<FileIntegrityItemViewModel> Files { get; } = [];

    // ── 요약 파생 속성 (Result 기반) ───────────────────────────────────

    public int OkCount         => Result?.OkCount         ?? 0;
    public int ModifiedCount   => Result?.ModifiedCount   ?? 0;
    public int CorruptedCount  => Result?.CorruptedCount  ?? 0;
    public int MissingCount    => Result?.MissingCount    ?? 0;
    public int UnexpectedCount => Result?.UnexpectedCount ?? 0;

    /// <summary>요약 배지 색상 결정용. 결과가 없으면 false(중립).</summary>
    public bool IsIntact => Result?.IsIntact ?? false;

    /// <summary>요약/그리드 영역 표시 여부 제어용.</summary>
    public bool HasResult => Result is not null;

    // ── 생성자 ─────────────────────────────────────────────────────────

    public IntegrityViewModel(IBinaryIntegrityService integrity)
    {
        _integrity = integrity;

        // UI 스레드에서 생성 → Report 콜백이 UI 스레드로 마샬링됨.
        _progress = new Progress<IntegrityProgress>(OnProgress);

        TargetDirectory = _integrity.GetDefaultTargetDirectory();
        HasManifest = _integrity.ManifestExists(TargetDirectory);
        StatusMessage = HasManifest
            ? "매니페스트가 있습니다. '검사 시작'을 누르세요."
            : "매니페스트 없음 — '기준 생성'을 먼저 실행하세요.";
    }

    /// <summary>
    /// View의 Loaded에서 await 호출(SettingsViewModel.InitializeAsync 패턴).
    /// 대상 디렉토리 기준 매니페스트 존재 여부를 갱신한다.
    /// 매니페스트 로드/파일 스캔 중 발생하는 예외는 여기서 흡수해
    /// <see cref="HasError"/>/<see cref="ErrorMessage"/> 배너 상태로 변환한다
    /// (async void Loaded 핸들러로 예외가 새어나가 전역 크래시 다이얼로그가 뜨지 않도록).
    /// </summary>
    public Task InitializeAsync()
    {
        try
        {
            HasError = false;
            ErrorMessage = string.Empty;
            RefreshManifestState();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = "무결성 검사 화면을 초기화하지 못했습니다. 대상 디렉토리 접근 권한을 확인한 뒤 다시 시도해 주세요.";
            StatusMessage = "초기화 오류";
            AppLog.Warn("IntegrityViewModel", "InitializeAsync 실패", ex);
        }

        return Task.CompletedTask;
    }

    // ── 진행률 마샬링 ──────────────────────────────────────────────────

    private void OnProgress(IntegrityProgress p)
    {
        ProgressFraction = p.Fraction;
        CurrentFile = p.CurrentFile;
        ProgressText = string.IsNullOrEmpty(p.CurrentFile)
            ? $"{p.ProcessedFiles} / {p.TotalFiles}"
            : $"{p.ProcessedFiles} / {p.TotalFiles} — {p.CurrentFile}";
    }

    // ── 대상 디렉토리 ──────────────────────────────────────────────────

    /// <summary>
    /// View가 폴더 선택 다이얼로그 후 호출. 대상 변경 시 매니페스트 존재 여부를 재평가한다.
    /// (WPF 기본 폴더 다이얼로그가 없어 다이얼로그는 코드비하인드 소유.)
    /// </summary>
    public void SetTargetDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        TargetDirectory = path;
    }

    partial void OnTargetDirectoryChanged(string value) => RefreshManifestState();

    private void RefreshManifestState()
    {
        try
        {
            HasManifest = _integrity.ManifestExists(TargetDirectory);
        }
        catch
        {
            HasManifest = false;
        }
    }

    // ── 옵션 빌드 ──────────────────────────────────────────────────────

    private IntegrityScanOptions BuildOptions() => new()
    {
        TargetDirectory = TargetDirectory,
        IncludeExtensions = ParseExtensions(IncludeExtensionsText),
        Recursive = RecursiveOption,
        VerifySignatures = VerifySignaturesOption,
        ExcludeManifestFile = true,
    };

    /// <summary>"exe, dll" / ".exe;.dll" 등 자유 입력을 소문자·점 포함 확장자 목록으로 정규화.</summary>
    private static IReadOnlyList<string> ParseExtensions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return []; // 빈 목록 = 모든 파일(서비스 계약).

        return text
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── 커맨드 ─────────────────────────────────────────────────────────

    private bool CanScan() => !IsScanning && HasManifest;

    /// <summary>매니페스트와 비교 검증. 진행률/취소 지원.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        await RunScanAsync(isBaseline: false).ConfigureAwait(true);
    }

    private bool CanGenerateBaseline() => !IsScanning;

    /// <summary>현재 디렉토리 상태를 스냅샷해 기준 매니페스트를 생성/저장한다.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateBaseline))]
    private async Task GenerateBaselineAsync()
    {
        await RunScanAsync(isBaseline: true).ConfigureAwait(true);
    }

    private bool CanCancel() => IsScanning;

    /// <summary>진행 중인 스캔/생성을 취소한다.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanBrowseTarget() => !IsScanning;

    /// <summary>
    /// 대상 디렉토리 선택 진입점(게이트 전용). 실제 폴더 다이얼로그는 View 코드비하인드가
    /// 소유하고, 선택 후 <see cref="SetTargetDirectory"/>를 호출한다(MVVM 안전).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBrowseTarget))]
    private void BrowseTarget()
    {
        // No-op: 다이얼로그는 View가 소유. SetTargetDirectory 참조.
    }

    private bool CanOpenManifestLocation() => HasManifest;

    /// <summary>
    /// 매니페스트 폴더를 탐색기로 여는 진입점(게이트 전용). 실제 셸 호출은 View 코드비하인드가
    /// 수행하며, 경로는 <see cref="GetManifestPath"/>로 얻는다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenManifestLocation))]
    private void OpenManifestLocation()
    {
        // No-op: 셸/탐색기 호출은 View가 소유. GetManifestPath 참조.
    }

    /// <summary>View 코드비하인드가 탐색기 열기에 사용할 매니페스트 경로.</summary>
    public string GetManifestPath() => _integrity.GetManifestPath(TargetDirectory);

    // ── 공통 스캔 실행 ─────────────────────────────────────────────────

    private async Task RunScanAsync(bool isBaseline)
    {
        var options = BuildOptions();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsScanning = true;
        HasError = false;
        ErrorMessage = string.Empty;
        Files.Clear();
        Result = null;
        ProgressFraction = 0d;
        ProgressText = string.Empty;
        CurrentFile = null;
        StatusMessage = isBaseline ? "기준 매니페스트 생성 중..." : "무결성 검사 중...";

        try
        {
            var result = isBaseline
                ? await _integrity.GenerateBaselineAsync(options, _progress, token).ConfigureAwait(true)
                : await _integrity.VerifyAsync(options, null, _progress, token).ConfigureAwait(true);

            Result = result;
            PopulateFiles(result.Files);

            if (isBaseline)
            {
                HasManifest = true;
                StatusMessage = $"기준 매니페스트 생성 완료 — 파일 {result.Files.Count}개.";
            }
            else if (result.Files.Count == 0)
            {
                StatusMessage = "검사 대상 파일이 없습니다.";
            }
            else
            {
                StatusMessage = result.IsIntact
                    ? "무결성 양호 — 모든 파일이 일치합니다."
                    : $"문제 발견 — 변조 {result.ModifiedCount}, 손상 {result.CorruptedCount}, " +
                      $"누락 {result.MissingCount}, 추가 {result.UnexpectedCount}.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "검사 취소됨";
        }
        catch (AgentException ex)
        {
            StatusMessage = $"오류: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            ProgressFraction = 0d;
            CurrentFile = null;
            RefreshManifestState();
        }
    }

    /// <summary>문제 항목 우선 정렬 후 그리드 컬렉션을 채운다(UI 스레드에서 호출됨).</summary>
    private void PopulateFiles(IReadOnlyList<FileIntegrityResult> files)
    {
        Files.Clear();
        var items = files
            .Select(f => new FileIntegrityItemViewModel(f))
            .OrderBy(vm => vm.SortPriority)
            .ThenBy(vm => vm.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
            Files.Add(item);
    }
}
