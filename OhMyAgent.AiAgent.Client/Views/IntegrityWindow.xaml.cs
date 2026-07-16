using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.ViewModels;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace OhMyAgent.AiAgent.Client.Views;

/// <summary>
/// 바이너리 무결성 검사 윈도우. SettingsWindow 테마(WindowStyle=None + 공유 StaticResource)를 차용한다.
/// DataContext는 생성자 주입(<see cref="IntegrityViewModel"/>). 코드비하인드는 프레임워크 이벤트와
/// 다이얼로그/셸 호출에만 사용하며, 상태 변경은 모두 ViewModel의 public 메서드에 위임한다.
/// </summary>
public partial class IntegrityWindow : Window
{
    private readonly IntegrityViewModel _vm;

    public IntegrityWindow(IntegrityViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += IntegrityWindow_Loaded;
    }

    private async void IntegrityWindow_Loaded(object sender, RoutedEventArgs e)
        => await _vm.InitializeAsync();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // 폴더 선택 다이얼로그 → ViewModel.SetTargetDirectory 위임 (MVVM 안전 분리).
    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "검사할 디렉토리를 선택하세요",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (!string.IsNullOrWhiteSpace(_vm.TargetDirectory) && Directory.Exists(_vm.TargetDirectory))
            dialog.SelectedPath = _vm.TargetDirectory;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _vm.SetTargetDirectory(dialog.SelectedPath);
    }

    // 기준 매니페스트 생성. 덮어쓰기 확인 MessageBox는 View 책임(ViewModel 명세 §5.1).
    private void GenerateBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.HasManifest)
        {
            var answer = MessageBox.Show(
                this,
                "기존 매니페스트가 이미 존재합니다. 현재 디렉토리 상태로 덮어쓰시겠습니까?",
                "기준 매니페스트 생성",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        if (_vm.GenerateBaselineCommand.CanExecute(null))
            _vm.GenerateBaselineCommand.Execute(null);
    }

    // 매니페스트 폴더를 탐색기에서 열고 파일을 선택. 경로는 ViewModel.GetManifestPath로 획득.
    private void OpenManifestLocation_Click(object sender, RoutedEventArgs e)
    {
        var manifestPath = _vm.GetManifestPath();

        try
        {
            if (File.Exists(manifestPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{manifestPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(manifestPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    {
                        UseShellExecute = true,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("IntegrityWindow", "탐색기 열기 실패", ex);
        }
    }
}
