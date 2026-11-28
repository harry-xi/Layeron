using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
#if DEBUG
using Spyglass.Agent; 
#endif

namespace Layeron.App;

sealed partial class Program
{
    /// <summary>
    /// True when this process is attached to a console it did not create, i.e. it was started from a
    /// terminal. A GUI application launched from Explorer has no console and must not create one.
    /// </summary>
    public static bool HasConsole { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Begin();
        StartupTrace.Mark("main-entry");

        // Attach only to a console that already exists. There is deliberately no AllocConsole fallback:
        // double-clicking the app must not open a console window, and the log file plus the in-app log
        // window are the diagnostics for that case. A terminal launch gets its logs on the terminal.
        HasConsole = !OperatingSystem.IsWindows() || AttachConsole(ATTACH_PARENT_PROCESS);
        StartupTrace.Mark($"console-attached:{HasConsole}");
        if (HasConsole)
            Console.WriteLine("Console attached.");


        BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
    } 

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterPlatformServicesSetup(_ => StartupTrace.Mark("platform-services-ready"))
            .AfterSetup(_ => StartupTrace.Mark("avalonia-setup-complete"))
            .WithInterFont()
            .LogToTrace()
            #if DEBUG
                .UseSpyglass()
            #endif
            .UseReactiveUI(_ => { });
        StartupTrace.Mark("appbuilder-configured");
        return builder;
    }


        // Console attachment P/Invoke definitions
    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint dwProcessId);
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
}
