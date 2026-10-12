using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Layeron.Plugin.Abstractions;

namespace Layeron.Plugins.PythonRunner;

/// <summary>
/// The Python environment page: what is deployed, pip, and installing packages.
/// <para>
/// A custom page rather than the host's declarative settings, for one reason: installing a package is an
/// operation with output, not a value. The transcript below the controls is the point of the page - pip is
/// slow and chatty, and hiding it behind a spinner is how an install comes to look like a hang.
/// </para>
/// <para>
/// Every operation here runs against the same engine the console and the editor use, so what this page
/// changes is what those see. Nothing here runs implicitly: pip needs the network and can fail, and the
/// engine is fully usable without it.
/// </para>
/// </summary>
internal sealed partial class PythonSettingsView : UserControl, IDisposable, ISettingsPageContent
{
    private readonly PythonSession? _engine;
    private readonly IPluginLog _log;

    private readonly StringBuilder _output = new();
    /// <summary>
    /// The list's items. Kept as a field and never replaced: a bound collection that is swapped out stops
    /// updating the control.
    /// </summary>
    private readonly ObservableCollection<PythonPackage> _packageItems = [];
    private int _running;

    private TextBlock? _interpreter;
    private TextBlock? _numpy;
    private TextBlock? _pip;
    private TextBlock? _status;
    private TextBlock? _hint;
    private TextBox? _input;
    private TextBox? _outputBox;
    private ListBox? _packageList;
    private Button? _cancel;
    private Button? _install;
    private Button? _uninstall;
    private Button? _bootstrap;
    private Button? _upgrade;

    /// <summary>
    /// Set when the settings window that holds this page closes. The host caches the page's content for the
    /// application's lifetime and hands the same control to every window, so a control that kept its visual
    /// parent would be refused by the next window - which is why the page drops out of its parent rather
    /// than staying where it was. Measured: without this the settings window opens once and every later
    /// attempt throws "already has a visual parent" while laying the window out.
    /// </summary>
    private bool _detached;

    /// <summary>
    /// Design-time and loader constructor. The settings window instantiates the page's type through the XAML
    /// loader, which needs a public parameterless constructor.
    /// </summary>
    public PythonSettingsView()
    {
        _log = NullLog.Instance;
        InitializeComponent();
        AfterInitialize();
    }

    internal PythonSettingsView(PythonSession engine, IPluginLog log)
    {
        _engine = engine;
        _log = log;

        InitializeComponent();
        AfterInitialize();

        _hint!.Text = $"安装在全局站点包目录（{engine.Home}），对该环境中的所有脚本可见。";
        RefreshState();

        // The window that first shows this page is the window it is detached from on the next open, so the
        // hook is installed now rather than when a specific window is known.
        DetachedFromVisualTree += (_, _) => _detached = true;
        AttachedToVisualTree += (_, _) => _detached = false;

        _ = ReloadPackagesAsync();
    }

    /// <summary>
    /// Called by the settings window just before this page is shown again.
    /// <para>
    /// A cached control can only be re-hosted by one parent at a time, so the page takes itself back out of
    /// the tree it was in. The alternative was to give the host a new control per window, which would mean
    /// rebuilding the page - and losing the output of an install the user is watching - every time the
    /// settings window is reopened.
    /// </para>
    /// </summary>
    public void Detach()
    {
        if (_detached)
            return;

        if (this.GetVisualParent() is ContentControl host && ReferenceEquals(host.Content, this))
            host.Content = null;

        _detached = true;
    }

    public void Dispose()
    {
        // The plugin is being unloaded while an install may still be running: the page owns that process, so
        // cancelling is what lets the load context go instead of being held by a live child.
        _cts?.Cancel();
    }

    /// <summary>
    /// Repaints the three status lines from the filesystem.
    /// <para>
    /// A probe rather than a query to the interpreter on purpose: this runs while the user is looking at the
    /// page, and starting an interpreter to answer "is numpy there" would deploy one to find out. The lines
    /// therefore describe the deployed environment, which is what the buttons below them change.
    /// </para>
    /// </summary>
    private void RefreshState()
    {
        if (_engine is null)
            return;

        _interpreter!.Text = _engine.InterpreterAvailable
            ? $"解释器：已部署 · {_engine.Home}"
            : "解释器：尚未部署（打开 REPL 或运行脚本时自动部署）";

        _numpy!.Text = _engine.NumpyAvailable
            ? "numpy：已部署（随解释器一起；pip 安装的包按它的 ABI 解析）"
            : "numpy：尚未部署（随解释器部署）";

        _pip!.Text = _engine.PipAvailable
            ? "pip：已安装"
            : "pip：未安装 — “初始化 pip”需要网络，只在使用包时需要";

        // Only meaningful once pip exists, and asking pip for the list without it fails, so the buttons that
        // need it are disabled rather than left to produce an error the user cannot act on.
        if (_upgrade is not null)
            _upgrade.IsEnabled = _engine.PipAvailable;
    }

    /// <summary>
    /// Fills the package list. Never throws: a page that cannot list packages must still be able to install
    /// or bootstrap the thing that is missing.
    /// <para>
    /// Started from the page's constructor, so it runs while the first window is still being shown: the pip
    /// child process is waited for on a background thread rather than on the one opening the window.
    /// </para>
    /// </summary>
    private async Task ReloadPackagesAsync()
    {
        if (_engine is null || _packageList is null)
            return;

        try
        {
            var packages = await Task.Run(() => _engine.ListPackagesAsync());

            _packageItems.Clear();
            foreach (var package in packages)
                _packageItems.Add(package);

            _packageList.ItemsSource = _packageItems;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not refresh the package list: {Message}", ex.Message);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<bool>> work, string status)
    {
        if (_engine is null)
            return;

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            SetStatus("正在执行上一个操作…");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;

        SetRunning(true);
        SetStatus(status);
        Append($"--- {status} ---");

        try
        {
            // Off the UI thread: an operation here can start a pip child process and wait on it, and the first
            // one also deploys the interpreter - so awaiting it on the thread that draws the window is what
            // used to leave the page's Cancel button unresponsive for the whole install.
            var ok = await Task.Run(() => work(cts.Token));
            SetStatus(ok ? $"{status}：完成" : $"{status}：失败（见上方输出）");
        }
        catch (Exception ex)
        {
            _log.Error("Python environment operation failed", ex);
            Append($"--- 未预期的错误：{ex.Message} ---");
            SetStatus("失败");
        }
        finally
        {
            _cts = null;

            // Released here, not in SetRunning: the guard is what makes the next operation start at all, and
            // forgetting it leaves the page refusing every later click with "still running". Measured - that
            // was a real bug, not a hypothetical one.
            Interlocked.Exchange(ref _running, 0);

            SetRunning(false);
            RefreshState();

            // The list is what the operation just changed, so it is reloaded whatever the outcome: a failed
            // install can still have installed something, and a rollback is what the user does next.
            _ = ReloadPackagesAsync();
        }
    }

    private void OnBootstrapPip() => _ = RunAsync(
        ct => _engine!.BootstrapPipAsync(Progress(), ct),
        "初始化 pip");

    private void OnUpgradePip() => _ = RunAsync(
        ct => _engine!.UpgradePipAsync(Progress(), ct),
        "升级 pip");

    private void OnInstall() => _ = RunAsync(
        ct => _engine!.RunPipAsync(["install", .. Specs()], Progress(), ct),
        "安装");

    private void OnUninstall() => _ = RunAsync(
        ct => _engine!.RunPipAsync(["uninstall", "-y", .. Specs()], Progress(), ct),
        "卸载");

    private void OnRefresh()
    {
        RefreshState();
        _ = ReloadPackagesAsync();
    }

    private void OnCancel()
    {
        _cts?.Cancel();
        SetStatus("正在取消…");
    }

    /// <summary>
    /// Splits whatever the user typed into package arguments.
    /// <para>
    /// One per line or separated by spaces, because both are things people do: a list of packages is usually
    /// one per line, a single version-pinned spec is typed in one go. Commas count as separators too, since
    /// that is how such a list is usually pasted.
    /// </para>
    /// </summary>
    private string[] Specs()
    {
        var text = _input?.Text ?? string.Empty;

        return text
            .Split(['\n', '\r', ' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private IProgress<PythonOutput> Progress() => new Progress<PythonOutput>(chunk =>
        Dispatcher.UIThread.Post(() => Append(chunk.Stream == PythonOutput.StandardError
            ? $"! {chunk.Text}"
            : chunk.Text)));

    /// <summary>
    /// Appends one line to the output area and keeps it scrolled to the end, which is where pip is.
    /// </summary>
    private void Append(string line)
    {
        if (_outputBox is null)
            return;

        _output.Append(line).Append('\n');
        _outputBox.Text = _output.ToString();
        _outputBox.CaretIndex = _outputBox.Text.Length;
    }

    private void SetRunning(bool running)
    {
        _cancel?.SetCurrentValue(IsVisibleProperty, running);
        _install?.SetCurrentValue(IsEnabledProperty, !running);
        _uninstall?.SetCurrentValue(IsEnabledProperty, !running);
        _bootstrap?.SetCurrentValue(IsEnabledProperty, !running);
        _upgrade?.SetCurrentValue(IsEnabledProperty, !running && (_engine?.PipAvailable ?? false));
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.Text = text;
    }

    private void AfterInitialize()
    {
        _interpreter = this.FindControl<TextBlock>("InterpreterText");
        _numpy = this.FindControl<TextBlock>("NumpyText");
        _pip = this.FindControl<TextBlock>("PipText");
        _status = this.FindControl<TextBlock>("StatusText");
        _hint = this.FindControl<TextBlock>("HintText");
        _input = this.FindControl<TextBox>("PackageInput");
        _outputBox = this.FindControl<TextBox>("Output");
        _packageList = this.FindControl<ListBox>("Packages");
        _cancel = this.FindControl<Button>("CancelButton");
        _install = this.FindControl<Button>("InstallButton");
        _uninstall = this.FindControl<Button>("UninstallButton");
        _bootstrap = this.FindControl<Button>("BootstrapPipButton");
        _upgrade = this.FindControl<Button>("UpgradePipButton");

        Wire("BootstrapPipButton", OnBootstrapPip);
        Wire("UpgradePipButton", OnUpgradePip);
        Wire("RefreshButton", OnRefresh);
        Wire("InstallButton", OnInstall);
        Wire("UninstallButton", OnUninstall);
        Wire("CancelButton", OnCancel);

        if (this.FindControl<Button>("ScikitImagePreset") is { } preset)
            preset.Click += (_, _) =>
            {
                if (_input is not null)
                    _input.Text = ScikitImage;
            };

        if (_hint is not null)
            _hint.Text = $"scikit-image 预设：{ScikitImage}";
    }

    /// <summary>
    /// scikit-image is the preset because it is the package the imaging workflows here actually need, and
    /// because it is the one whose install has a trap: its dependencies are the ones most likely to pull in a
    /// numpy newer than the interpreter's ABI, which the constraint file then has to hold back.
    /// </summary>
    private const string ScikitImage = "scikit-image";

    private CancellationTokenSource? _cts;

    private void Wire(string name, Action action)
    {
        if (this.FindControl<Button>(name) is { } button)
            button.Click += (_, _) => action();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
