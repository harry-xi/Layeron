using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Layeron.App;

/// <summary>
/// Locates the main window's storage provider, the only way to show a file or folder dialog.
/// <para>
/// It is a locator rather than an injected service because the window owns the provider and is created
/// after the view models that need to raise dialogs; going through the application lifetime keeps the
/// dependency pointing at the window instead of threading it through every constructor.
/// </para>
/// </summary>
internal static class StorageProviderAccess
{
    public static IStorageProvider Get()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return window?.StorageProvider
            ?? throw new InvalidOperationException("No main window is available for a file dialog.");
    }
}
