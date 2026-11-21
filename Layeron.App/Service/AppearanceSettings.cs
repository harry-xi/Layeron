using System;
using System.IO;
using Avalonia;
using Avalonia.Styling;
using Layeron.App.Logging;

namespace Layeron.App.Service;

/// <summary>
/// 明暗主题偏好。
/// <para>
/// 只保存一个值，所以用一个文本文件而不是 JSON：启动时在 <see cref="App.Initialize"/> 读取并应用，
/// 设置页切换时立即生效并写回。默认深色，与 App.axaml 的初始值一致。
/// </para>
/// </summary>
public static class AppearanceSettings
{
    private static readonly string FilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Layeron", "appearance.theme");

    /// <summary>当前偏好；<c>true</c> 为深色。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>偏好对应的 Avalonia 主题变体。</summary>
    public static ThemeVariant CurrentTheme => IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>读取上次保存的偏好；文件不存在或读取失败时保持深色。</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                IsDark = !string.Equals(File.ReadAllText(FilePath).Trim(), "Light", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Appearance", "读取主题偏好失败，继续使用深色: {Message}", ex.Message);
        }
    }

    /// <summary>切换并立即应用，同时写回磁盘；写盘失败只影响下次启动。</summary>
    public static void Set(bool isDark)
    {
        IsDark = isDark;
        Apply();

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (directory != null)
                Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, isDark ? "Dark" : "Light");
        }
        catch (Exception ex)
        {
            Log.Warn("Appearance", "保存主题偏好失败: {Message}", ex.Message);
        }
    }

    /// <summary>把当前偏好写入 Application；运行时切换和启动恢复都走这里。</summary>
    public static void Apply()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = CurrentTheme;
    }
}
