using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Yolime.Core;
using Yolime.Core.Logging;

namespace Yolime.App;

public static class Program
{
#if YOLIME_E2E
    private const string InstanceKey = "yolime-main-e2e";
#else
    private const string InstanceKey = "yolime-main";
#endif

    /// <summary>What the E2E build swaps in, read by <see cref="App"/> when it starts the host.</summary>
    internal static HostOverrides? Overrides { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
#if YOLIME_E2E
        // Runs before XAML starts, so no window can ever be created by a test run.
        if (args.Contains("--test-audio")) return E2E.TestAudioRunner.RunAsync(args).GetAwaiter().GetResult();
        if (args.Contains("--e2e"))
        {
            if (E2E.E2EMode.Configure(args) is not { } overrides) return 64;
            Overrides = overrides;
        }
        else
        {
            // The update scenarios' copies start without arguments (setup relaunches them): their settings are in a file.
            Overrides = E2E.E2EMode.ConfigureFromFile(out var refused);
            if (refused) return 64;
        }
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // One instance. A second launch hands its activation to the first (which says "already running") and exits.
        // Its arguments are ignored: another process must not be able to steer the running app.
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // Off the STA thread, as documented: the redirect is a cross-process COM call.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask().Wait()).Wait();
            return 0;
        }

        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Unhandled exceptions are logged as type and HResult only: their messages can quote content.</summary>
    internal static void InstallCrashLogging(ILog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write(LogEvent.UnhandledException, LogField.Error(e.Exception));
            e.SetObserved();
        };
    }
}
