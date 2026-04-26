using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using OhMyAgent.AiAgent.Client.Models;
using Application = System.Windows.Application;

namespace OhMyAgent.AiAgent.Client.Services;

public class SettingsService : ISettingsService
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OhMyAgent");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    private readonly object _ioLock = new();

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync()
    {
        var migrationNeeded = await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        Current = new AppSettings();
                        return false;
                    }

                    var json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonConvert.DeserializeObject<AppSettings>(json);
                    Current = loaded ?? new AppSettings();

                    // 스키마 마이그레이션
                    if (Current.SchemaVersion < 2)
                    {
                        Current.McpPort       = 3000;
                        Current.McpEnabled    = true;
                        Current.SchemaVersion = 2;
                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    // 파일 손상/파싱 실패 시 기본값으로 폴백
                    Debug.WriteLine($"[SettingsService] LoadAsync failed: {ex.Message}");
                    Current = new AppSettings();
                    return false;
                }
            }
        }).ConfigureAwait(false);

        if (migrationNeeded)
            await SaveAsync().ConfigureAwait(false); // 마이그레이션 결과 저장
    }

    public Task SaveAsync()
    {
        return Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    if (!Directory.Exists(SettingsDirectory))
                        Directory.CreateDirectory(SettingsDirectory);

                    var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
                    File.WriteAllText(SettingsFilePath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SettingsService] SaveAsync failed: {ex.Message}");
                }
            }
        });
    }

    public async Task UpdateHotkeyAsync(HotkeySettings hotkey)
    {
        Current.Hotkey = hotkey;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateOpacityAsync(double opacity)
    {
        Current.Opacity = opacity;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        var handler = SettingsChanged;
        if (handler == null) return;

        var snapshot = Current;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            handler.Invoke(this, snapshot);
        }
        else
        {
            dispatcher.Invoke(() => handler.Invoke(this, snapshot));
        }
    }
}
