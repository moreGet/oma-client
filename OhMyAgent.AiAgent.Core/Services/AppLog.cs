using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace OhMyAgent.AiAgent.Client.Services;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 파일 로거. %APPDATA%\OhMyAgent\logs\app-yyyyMMdd.log 에 하루 한 파일로 기록한다.
///
/// 왜 Debug.WriteLine 이 아닌가: Debug.WriteLine 은 [Conditional("DEBUG")] 이라 Release 빌드에서
/// 호출 자체가 사라진다. 즉 배포된 앱은 로그가 전혀 없어 현장 크래시를 진단할 수 없었다.
///
/// 왜 정적 클래스인가: 이 앱은 DI 컨테이너 없이 App.StartupAsync 에서 수동 배선하는데,
/// 로깅은 배선 이전(설정 로드)부터 배선 이후(도구 실행) 까지 어디서나 필요하고,
/// 모든 catch 블록에 로거를 주입하려면 거의 모든 생성자를 바꿔야 한다.
///
/// 스레드 안전. 로깅 실패는 절대 예외를 던지지 않는다 — 로거 때문에 앱이 죽으면 본말전도다.
/// </summary>
public static class AppLog
{
    private const int RetentionDays = 7;
    private const long MaxFileBytes = 10 * 1024 * 1024; // 10MB — 폭주 시 디스크를 채우지 않도록.

    private static readonly object Gate = new();
    private static string? _dir;
    private static bool _enabled;
    private static DateTime _currentDate;
    private static string? _currentPath;

    /// <summary>현재 로그 파일 경로(미초기화/비활성이면 null). "로그 폴더 열기" 같은 UI에 쓸 수 있다.</summary>
    public static string? CurrentFilePath { get { lock (Gate) return _currentPath; } }

    /// <summary>로그 디렉토리 경로(미초기화면 null).</summary>
    public static string? LogDirectory { get { lock (Gate) return _dir; } }

    /// <summary>앱 시작 시 1회 호출. 실패해도 앱은 계속 진행하며, 이후 로깅은 조용히 no-op 이 된다.</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _dir = Path.Combine(appData, "OhMyAgent", "logs");
                Directory.CreateDirectory(_dir);
                _enabled = true;
                OpenForToday_NoLock();
                PurgeOldFiles_NoLock();
            }
            catch
            {
                _enabled = false;   // 로그를 못 쓰는 환경 → 조용히 포기.
            }
        }
    }

    public static void Debug(string category, string message, Exception? ex = null) => Write(LogLevel.Debug, category, message, ex);
    public static void Info(string category, string message, Exception? ex = null)  => Write(LogLevel.Info,  category, message, ex);
    public static void Warn(string category, string message, Exception? ex = null)  => Write(LogLevel.Warn,  category, message, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (!_enabled) return;

            try
            {
                RollIfNeeded_NoLock();
                if (_currentPath is null) return;

                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append(" [").Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(']')
                  .Append(" [T").Append(Environment.CurrentManagedThreadId).Append(']')
                  .Append(" [").Append(category).Append("] ")
                  .Append(message);

                // 예외는 타입/메시지/스택 + InnerException 사슬까지 남긴다 — 이게 없으면 남길 이유가 없다.
                for (var e = ex; e is not null; e = e.InnerException)
                {
                    sb.AppendLine()
                      .Append("    ").Append(e.GetType().FullName).Append(": ").Append(e.Message);
                    if (!string.IsNullOrEmpty(e.StackTrace))
                        sb.AppendLine().Append(e.StackTrace);
                }

                sb.AppendLine();
                File.AppendAllText(_currentPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 디스크 가득/권한 없음/파일 잠김 → 로깅은 포기하되 앱 흐름은 건드리지 않는다.
            }
        }
    }

    private static void RollIfNeeded_NoLock()
    {
        // 날짜가 바뀌었거나(장시간 실행), 파일이 상한을 넘겼으면 새 파일로.
        if (_currentPath is null || _currentDate != DateTime.Today)
        {
            OpenForToday_NoLock();
            PurgeOldFiles_NoLock();
            return;
        }

        try
        {
            var info = new FileInfo(_currentPath);
            if (info.Exists && info.Length >= MaxFileBytes)
            {
                var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                _currentPath = Path.Combine(_dir!, $"app-{_currentDate:yyyyMMdd}-{stamp}.log");
            }
        }
        catch { /* 크기 확인 실패 → 그대로 쓴다. */ }
    }

    private static void OpenForToday_NoLock()
    {
        if (_dir is null) return;
        _currentDate = DateTime.Today;
        _currentPath = Path.Combine(_dir, $"app-{_currentDate:yyyyMMdd}.log");
    }

    private static void PurgeOldFiles_NoLock()
    {
        if (_dir is null) return;

        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* 잠긴 파일 등 → 건너뛴다. */ }
            }
        }
        catch { /* 열거 실패 → 정리 생략. */ }
    }
}
