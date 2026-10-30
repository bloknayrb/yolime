using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Yolime.E2E;

/// <summary>PR5: the tray's paste-last chord and menu, Settings (through UI Automation) and Credential Manager.</summary>
internal sealed partial class Scenarios
{
    private const string SettingsJson = @"C:\e2edata\roaming\settings.json";
    private const string SettingsTitle = "yolime settings";
    private const ushort LWin = 0x5B, LAlt = 0xA4, V = 0x56, RShift = 0xA1;
    private const int CmdPauseHistory = 5, CmdSettings = 6;

    public IEnumerable<Scenario> Onboarding() =>
    [
        new("PasteLast_ChordPastesAgain", PasteLastChord),
        new("Tray_PauseHistory_Saved", TrayPauseHistory),
        new("Settings_OverlayPosition_Saved", SettingsOverlayPosition),
        new("Settings_Key_SavedInCredentialManager_TestUnreachable_Removed", SettingsKeyRoundTrip),
        new("Settings_HoldKeyCapture_RightShiftDictates", SettingsHoldKeyCapture),
    ];

    /// <summary>Dictate, empty the box, press Win+Alt+V: the same result is pasted again.</summary>
    private string? PasteLastChord()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 4000)) return "first paste never landed";
        Thread.Sleep(1500); // the clipboard restore
        form.On(() => form.Box1.Text = "");
        Input.Down(LWin);
        Input.Down(LAlt);
        Input.Tap(V);
        Input.Up(LAlt);
        Input.Up(LWin);
        return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 5000) ? null : $"box1 is '{form.On(() => form.Box1.Text)}'";
    }

    /// <summary>The tray's "Pause history" is saved to settings.json, and unpausing saves it back.</summary>
    private string? TrayPauseHistory()
    {
        if (TrayCommand(CmdPauseHistory) is { } fail) return fail;
        if (!Program.Wait(() => SettingBool("historyPaused") == true, 3000)) return "historyPaused never became true";
        TrayCommand(CmdPauseHistory);
        return Program.Wait(() => SettingBool("historyPaused") == false, 3000) ? null : "historyPaused never went back to false";
    }

    /// <summary>Settings opens from the tray; choosing "Bottom centre" is saved; the window closes.</summary>
    private string? SettingsOverlayPosition()
    {
        using var automation = new UIA3Automation();
        if (OpenSettings(automation) is not { } window) return "Settings never opened";
        try
        {
            if (Find(window, "Overlay & history") is not { } nav) return "no Overlay & history page";
            Activate(nav);
            if (!Program.Wait(() => Find(window, "Position") is not null, 3000)) return "the page never showed";
            var combo = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox))?.AsComboBox();
            if (combo is null) return "no position box";
            combo.Select("Bottom centre");
            return Program.Wait(() => SettingString("overlayPosition") == "BottomCentre", 3000) ? null : $"overlayPosition is {SettingString("overlayPosition")}";
        }
        finally
        {
            CloseQuietly(window);
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// A key saved on the Keys page lands in Credential Manager (never in settings.json); Test can't reach the provider
    /// (the Sandbox has no network); Remove, confirmed, deletes it.
    /// </summary>
    private string? SettingsKeyRoundTrip()
    {
        const string key = "sk-e2e-0123456789abcdefghijklmnop";
        using var automation = new UIA3Automation();
        if (OpenSettings(automation) is not { } window) return "Settings never opened";
        var step = "open";
        try
        {
            if (Find(window, "Keys") is not { } nav) return "no Keys page";
            Activate(nav);
            if (!Program.Wait(() => Find(window, "OpenAI (transcription)") is not null, 3000)) return "the Keys page never showed";
            var boxes = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
            if (boxes.Length == 0) return "no key box";
            step = "type";
            // Through UIA when the box allows it; typing depends on the window holding the foreground.
            if (boxes[0].Patterns.Value.IsSupported) boxes[0].Patterns.Value.Pattern.SetValue(key);
            else
            {
                boxes[0].Focus();
                Thread.Sleep(200);
                Input.Type(key);
            }
            Thread.Sleep(200);
            step = "save";
            // Named for Narrator with the key they act on, which is also how they are found here.
            if (Find(window, "Save OpenAI (transcription) key") is not { } save) return "no named Save button";
            Activate(save);
            if (!Program.Wait(() => CredentialManager.Read("yolime/OPENAI_API_KEY") == key, 3000)) return "the key never reached Credential Manager";
            if (File.ReadAllText(SettingsJson).Contains(key, StringComparison.Ordinal)) return "the key was written to settings.json";

            step = "test";
            if (Find(window, "Test OpenAI (transcription) key") is not { } test) return "no named Test button";
            Activate(test);
            if (!Program.Wait(() => window.FindFirstDescendant(cf => cf.ByName("Couldn't reach the provider. Check your connection and try again.")) is not null, 15_000))
                return "Test never said Unreachable";

            step = "remove";
            if (Find(window, "Remove OpenAI (transcription) key") is not { } remove) return "no named Remove button";
            Activate(remove);
            if (!Program.Wait(() => Find(window, "Remove this key?") is not null, 3000)) return "no confirmation";
            step = "confirm";
            // The dialog's own button, not the page's Remove behind it.
            var pageButton = remove.Properties.RuntimeId.Value;
            // The dialog's title can reach UIA before its buttons do: wait for the button too, not one look.
            FlaUI.Core.AutomationElements.AutomationElement? confirm = null;
            Program.Wait(() => (confirm = window.FindAllDescendants(cf => cf.ByName("Remove"))
                .FirstOrDefault(e => !e.Properties.RuntimeId.Value.SequenceEqual(pageButton))) is not null, 3000);
            if (confirm is null)
            {
                // Names only (button captions, never key text): what the dialog did offer, if it wasn't a timing race.
                var buttons = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                    .Select(b => b.Properties.Name.ValueOrDefault ?? "").Where(n => n.Length > 0);
                return "no Remove button in the dialog; buttons: " + string.Join(" | ", buttons);
            }
            Activate(confirm);
            return Program.Wait(() => CredentialManager.Read("yolime/OPENAI_API_KEY") is null, 3000) ? null : "the key is still in Credential Manager";
        }
        catch (Exception ex)
        {
            return $"at step '{step}': {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            CloseQuietly(window);
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// "Change\u2026" captures the next key: Right Shift is saved, and holding it then dictates (the hook picked it up live).
    /// Right Ctrl is captured back at the end so later runs keep the default.
    /// </summary>
    private string? SettingsHoldKeyCapture()
    {
        if (CaptureHoldKey(RShift) is { } fail) return fail;
        if (Prepare(form.Box1) is { } notReady) return notReady;
        var runs = RunCount();
        Input.Hold(RShift, 1500);
        var dictated = WaitRun(runs) && Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 4000);
        var restored = CaptureHoldKey(Input.RCtrl);
        if (!dictated) return $"holding Right Shift didn't dictate: box1 is '{form.On(() => form.Box1.Text)}'";
        return restored;
    }

    private string? CaptureHoldKey(ushort vk)
    {
        using var automation = new UIA3Automation();
        if (OpenSettings(automation) is not { } window) return "Settings never opened";
        try
        {
            if (Find(window, "General") is { } nav) Activate(nav);
            if (!Program.Wait(() => Find(window, "Change\u2026") is not null, 3000)) return "no Change button";
            Activate(Find(window, "Change\u2026")!);
            if (!Program.Wait(() => window.FindFirstDescendant(cf => cf.ByName("Press the key you want to hold\u2026 (10 seconds)")) is not null, 3000))
                return "capture never armed";
            Thread.Sleep(300);
            Input.Tap(vk);
            return Program.Wait(() => SettingInt("holdKeyVk") == vk, 3000) ? null : $"holdKeyVk is {SettingInt("holdKeyVk")}";
        }
        finally
        {
            CloseQuietly(window);
            Thread.Sleep(800);
        }
    }

    private static AutomationElement? OpenSettings(UIA3Automation automation)
    {
        if (TrayCommand(CmdSettings) is not null) return null;
        AutomationElement? window = null;
        Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName(SettingsTitle))) is not null, 8000);
        if (window is null) return null;
        window.Focus();
        Thread.Sleep(600); // let the first page build
        return window;
    }

    /// <summary>Closes the window if it's still there; a failure here must never hide the scenario's own result.</summary>
    private static void CloseQuietly(AutomationElement window)
    {
        try { window.AsWindow().Close(); }
        catch (Exception) { }
    }

    private static AutomationElement? Find(AutomationElement root, string name) => root.FindFirstDescendant(cf => cf.ByName(name));

    /// <summary>Through patterns, never a mouse click: the harness and the app may not agree on DPI.</summary>
    private static void Activate(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
        else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
        else if (element.Patterns.Toggle.IsSupported) element.Patterns.Toggle.Pattern.Toggle();
        else element.Click();
    }

    private static string? TrayCommand(int command)
    {
        var tray = FindWindowW("yolime-tray", "yolime");
        if (tray == 0) return "no tray window";
        PostMessageW(tray, 0x0111, command, 0);
        return null;
    }

    private static JsonElement? Setting(string name)
    {
        try
        {
            using var stream = new FileStream(SettingsJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty(name, out var v) ? v.Clone() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static bool? SettingBool(string name) => Setting(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;

    private static string? SettingString(string name) => Setting(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static int? SettingInt(string name) => Setting(name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}

/// <summary>Reads a generic credential the way yolime writes it: a UTF-8 blob.</summary>
internal static class CredentialManager
{
    public static unsafe string? Read(string target)
    {
        if (!CredReadW(target, 1, 0, out var cred)) return null;
        try
        {
            var c = (Credential*)cred;
            return c->BlobSize == 0 ? "" : System.Text.Encoding.UTF8.GetString(c->Blob, (int)c->BlobSize);
        }
        finally
        {
            CredFree(cred);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint BlobSize;
        public byte* Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, uint type, uint flags, out nint cred);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);
}
