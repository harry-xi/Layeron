using Avalonia;
using ReactiveUI.Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Layeron.App;

sealed partial class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();
            Console.WriteLine("Console attached.");
        }


        BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
    } 

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(_ => { });


        // Console attachment P/Invoke definitions
    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint dwProcessId);
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
}
