using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yolime.Core.Updates;

namespace Yolime.App.Shell;

/// <summary>
/// "Install yolime X?": Install now, What's new, Not now. After Install it stays open through the wait for a
/// dictation, the download and the check, and shows a failure in place; once setup has started it closes (the
/// Updating card takes over). Everything it shows comes from <see cref="UpdateState"/> through <see cref="UpdateTexts"/>.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly Updater _updater;
    private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _install = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _whatsNew = new() { Content = "What's new" };
    private readonly Button _notNow = new() { Content = "Not now" };
    private bool _closed;

    public UpdateWindow(Updater updater)
    {
        _updater = updater;
        Title = "yolime update";
        SystemBackdrop = new MicaBackdrop();
        _heading.Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"];
        // Narrator reads progress and failures as they change.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        _install.Click += (_, _) => _updater.Install();
        _whatsNew.Click += (_, _) =>
        {
            // The browser opens; this window stays, so Install is still a click away.
            if (_updater.State.Release is { } r) _updater.OpenReleasePage(r.Version);
        };
        _notNow.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_install);
        buttons.Children.Add(_whatsNew);
        buttons.Children.Add(_notNow);
        var root = new StackPanel { Spacing = 14, Padding = new Thickness(24, 20, 24, 20) };
        root.Children.Add(_heading);
        root.Children.Add(_status);
        root.Children.Add(_progress);
        root.Children.Add(buttons);
        Content = root;

        WindowSizing.SizeAndCentre(this, 500, 280);
        updater.StateChanged += OnState;
        Closed += (_, _) =>
        {
            _closed = true;
            updater.StateChanged -= OnState;
        };
        Render(updater.State);
        Activate();
    }

    private void OnState(UpdateState state)
    {
        if (_closed) return;
        // Setup is running: the Updating card says what happens next, and the window would only be in the way.
        if (state.Phase == UpdatePhase.Updating)
        {
            Close();
            return;
        }
        Render(state);
    }

    private void Render(UpdateState state)
    {
        var version = state.Release?.Version;
        _heading.Text = version is { } v ? UpdateTexts.WindowTitle(v) : "No update to install";
        _status.Text = version is null ? "yolime is up to date, or the update hasn't been found yet." : UpdateTexts.WindowStatus(state);
        _install.Content = UpdateTexts.InstallButton(state);
        // Closing doesn't stop an install under way, so the button doesn't claim to.
        _notNow.Content = UpdateTexts.DismissButton(state);
        // A second click while one is in progress does nothing; the button says so by being off.
        _install.IsEnabled = state.CanInstall;
        _whatsNew.IsEnabled = version is not null;
        var busy = state.Phase is UpdatePhase.Finishing or UpdatePhase.Downloading;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = state.Phase == UpdatePhase.Finishing || (state.Phase == UpdatePhase.Downloading && state.Progress >= 1);
        _progress.Value = state.Progress;
    }

    /// <summary>Brings an open window forward.</summary>
    public void BringForward()
    {
        Render(_updater.State);
        Activate();
    }
}
