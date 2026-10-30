using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Yolime.App.Native;
using Yolime.App.Shell;
using Yolime.Core.Keys;

namespace Yolime.App;

public partial class App : Application
{
    private AppHost? _host;
    private SettingsWindow? _settings;
    private WelcomeWindow? _welcome;
    private HistoryWindow? _history;
    private UpdateWindow? _update;

    public App()
    {
        InitializeComponent();
        // A tray app: it has no window to close, so only Exit from the tray ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost(Program.Overrides);
        // XAML's own exceptions (layout, a page handler) arrive here, not at the AppDomain handler. Logged by type and
        // HRESULT only; the app still goes down, as it would have.
        UnhandledException += (_, e) =>
        {
            if (e.Exception is { } ex) _host?.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
        };
        _host.ExitRequested += () => Shutdown(reset: false);
        _host.ResetRequested += () => Shutdown(reset: true);
        _host.SettingsRequested += () => OpenSettings(SettingsWindow.General);
        _host.FinishSetupRequested += () => OpenSettings(SettingsWindow.KeysPage);
        _host.SettingsPageRequested += OpenSettings;
        _host.HistoryRequested += OpenHistory;
        _host.UpdateWindowRequested += OpenUpdate;
        _host.Updates.WindowOpen = () => _update is not null;
        if (Yolime.Core.Settings.FirstRun.ShowWizard(_host.Settings.Current))
        {
            _welcome = new WelcomeWindow(_host);
            _welcome.Closed += (_, _) => _welcome = null;
        }
        var queue = DispatcherQueue.GetForCurrentThread();
        // A second launch was redirected here. Its arguments are deliberately not read.
        AppInstance.GetCurrent().Activated += (_, _) => queue.TryEnqueue(() => _host?.ShowAlreadyRunning());
    }

    /// <summary>One Settings window: opening it again brings the existing one forward on the asked-for page.</summary>
    private void OpenSettings(string page)
    {
        if (_host is null) return;
        if (_settings is { } open)
        {
            open.Select(page);
            return;
        }
        _settings = new SettingsWindow(_host, page);
        _settings.Closed += (_, _) => _settings = null;
    }

    /// <summary>One History window: opening it again brings it forward. Closing it drops its text from memory.</summary>
    private void OpenHistory()
    {
        if (_host is null) return;
        if (_history is { } open)
        {
            open.Activate();
            return;
        }
        _history = new HistoryWindow(_host);
        _history.Closed += (_, _) => _history = null;
    }

    /// <summary>One update window: opening it again brings it forward.</summary>
    private void OpenUpdate()
    {
        if (_host is null) return;
        if (_update is { } open)
        {
            open.BringForward();
            return;
        }
        _update = new UpdateWindow(_host.Updates);
        _update.Closed += (_, _) => _update = null;
    }

    /// <summary>
    /// Windows close before the host is disposed (they read it), and a reset wipes only after disposal, when the
    /// history database is closed. The log is appended per line, so it holds no file open.
    /// </summary>
    private void Shutdown(bool reset)
    {
        if (_host is not { } host) return;
        _host = null;
        _settings?.Close();
        _history?.Close();
        _update?.Close();
        // Not a Skip: on Reset it mustn't write firstRunDone back after the wipe.
        _welcome?.CloseForShutdown();
        var paths = host.Paths;
        host.Dispose();
        if (reset) Wipe(paths);
        Exit();
    }

    private static void Wipe(Yolime.Core.AppPaths paths)
    {
        var store = new WindowsCredentialStore();
        foreach (var slot in KeySource.Slots) store.Delete(slot);
        StartupRegistry.Disable();
        foreach (var dir in new[] { paths.RoamingDir, paths.LocalDir })
        {
            // Only yolime's own folders: an unresolved known folder would make the path relative, and a recursive
            // delete of a relative "Yolime" could remove something else entirely.
            if (!Path.IsPathFullyQualified(dir) || !string.Equals(Path.GetFileName(dir.TrimEnd('\\')), "Yolime", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: whatever survives is recreated or ignored on the next start.
            }
        }
    }
}
