using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        }

        base.OnFrameworkInitializationCompleted();
    }
}