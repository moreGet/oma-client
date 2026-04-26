---
name: wpf-ui
description: >
  WPF XAML View 구현 스킬. Window/UserControl/Page XAML 작성, 데이터 바인딩,
  Style/ControlTemplate/DataTemplate, 리소스 딕셔너리, 애니메이션.
  wpf-orchestrator 내 UIDesigner 에이전트가 사용.
---

# WPF XAML UI 구현 가이드

## View 기본 구조

```xml
<Window x:Class="OhMyAgent.AiAgent.Client.Views.ChatWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:vm="clr-namespace:OhMyAgent.AiAgent.Client.ViewModels"
        mc:Ignorable="d"
        d:DataContext="{d:DesignInstance Type=vm:ChatViewModel, IsDesignTimeCreatable=False}"
        Title="OhMyAgent Chat" Height="600" Width="900">
```

## DataContext 설정

코드비하인드에서 DI로 주입:
```csharp
public partial class ChatWindow : Window
{
    public ChatWindow(ChatViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
```

## 바인딩 패턴

### 기본 바인딩
```xml
<TextBox Text="{Binding InputText, UpdateSourceTrigger=PropertyChanged}" />
<Button Command="{Binding SendCommand}" Content="전송"
        IsEnabled="{Binding IsBusy, Converter={StaticResource BoolToInverseConverter}}" />
<ProgressBar Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}" />
```

### 컬렉션 바인딩
```xml
<ListView ItemsSource="{Binding Messages}"
          ScrollViewer.VerticalScrollBarVisibility="Auto">
    <ListView.ItemTemplate>
        <DataTemplate>
            <Border Padding="8" Margin="4">
                <TextBlock Text="{Binding Content}" TextWrapping="Wrap" />
            </Border>
        </DataTemplate>
    </ListView.ItemTemplate>
</ListView>
```

## 리소스 딕셔너리 구조

**App.xaml에 병합:**
```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Resources/Colors.xaml" />
            <ResourceDictionary Source="Resources/Styles.xaml" />
            <ResourceDictionary Source="Resources/Converters.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

**Converters.xaml 예시:**
```xml
<converters:BoolToVisibilityConverter x:Key="BoolToVisibilityConverter" />
<converters:BoolToInverseConverter x:Key="BoolToInverseConverter" />
```

## 자주 쓰는 컨버터 구현

```csharp
[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility.Visible;
}
```

## 레이아웃 원칙

- 고정 Width/Height 최소화 — `*`, `Auto` 활용
- `DockPanel.LastChildFill` 적극 활용
- `Grid.IsSharedSizeScope` 으로 열 너비 맞추기
- 스크롤 필요한 목록: `ScrollViewer` 명시적 포함

## 코드비하인드 허용 범위

코드비하인드에는 WPF 프레임워크 이벤트만:
```csharp
// OK: 스크롤 자동 이동
private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    => MessagesListView.ScrollIntoView(e.NewItems?[^1]);

// OK: 키보드 단축키 (Enter 전송)
private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Enter && !e.IsRepeat)
    {
        ((ChatViewModel)DataContext).SendCommand.Execute(null);
        e.Handled = true;
    }
}
```

## 산출물 요약 형식

`_workspace/04_ui_summary.md`:
```markdown
## 생성된 View 파일
- Views/ChatWindow.xaml — 채팅 메인 화면
- Views/ChatWindow.xaml.cs — 스크롤 자동이동 코드비하인드
- Resources/Converters.xaml — BoolToVisibility 등
- Resources/Styles.xaml — 버튼·텍스트박스 공통 스타일

## 주요 바인딩 경로
| XAML | Binding Path | ViewModel 프로퍼티 |
|------|-------------|-----------------|
| TextBox | InputText | ChatViewModel.InputText |
| Button | SendCommand | ChatViewModel.SendCommand |
```
