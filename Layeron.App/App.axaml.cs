using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.IO;
using Layeron.App.Logging;
using Layeron.App.Session;
using Layeron.App.ViewModels;
using Layeron.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Layeron.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Logging is the first thing that starts and the last that stops, so everything after
            // this line - including a failure while composing the window - is on record.
            Log.Start(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Layeron", "logs"));
            desktop.Exit += (_, _) => Log.Shutdown();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error("App", (Exception)e.ExceptionObject, "Unhandled exception");

            // Composition root: the only place that wires concrete services together.
            // ISessionManager is the single stable entry point; sessions themselves are
            // dynamic objects whose lifetime is broadcast through its CurrentSession stream.
            var services = new ServiceCollection();
            services.AddSingleton<ISessionManager, SessionManager>();
            services.AddSingleton<MainWindowViewModel>();
            var provider = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow
            {
                DataContext = provider.GetRequiredService<MainWindowViewModel>(),
            };

            Log.Info("App", "Layeron started; log file: {Path}", Log.FilePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}