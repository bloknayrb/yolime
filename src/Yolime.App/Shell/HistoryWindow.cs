using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yolime.App.Native;
using Yolime.Core;
using Yolime.Core.Feedback;
using Yolime.Core.History;
using Yolime.Core.Hotkeys;
using Yolime.Core.Settings;

namespace Yolime.App.Shell;

/// <summary>
/// History: the runs list on the left, the selected run on the right. Built in code like Settings; every label and
/// enable rule comes from <see cref="HistoryItem"/>. Reads run off the UI thread; the window is excluded from screen
/// capture like the notch, and the context of a run is only put on screen when asked for.
/// </summary>
internal sealed partial class HistoryWindow : Window
{
    private const int PageSize = 200;

    private readonly AppHost _host;
    private readonly ObservableCollection<HistoryItem> _items = [];
    private readonly Dictionary<long, HistoryRow> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _search = new() { PlaceholderText = "Search", MinWidth = 200 };
    private readonly ComboBox _modeFilter = new() { MinWidth = 120 };
    private readonly Button _older = new() { Content = "Load older", Visibility = Visibility.Collapsed };
    private readonly InfoBar _banner = new() { IsClosable = false };
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    private readonly StackPanel _detail = new() { Spacing = 12, Padding = new Thickness(20, 16, 20, 20) };
    private readonly ScrollViewer _detailScroll = new();
    private readonly TextBlock _retention = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly SemaphoreSlim _listGate = new(1, 1);
    private HistoryCursor? _cursor;
    private long? _shownId;
    private string _lastSelection = "";
    private CancellationTokenSource? _searchDelay;
    private bool _busy;
    private bool _dialogOpen;
    private bool _closed;
    private bool _swapping;
    private bool _readFailed;
    private long? _selectAfterReload;
    private Button? _reinsert;
    private readonly Dictionary<(uint Pid, long Start), string> _appNames = [];
    private HistoryRow? _contextRow;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;

    public HistoryWindow(AppHost host)
    {
        // Captured here, on the UI thread: Window.DispatcherQueue can't be read from the background threads that
        // raise history, settings and meter events (it throws RPC_E_WRONG_THREAD).
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _host = host;
        Title = "yolime history";
        SystemBackdrop = new MicaBackdrop();

        _modeFilter.Items.Add("All modes");
        foreach (var m in Enum.GetValues<Mode>()) _modeFilter.Items.Add(m.ToString());
        _modeFilter.SelectedIndex = 0;
        AutomationProperties.SetName(_modeFilter, "Mode filter");
        AutomationProperties.SetName(_search, "Search history");
        AutomationProperties.SetName(_list, "Runs");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _list.ItemsSource = _items;
        // An empty template the row is drawn into: with no template, the container shows the item's ToString
        // ("HistoryItem { Id = 4, … }"), whatever is set as its content here.
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Border /></DataTemplate>""");
        _list.ContainerContentChanging += (_, e) =>
        {
            // Each row is drawn from code and named for Narrator with everything it shows.
            if (e.InRecycleQueue || e.Item is not HistoryItem item || e.ItemContainer is not ListViewItem container) return;
            if (container.ContentTemplateRoot is Border root) root.Child = RowView(item);
            AutomationProperties.SetName(container, item.AccessibleName);
            e.Handled = true;
        };
        _list.SelectionChanged += (_, _) => ShowSelected();
        _older.Click += (_, _) => Safe(() => LoadAsync(append: true));
        _search.TextChanged += (_, _) => SearchSoon();
        _modeFilter.SelectionChanged += (_, _) => Safe(() => LoadAsync(append: false));

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 12, 12, 4) };
        top.Children.Add(_search);
        top.Children.Add(_modeFilter);

        var left = new Grid { MinWidth = 340 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(left, top, 0);
        Add(left, _banner, 1);
        Add(left, _list, 2);
        Add(left, _empty, 2);
        Add(left, _older, 3);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 4, 12, 12) };
        var change = new HyperlinkButton { Content = "Change" };
        AutomationProperties.SetName(change, "Change how long runs are kept");
        change.Click += (_, _) => _host.OpenSettings(SettingsWindow.OverlayHistory);
        var clear = new Button { Content = "Clear all\u2026" };
        clear.Click += (_, _) => Safe(ClearAllAsync);
        footer.Children.Add(_retention);
        footer.Children.Add(change);
        footer.Children.Add(clear);
        Add(left, footer, 4);

        _detailScroll.Content = _detail;
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Add(right, _status, 0);
        Add(right, _detailScroll, 1);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(left);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;
        Keys(root);

        WindowSizing.SizeAndCentre(this, 1040, 720);
        ApplyCapture();

        host.HistoryChanged += OnHistoryChanged;
        host.Settings.Changed += OnSettingsChanged;
        Activated += (_, e) =>
        {
            // Context comes off the screen when the window is left.
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideContext();
            // Back from another app: Re-insert now targets that one, so the label says so.
            else if (_reinsert is not null) _reinsert.Content = ReinsertLabel();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            host.HistoryChanged -= OnHistoryChanged;
            host.Settings.Changed -= OnSettingsChanged;
            _searchDelay?.Cancel();
            _items.Clear();
            _rows.Clear();
            _detail.Children.Clear();
        };
        UpdateBanner();
        Safe(() => LoadAsync(append: false, selectFirst: true));
        // Retention applies when History opens too, not only at startup and every six hours.
        var days = host.Settings.Current.HistoryRetentionDays;
        _ = Task.Run(() => host.PruneHistory(days));
        Activate();
    }

    private static void Add(Grid grid, FrameworkElement child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    // ---------------------------------------------------------------- keyboard

    private void Keys(UIElement root)
    {
        void On(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var accel = new KeyboardAccelerator { Key = key, Modifiers = mods };
            accel.Invoked += (_, e) =>
            {
                e.Handled = true;
                action();
            };
            root.KeyboardAccelerators.Add(accel);
        }
        On(VirtualKey.F, VirtualKeyModifiers.Control, () => _search.Focus(FocusState.Keyboard));
        On(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (_search.FocusState != FocusState.Unfocused && _search.Text.Length > 0) _search.Text = "";
            else Close();
        });
        On(VirtualKey.F6, VirtualKeyModifiers.None, () =>
        {
            if (_list.FocusState == FocusState.Unfocused) _list.Focus(FocusState.Keyboard);
            else (FocusManager.FindFirstFocusableElement(_detail) as Control)?.Focus(FocusState.Keyboard);
        });
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Delete || Selected is not { } item) return;
            e.Handled = true;
            Safe(() => DeleteAsync(item));
        };
    }

    // ---------------------------------------------------------------- loading

    private HistoryItem? Selected => _list.SelectedItem as HistoryItem;

    private Mode? ModeFilter => _modeFilter.SelectedIndex <= 0 ? null : Enum.GetValues<Mode>()[_modeFilter.SelectedIndex - 1];

    private HistoryItem ItemOf(HistoryRow row) => HistoryItem.From(row, DisplayNames.For, DateTimeOffset.Now, TimeZoneInfo.Local);

    /// <summary>Loads the first page (or the next one) off the UI thread. A search loads every match instead.</summary>
    /// <param name="keepSelection">
    /// Reloads after a change: keep the row selected when the reload actually starts (or the one a delete asked for),
    /// read inside the gate so a load still running first counts, and select the first row if nothing was selected.
    /// </param>
    private async Task LoadAsync(bool append, bool selectFirst = false, long? select = null, bool keepSelection = false)
    {
        // One list update at a time: a refresh and a reload interleaving at their awaits would both rewrite the list.
        await _listGate.WaitAsync();
        try
        {
            if (keepSelection)
            {
                select = _selectAfterReload ?? Selected?.Id;
                _selectAfterReload = null;
                // Falls back to the first row, but never during a search and never taking focus: a reload after a run
                // mustn't pull the cursor out of the search box mid-word.
                if (select is null && _search.Text.Trim().Length == 0 && _items.Count > 0) select = _items[0].Id;
            }
            await LoadCoreAsync(append, selectFirst, select);
        }
        finally
        {
            _listGate.Release();
        }
    }

    private async Task LoadCoreAsync(bool append, bool selectFirst, long? select)
    {
        var term = _search.Text;
        var mode = ModeFilter;
        var cursor = append ? _cursor : null;
        IReadOnlyList<HistoryRow> rows;
        List<HistoryItem> built;
        try
        {
            (rows, built) = await Task.Run(() =>
            {
                IReadOnlyList<HistoryRow> found = term.Trim().Length > 0
                    ? HistorySearch.Filter(_host.History.All().Where(r => mode is null || r.Mode == mode), term, DisplayNames.For)
                    : _host.History.Page(PageSize, cursor, mode);
                // Display names enumerate processes: never on the UI thread, row by row.
                return (found, found.Select(ItemOf).ToList());
            });
            _readFailed = false;
        }
        catch (Exception ex) when (ex is HistoryUnavailableException or ObjectDisposedException)
        {
            _readFailed = true;
            ShowUnreadable();
            return;
        }
        if (_closed) return;
        if (!append)
        {
            _items.Clear();
            _rows.Clear();
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (!_rows.TryAdd(rows[i].Id, rows[i])) continue;
            _items.Add(built[i]);
        }
        _cursor = rows.Count > 0 ? HistoryCursor.After(rows[^1]) : _cursor;
        _older.Visibility = term.Trim().Length == 0 && rows.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
        if (term.Trim().Length > 0 && rows.Count >= HistorySearch.MaxHits) Say(InfoBarSeverity.Informational, $"Showing the first {HistorySearch.MaxHits} matches.");
        UpdateEmpty();
        if (select is { } id && _items.FirstOrDefault(i => i.Id == id) is { } wanted) _list.SelectedItem = wanted;
        else if (selectFirst && _items.Count > 0)
        {
            _list.SelectedIndex = 0;
            _list.Focus(FocusState.Programmatic);
        }
        if (_list.SelectedItem is null) ShowDetailFor(null);
    }

    private void SearchSoon()
    {
        _searchDelay?.Cancel();
        var delay = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(250, delay.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ui.TryEnqueue(() => Safe(() => LoadAsync(append: false)));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// A finished run comes in at the top without disturbing the user's place; a changed row is redrawn where it is.
    /// Clear, prune and delete reload, and a selected run that's gone leaves the detail pane empty at once.
    /// </summary>
    private void OnHistoryChanged(HistoryChange change) => _ui.TryEnqueue(() => Safe(() => RefreshAsync(change)));

    private async Task RefreshAsync(HistoryChange change)
    {
        if (_closed) return;
        if (change == HistoryChange.Reset || _search.Text.Trim().Length > 0)
        {
            await LoadAsync(append: false, keepSelection: true);
            return;
        }
        await _listGate.WaitAsync();
        try
        {
            await PrependAsync();
        }
        finally
        {
            _listGate.Release();
        }
    }

    private async Task PrependAsync()
    {
        IReadOnlyList<HistoryRow> fresh;
        List<HistoryItem> built;
        // Read on the UI thread: a control's properties throw RPC_E_WRONG_THREAD from the pool (E2E caught this).
        var mode = ModeFilter;
        try
        {
            (fresh, built) = await Task.Run(() =>
            {
                var page = _host.History.Page(50, null, mode);
                return (page, page.Select(ItemOf).ToList());
            });
        }
        catch (Exception ex) when (ex is HistoryUnavailableException or ObjectDisposedException)
        {
            return;
        }
        if (_closed) return;
        var insertAt = 0;
        for (var i = 0; i < fresh.Count; i++)
        {
            var row = fresh[i];
            if (_rows.TryGetValue(row.Id, out var known))
            {
                // Replaced only when something shown changed: a swap clears the ListView's selection, and the detail pane
                // (a text selection, revealed context, an open flyout) must survive a refresh.
                if (!Shows(known, row)) continue;
                _rows[row.Id] = row;
                var index = IndexOf(row.Id);
                if (index < 0) continue;
                var wasSelected = Selected?.Id == row.Id;
                _swapping = true;
                try
                {
                    _items[index] = built[i];
                    if (wasSelected) _list.SelectedIndex = index;
                }
                finally
                {
                    _swapping = false;
                }
                if (wasSelected) ShowDetailFor(built[i], force: true);
                continue;
            }
            // Re-runs are placed beside their source by the re-run itself; anything else new is a run that just finished.
            if (row.RerunOf is not null) continue;
            if (_items.Count > 0 && _rows.TryGetValue(_items[0].Id, out var top) && row.StartedUtc < top.StartedUtc) continue;
            _rows[row.Id] = row;
            _items.Insert(insertAt++, built[i]);
        }
        UpdateEmpty();
    }

    /// <summary>Whether anything the window shows differs (records compare their stage dictionaries by reference).</summary>
    private static bool Shows(HistoryRow a, HistoryRow b) =>
        (a.Outcome, a.Delivery, a.Mode, a.Output, a.RawText, a.PendingAudioFile, a.AudioExpired, a.TotalMs, a.ProcessName, a.Context, a.StageMs.Count)
        != (b.Outcome, b.Delivery, b.Mode, b.Output, b.RawText, b.PendingAudioFile, b.AudioExpired, b.TotalMs, b.ProcessName, b.Context, b.StageMs.Count);

    private int IndexOf(long id)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == id) return i;
        return -1;
    }

    private void OnSettingsChanged(YolimeSettings old, YolimeSettings now) => _ui.TryEnqueue(() => Safe(() =>
    {
        if (_closed) return Task.CompletedTask;
        if (old.HistoryPaused != now.HistoryPaused || old.HistoryRetentionDays != now.HistoryRetentionDays)
        {
            UpdateBanner();
            UpdateEmpty();
        }
        if (old.HideOverlayFromCapture != now.HideOverlayFromCapture) ApplyCapture();
        return Task.CompletedTask;
    }));

    private void UpdateBanner()
    {
        var s = _host.Settings.Current;
        _retention.Text = s.HistoryRetentionDays == 1 ? "Runs are kept for 1 day." : $"Runs are kept for {s.HistoryRetentionDays} days.";
        // A read that failed keeps its message until a read succeeds; a settings change doesn't hide it.
        if (_host.History.IsReadOnly || _readFailed)
        {
            ShowUnreadable();
            return;
        }
        if (s.HistoryPaused)
        {
            var resume = new Button { Content = "Resume" };
            resume.Click += (_, _) => _host.Settings.Update(x => x with { HistoryPaused = false });
            _banner.Severity = InfoBarSeverity.Informational;
            _banner.Title = "History is paused";
            _banner.Message = "Nothing new is kept until you resume.";
            _banner.ActionButton = resume;
            _banner.IsOpen = true;
        }
        else _banner.IsOpen = false;
        if (Selected is { } item) ShowDetailFor(item, force: true);
    }

    private void ShowUnreadable()
    {
        _banner.Severity = InfoBarSeverity.Error;
        _banner.Title = "History can't be read";
        _banner.Message = _host.History.IsReadOnly
            ? "It was written by a newer yolime. New runs aren't being kept."
            : "History couldn't be read just now. Close this window and open it again; if it keeps happening, the file may be damaged.";
        _banner.ActionButton = null;
        _banner.IsOpen = true;
        _empty.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmpty()
    {
        if (_items.Count > 0 || _host.History.IsReadOnly)
        {
            _empty.Visibility = Visibility.Collapsed;
            return;
        }
        _empty.Text = _search.Text.Trim().Length > 0 || ModeFilter is not null ? "Nothing matches."
            : _host.Settings.Current.HistoryPaused ? "History is paused, so nothing new is kept here."
            : $"No runs yet. Hold {HoldKeyRules.Name(_host.Settings.Current.HoldKeyVk)}, speak, and let go: your runs appear here.";
        _empty.Visibility = Visibility.Visible;
    }

    private void ApplyCapture()
    {
        // Like the notch: transcripts and app names stay out of screenshots and screen sharing.
        const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowDisplayAffinity(hwnd, _host.Settings.Current.HideOverlayFromCapture ? WdaExcludeFromCapture : WdaNone);
    }

    // ---------------------------------------------------------------- list row

    private static UIElement RowView(HistoryItem item)
    {
        var panel = new StackPanel { Spacing = 2, Padding = new Thickness(0, 6, 0, 6) };
        var top = new TextBlock { Opacity = 0.75, FontSize = 12 };
        top.Text = $"{item.When} \u00b7 {item.Mode} \u00b7 {item.App}" + (item.RerunOf is null ? "" : " \u00b7 re-run");
        panel.Children.Add(top);
        panel.Children.Add(new TextBlock { Text = item.FirstLine, TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new TextBlock { Text = item.Status, FontSize = 12, FontWeight = item.Severity == HistorySeverity.Ok ? FontWeights.Normal : FontWeights.SemiBold };
        panel.Children.Add(status);
        return panel;
    }

    // ---------------------------------------------------------------- detail

    private void ShowSelected()
    {
        // A refresh swapping the selected row's item must not tear the detail pane down.
        if (_swapping) return;
        HideContext();
        // Add word starts from a selection in this run, not one left over from another.
        _lastSelection = "";
        ShowDetailFor(Selected);
    }

    private void ShowDetailFor(HistoryItem? item, bool force = false)
    {
        if (item is null)
        {
            _shownId = null;
            _detail.Children.Clear();
            return;
        }
        // Rebuilt only when the selection changes, so a text selection the user is making survives a refresh.
        if (!force && _shownId == item.Id && _detail.Children.Count > 0) return;
        _shownId = item.Id;
        _detail.Children.Clear();
        if (!_rows.TryGetValue(item.Id, out var row)) return;

        _detail.Children.Add(new TextBlock { Text = $"{item.When} \u00b7 {item.Mode} \u00b7 {item.App}", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        var facts = new List<string> { item.Status };
        if (row.TotalMs is { } total) facts.Add($"{total / 1000.0:0.0} s");
        // A stage that never ran (Raw's rewrite, a short Dictate cleaned locally) still has a time: it isn't a fact.
        foreach (var (stage, ms) in row.StageMs)
            if (!row.Stages.TryGetValue(stage, out var status) || status != StageStatus.Skipped) facts.Add($"{StageName(stage)} {ms / 1000.0:0.0} s");
        // Said only when the Retry button is there too.
        if (item.CanRetryAudio) facts.Add("recording kept for Retry");
        _detail.Children.Add(new TextBlock { Text = string.Join(" \u00b7 ", facts), Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        if (row.RerunOf is { } source)
        {
            var label = _rows.TryGetValue(source, out var src) ? HistoryItem.RelativeTime(src.StartedUtc, DateTimeOffset.Now, TimeZoneInfo.Local) : "an earlier run";
            if (label.StartsWith("Today", StringComparison.Ordinal) || label.StartsWith("Yesterday", StringComparison.Ordinal))
                label = char.ToLowerInvariant(label[0]) + label[1..];
            _detail.Children.Add(new TextBlock { Text = $"Re-run of {label}", Opacity = 0.8 });
        }

        _detail.Children.Add(Actions(item, row));

        if (row.Output is { Length: > 0 } output) _detail.Children.Add(TextSection("Output", output));
        if (row.RawText is { Length: > 0 } raw) _detail.Children.Add(TextSection("Raw transcript", raw));
        if (row.Output is { Length: > 0 } o && row.RawText is { Length: > 0 } r && o != r) _detail.Children.Add(DiffSection(r, o));
        if (item.HasContext) _detail.Children.Add(ContextSection(row));
    }

    private static string StageName(Stage stage) => stage switch
    {
        Stage.Capture => "Recording",
        Stage.Stt => "Transcribe",
        Stage.Rewrite => "Claude",
        Stage.Output => "Paste",
        _ => stage.ToString(),
    };

    private UIElement TextSection(string title, string text)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 };
        AutomationProperties.SetName(box, title);
        // Add word starts from whatever was selected last in either box.
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedText.Length > 0) _lastSelection = box.SelectedText;
        };
        panel.Children.Add(box);
        return panel;
    }

    private UIElement DiffSection(string raw, string output)
    {
        var diff = WordDiff.Compute(raw, output);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = diff.TooLong ? "The output is too long to compare word by word." : "What changed: " + diff.Summary, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (diff.TooLong) return panel;
        var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(text, "Changes from the transcript to the output: " + diff.Summary);
        var paragraph = new Paragraph();
        foreach (var segment in diff.Segments)
        {
            var run = new Run { Text = segment.Text + " " };
            switch (segment.Kind)
            {
                // Struck through and underlined, not only coloured, so it reads in high contrast too.
                case DiffKind.Removed:
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    run.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xC4, 0x2B, 0x1C));
                    break;
                case DiffKind.Added:
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    run.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x10, 0x7C, 0x10));
                    break;
            }
            paragraph.Inlines.Add(run);
        }
        text.Blocks.Add(paragraph);
        panel.Children.Add(text);
        return panel;
    }

    private readonly StackPanel _contextHost = new() { Spacing = 4 };

    /// <summary>The context isn't in the window (or Narrator's tree) until the user asks for it.</summary>
    private UIElement ContextSection(HistoryRow row)
    {
        _contextRow = row;
        _contextHost.Children.Clear();
        var show = new Button { Content = "Show the context sent to Claude" };
        show.Click += (_, _) =>
        {
            _contextHost.Children.Clear();
            var box = new TextBox { Text = row.Context, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200 };
            AutomationProperties.SetName(box, "Context sent to Claude");
            var hide = new Button { Content = "Hide" };
            hide.Click += (_, _) => HideContext();
            _contextHost.Children.Add(new TextBlock { Text = "Context sent to Claude", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            _contextHost.Children.Add(box);
            _contextHost.Children.Add(hide);
        };
        _contextHost.Children.Add(show);
        return _contextHost;
    }

    /// <summary>Takes a revealed context off the screen (and out of the UIA tree), leaving the button to show it again.</summary>
    private void HideContext()
    {
        if (_contextHost.Children.Count <= 1 || _contextRow is not { } row) return;
        ContextSection(row);
    }

    // ---------------------------------------------------------------- actions

    private UIElement Actions(HistoryItem item, HistoryRow row)
    {
        // Wrapped, not scrolled: a scrolling row hid Delete past the pane's edge with no sign it was there.
        var bar = new WrapRow { Spacing = 8 };
        var paused = _host.Settings.Current.HistoryPaused;

        _reinsert = null;
        if (item.CanReinsert)
        {
            var reinsert = new Button { Content = ReinsertLabel(), IsEnabled = !_busy };
            reinsert.Click += (_, _) => Safe(() => ReinsertAsync(row));
            bar.Children.Add(reinsert);
            _reinsert = reinsert;
        }
        if (item.CanCopyOutput) bar.Children.Add(ActionButton("Copy output", () => CopyAsync(row.Output!, "Output copied. It stays in Windows' clipboard history, if that's on.")));
        if (item.CanCopyRaw) bar.Children.Add(ActionButton("Copy transcript", () => CopyAsync(row.RawText!, "Transcript copied. It stays in Windows' clipboard history, if that's on.")));
        if (item.RerunModes.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var mode in item.RerunModes)
            {
                var entry = new MenuFlyoutItem { Text = RerunLabel(mode) };
                var m = mode;
                entry.Click += (_, _) => Safe(() => RerunAsync(row, m));
                menu.Items.Add(entry);
            }
            var rerun = new DropDownButton { Content = "Re-run as", Flyout = menu, IsEnabled = !paused && !_busy };
            AutomationProperties.SetName(rerun, "Re-run as another mode");
            if (paused)
            {
                ToolTipService.SetToolTip(rerun, "History is paused, so a re-run couldn't be kept.");
                // A disabled control shows no tooltip; Narrator gets the reason this way.
                AutomationProperties.SetHelpText(rerun, "Disabled: history is paused, so a re-run couldn't be kept.");
            }
            else if (_busy) AutomationProperties.SetHelpText(rerun, "A re-run is in progress.");
            bar.Children.Add(rerun);
        }
        if (item.CanRetryAudio)
        {
            bar.Children.Add(ActionButton("Retry", async () =>
            {
                // The result shows where a run's result always does, not here: say so rather than leave "Retrying\u2026" up.
                Say(InfoBarSeverity.Informational, "Retrying: the result shows on the notch.");
                await Guarded(() => _host.Coordinator.RetryAsync(row.Id, CancellationToken.None));
            }));
        }
        bar.Children.Add(ActionButton("Add word\u2026", () => AddWord(bar)));
        bar.Children.Add(ActionButton("Delete", () => DeleteAsync(item)));
        var actions = new StackPanel { Spacing = 6 };
        if (paused) actions.Children.Add(new TextBlock { Text = "History is paused: re-runs are off.", Opacity = 0.75 });
        actions.Children.Add(bar);
        return actions;
    }

    /// <summary>Lays its children out left to right, starting a new line when the next one wouldn't fit.</summary>
    private sealed partial class WrapRow : Panel
    {
        public double Spacing { get; init; }

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
        {
            double x = 0, y = 0, line = 0, widest = 0;
            foreach (var child in Children)
            {
                child.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
                var s = child.DesiredSize;
                if (x > 0 && x + s.Width > available.Width) (x, y, line) = (0, y + line + Spacing, 0);
                x += s.Width + Spacing;
                line = Math.Max(line, s.Height);
                widest = Math.Max(widest, x - Spacing);
            }
            return new(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + line);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
        {
            double x = 0, y = 0, line = 0;
            foreach (var child in Children)
            {
                var s = child.DesiredSize;
                if (x > 0 && x + s.Width > final.Width) (x, y, line) = (0, y + line + Spacing, 0);
                child.Arrange(new Windows.Foundation.Rect(x, y, s.Width, s.Height));
                x += s.Width + Spacing;
                line = Math.Max(line, s.Height);
            }
            return final;
        }
    }

    /// <summary>"Re-insert into Outlook": the app the click will paste into, read now.</summary>
    private string ReinsertLabel()
    {
        string? name = null;
        if (_host.LastAppWindow is { } t && !_appNames.TryGetValue((t.Pid, t.ProcessStart), out name))
        {
            // Enumerating processes for a name is slow: once per process, then remembered.
            name = DisplayNames.For(ProcessName(t.Pid));
            _appNames[(t.Pid, t.ProcessStart)] = name;
        }
        return name is { Length: > 0 } ? $"Re-insert into {name}" : "Re-insert";
    }

    private static string RerunLabel(Mode mode) => mode switch
    {
        Mode.Prompt => "Prompt (Claude)",
        Mode.Reply => "Reply (Claude)",
        Mode.Dictate => "Dictate (Claude if long)",
        _ => "Raw (no AI)",
    };

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => Safe(action);
        return button;
    }

    private Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private async Task CopyAsync(string text, string done)
    {
        if (await Guarded(() => _host.Executor.CopyForUserAsync(text, NoticeKind.Copied, CancellationToken.None)))
            Say(InfoBarSeverity.Success, done);
    }

    private async Task RerunAsync(HistoryRow row, Mode mode)
    {
        if (_busy) return;
        SetBusy(true);
        Say(InfoBarSeverity.Informational, $"Re-running as {mode}\u2026");
        Yolime.Core.Pipeline.RunResult? result = null;
        await Guarded(async () => result = await _host.Coordinator.RerunAsync(row.Id, mode, CancellationToken.None));
        SetBusy(false);
        if (result is not { HistoryId: { } id } r)
        {
            Say(InfoBarSeverity.Warning, "The re-run couldn't start: yolime is busy with another run, history is paused, or the run is gone.");
            return;
        }
        Say(r.Outcome is RunOutcome.Completed ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            r.Outcome is RunOutcome.Completed ? $"Re-run as {mode}: copied, ready to paste." : $"Re-run as {mode}: {HistoryItem.From(new HistoryRow { RunId = r.RunId, StartedUtc = DateTimeOffset.Now, Mode = mode, Outcome = r.Outcome }, _ => "", DateTimeOffset.Now, TimeZoneInfo.Local).Status}.");
        await PlaceRerunAsync(id, row.Id);
    }

    /// <summary>
    /// The re-run keeps its source's time, so it belongs beside the source, which may be past the first page: it's put
    /// there rather than reloading page one (which would lose the user's place).
    /// </summary>
    private async Task PlaceRerunAsync(long id, long sourceId)
    {
        await _listGate.WaitAsync();
        try
        {
            var (row, item) = await Task.Run(() =>
            {
                var r = _host.History.Get(id);
                return (r, r is null ? null : ItemOf(r));
            });
            if (_closed || row is null || item is null) return;
            // Only into a list that would show it: not under another mode's filter or a search.
            if ((ModeFilter is { } m && m != row.Mode) || _search.Text.Trim().Length > 0) return;
            var at = IndexOf(row.RerunOf ?? sourceId);
            if (at < 0) return; // the status line already says it's copied
            if (!_rows.ContainsKey(id))
            {
                _rows[id] = row;
                _items.Insert(at, item);
            }
            _list.SelectedItem = _items[IndexOf(id)];
        }
        finally
        {
            _listGate.Release();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) return;
        // The action buttons are built from _busy: rebuild them so they show it.
        if (Selected is { } current) ShowDetailFor(current, force: true);
    }

    private void AddWord(FrameworkElement anchor)
    {
        var box = new TextBox { Text = Trimmed(_lastSelection), MinWidth = 220, PlaceholderText = "The word, spelled correctly" };
        AutomationProperties.SetName(box, "Word to add to the vocabulary");
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        var add = new Button { Content = "Add" };
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = "Add to vocabulary. Fix the spelling if needed:" });
        panel.Children.Add(box);
        panel.Children.Add(add);
        panel.Children.Add(result);
        var flyout = new Flyout { Content = panel };
        add.Click += (_, _) => Submit();

        void Submit()
        {
            var (next, outcome) = SettingsEdits.AddVocabulary(_host.Settings.Current, box.Text);
            if (outcome == VocabularyAdd.Added && _host.Settings.Update(_ => next) != SaveResult.Saved)
            {
                result.Text = "Couldn't save settings.json.";
                return;
            }
            result.Text = outcome switch
            {
                VocabularyAdd.Added => "Added. It's sent with every recording from now on.",
                VocabularyAdd.AlreadyThere => "Already in your vocabulary.",
                VocabularyAdd.Empty => "Type a word first.",
                VocabularyAdd.MultiLine => "One word or name at a time.",
                VocabularyAdd.TooLong => $"That's long for a vocabulary word (up to {SettingsEdits.MaxTermLength} characters).",
                _ => "Your vocabulary is full: remove some words in Settings, Apps & words.",
            };
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(result)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.ShowAt(anchor);
    }

    private static string Trimmed(string selection) =>
        selection.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'') ?? "";

    private async Task DeleteAsync(HistoryItem item)
    {
        var reruns = await Guarded(() => Task.Run(() => _host.History.RerunCount(item.Id)), 0);
        // One run goes at once (Bryan: no guards on his own output). A run with re-runs asks whether they go too: a
        // re-run is often the result the user kept.
        var keepReruns = false;
        if (reruns > 0)
        {
            var choice = await AskDeleteWithReruns(reruns);
            if (choice is null) return;
            keepReruns = choice.Value;
        }
        // The reload after the delete lands on the next row (or the one before, at the end).
        var index = IndexOf(item.Id);
        var survivors = _items.Where(i => i.Id != item.Id && (keepReruns || i.RerunOf != item.Id)).ToList();
        var after = _items.Skip(index + 1).FirstOrDefault(i => survivors.Contains(i));
        var before = _items.Take(Math.Max(0, index)).LastOrDefault(i => survivors.Contains(i));
        _selectAfterReload = (after ?? before)?.Id;
        var deleted = await Guarded(() => _host.DeleteRunAsync(item.Id, keepReruns), (IReadOnlyList<long>)[]);
        if (deleted.Count == 0)
        {
            _selectAfterReload = null;
            Say(InfoBarSeverity.Error, "Couldn't delete: it may already be gone, or history isn't writable.");
            return;
        }
        Say(InfoBarSeverity.Success, deleted.Count == 1 ? "Deleted." : $"Deleted the run and {deleted.Count - 1} re-run{(deleted.Count == 2 ? "" : "s")}.");
        _list.Focus(FocusState.Programmatic);
    }

    /// <summary>True: keep the re-runs. False: delete them too. Null: cancelled.</summary>
    private async Task<bool?> AskDeleteWithReruns(int reruns)
    {
        if (_dialogOpen) return null;
        var plural = reruns == 1 ? "re-run" : $"{reruns} re-runs";
        var dialog = new ContentDialog
        {
            Title = "Delete its re-runs too?",
            Content = new TextBlock { Text = $"This run has {plural} made from it. Delete them as well, or keep them on their own?", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete all",
            SecondaryButtonText = "Keep re-runs",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => false,
                ContentDialogResult.Secondary => true,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            return null;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ClearAllAsync()
    {
        if (!await Confirm("Clear history?", "Every run, transcript and kept recording is deleted. A run in progress finishes first. This can't be undone.", "Clear")) return;
        Say(InfoBarSeverity.Informational, "Clearing\u2026 (waits for a run in progress)");
        var ok = await Guarded(_host.ClearHistoryAsync, false);
        Say(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, ok ? "History cleared." : "History couldn't be cleared: its database isn't writable.");
    }

    // ---------------------------------------------------------------- re-insert

    /// <summary>
    /// Pastes the run's output into the window the user was last in. The target is read now (not when History opened),
    /// checked to be the same window of the same process, brought forward, and only then pasted into, through the
    /// executor with its usual rules. Anything off, and the text is copied instead, saying why.
    /// </summary>
    private async Task ReinsertAsync(HistoryRow row)
    {
        if (_busy || row.Output is not { Length: > 0 } text) return;
        var target = _host.LastAppWindow;
        if (target is not { } t || !t.StillValid())
        {
            await CopyAsync(text, "That window has closed, so the output was copied.");
            return;
        }
        if (_host.Targets.BlocksInput(t.Hwnd))
        {
            await CopyAsync(text, "That window runs as administrator, so the output was copied.");
            return;
        }
        SetBusy(true);
        try
        {
            // A button pressed with Enter fires on key-down: its key-up mustn't land in the target.
            for (var i = 0; i < 40 && AnyKeyDown(); i++) await Task.Delay(25);
            if (AnyKeyDown())
            {
                await CopyAsync(text, "A key was still held down, so the output was copied instead of pasted.");
                return;
            }
            if (t.IsMinimised) t.Restore();
            User32.SetForegroundWindow(t.Hwnd);
            var arrived = false;
            for (var i = 0; i < 20 && !arrived; i++)
            {
                await Task.Delay(50);
                arrived = User32.GetForegroundWindow() == t.Hwnd && t.StillValid();
            }
            if (!arrived)
            {
                await CopyAsync(text, "The window didn't come forward, so the output was copied.");
                return;
            }
            await Task.Delay(300); // UI Automation focus settles after the foreground does
            // Pasted only if the target is still the window in front when the executor looks: a popup in between gets nothing.
            var delivery = await Guarded(() => _host.Coordinator.PasteTextAsync(text, row.Mode, CancellationToken.None, expectHwnd: t.Hwnd), null);
            if (delivery == DeliveryKind.None)
            {
                await CopyAsync(text, "Another window came forward, so the output was copied.");
                return;
            }
            Say(delivery == DeliveryKind.Pasted ? InfoBarSeverity.Success : InfoBarSeverity.Warning, delivery switch
            {
                DeliveryKind.Pasted => "Re-inserted.",
                DeliveryKind.Refused => "Not pasted: that's a password field.",
                null => "yolime is busy with another run; try again in a moment.",
                _ => "Copied instead of pasted (see the notice).",
            });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool AnyKeyDown()
    {
        for (var vk = 0x08; vk <= 0xFE; vk++)
            if ((User32.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private void Say(InfoBarSeverity severity, string message)
    {
        if (_closed) return;
        var wasOpen = _status.IsOpen;
        _status.Severity = severity;
        _status.Message = message;
        _status.IsOpen = true;
        if (wasOpen) FrameworkElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// Every async handler and dispatcher callback in this window runs through here: an exception escaping one ends the
    /// whole app (a stowed exception on the dispatcher, past every other handler). Logged by type only.
    /// </summary>
    private async void Safe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            if (_closed) return;
            try
            {
                Say(InfoBarSeverity.Error, "Something went wrong there; it's in the log. The window has been refreshed.");
                await LoadAsync(append: false, keepSelection: true);
            }
            catch (Exception)
            {
                // Nothing more to do: this is the last line before the app would go down.
            }
        }
    }

    /// <summary>Background work for a button; an exception escaping an async handler would end the app.</summary>
    private async Task<T> Guarded<T>(Func<Task<T>> work, T failed)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            return failed;
        }
    }

    private async Task<bool> Guarded(Func<Task> work) => await Guarded(async () =>
    {
        await work();
        return true;
    }, false);

    private async Task<bool> Confirm(string title, string message, string action)
    {
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            _host.Log.Write(Yolime.Core.Logging.LogEvent.UnhandledException, Yolime.Core.Logging.LogField.Error(ex));
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
