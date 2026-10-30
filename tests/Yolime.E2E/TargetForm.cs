using System.Runtime.InteropServices;

namespace Yolime.E2E;

/// <summary>The harness's own target window: two normal boxes, a password box and a read-only box.</summary>
internal sealed class TargetForm : Form
{
    public TextBox Box1 { get; } = new() { Left = 20, Top = 20, Width = 600, Name = "box1" };
    public TextBox Box2 { get; } = new() { Left = 20, Top = 60, Width = 600, Name = "box2" };
    public TextBox Password { get; } = new() { Left = 20, Top = 100, Width = 600, UseSystemPasswordChar = true, Name = "password" };
    public TextBox ReadOnlyBox { get; } = new() { Left = 20, Top = 140, Width = 600, ReadOnly = true, Name = "readonly" };
    public SlowPasteBox Slow { get; } = new() { Left = 20, Top = 180, Width = 600, Name = "slow" };

    private int _escDowns;
    private int _ctrlCDowns;

    private TargetForm()
    {
        Text = "yolime-e2e-target";
        Width = 700;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Controls.AddRange([Box1, Box2, Password, ReadOnlyBox, Slow]);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Interlocked.Increment(ref _escDowns);
            if (e.KeyCode == Keys.C && e.Control) Interlocked.Increment(ref _ctrlCDowns);
        };
    }

    public int EscDowns => Volatile.Read(ref _escDowns);
    public int CtrlCDowns => Volatile.Read(ref _ctrlCDowns);

    /// <summary>The form runs its own message loop on its own thread; the scenarios drive it from the main thread.</summary>
    public static TargetForm StartOnOwnThread()
    {
        TargetForm? form = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            form = new TargetForm();
            form.Shown += (_, _) => ready.Set();
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        return form!;
    }

    public T On<T>(Func<T> f) => (T)Invoke(f);

    public void On(Action a) => Invoke(a);

    public void Reset()
    {
        On(() =>
        {
            foreach (var b in new TextBox[] { Box1, Box2, Password, ReadOnlyBox, Slow }) b.Text = "";
            Slow.LastReadMs = -1;
            _escDowns = 0;
            _ctrlCDowns = 0;
        });
    }

    /// <summary>
    /// Brings the form to the foreground and focuses a box. The spike found SetForegroundWindow alone is ignored in
    /// the Sandbox logon session, so this also attaches to the foreground thread's input and, failing that, clicks
    /// the box (in physical pixels: the harness is per-monitor DPI aware).
    /// </summary>
    public bool FocusBox(TextBox box)
    {
        var hwnd = On(() => Handle);
        for (var attempt = 0; attempt < 3 && GetForegroundWindow() != hwnd; attempt++)
        {
            var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var ours = GetCurrentThreadId();
            AttachThreadInput(ours, fgThread, true);
            On(() =>
            {
                Activate();
                SetForegroundWindow(hwnd);
            });
            AttachThreadInput(ours, fgThread, false);
            if (GetForegroundWindow() == hwnd) break;
            var center = On(() => box.PointToScreen(new Point(box.Width / 2, box.Height / 2)));
            Input.Click(center.X, center.Y);
            Thread.Sleep(300);
        }
        On(() => box.Focus());
        Thread.Sleep(150);
        return GetForegroundWindow() == hwnd && On(() => box.Focused);
    }

    public bool IsForeground => GetForegroundWindow() == On(() => Handle);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

/// <summary>
/// A box that reads the clipboard late on a paste, as Outlook, Electron apps and slow web editors do: a timer reads it
/// <see cref="DelayMs"/> after Ctrl+V, long after yolime's usual idle + 800 ms restore.
/// </summary>
internal sealed class SlowPasteBox : TextBox
{
    // Fields, not properties: the WinForms designer would otherwise want to serialize them. Touched on the form's thread.
    public int DelayMs = 1500;

    /// <summary>How long the read itself took (the render's round trip to yolime), or -1 before one.</summary>
    public long LastReadMs = -1;

    /// <summary>When the read began (UTC ticks), to line it up with a stall in yolime.</summary>
    public long LastReadAt;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is not (Keys.Control | Keys.V) and not (Keys.Shift | Keys.Insert)) return base.ProcessCmdKey(ref msg, keyData);
        var timer = new System.Windows.Forms.Timer { Interval = DelayMs };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            LastReadAt = DateTime.UtcNow.Ticks;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string? text = null;
            for (var attempt = 0; attempt < 10 && text is null; attempt++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch (ExternalException) { Thread.Sleep(50); }
            }
            LastReadMs = watch.ElapsedMilliseconds;
            SelectedText = text ?? "";
        };
        timer.Start();
        return true;
    }
}
