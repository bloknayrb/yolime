using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Yolime.Core.Design;

namespace Yolime.App.Shell;

/// <summary>
/// Colours for things a XAML Style can't reach (a Run's foreground, a border that changes with state, the meters).
/// A plain <c>Application.Current.Resources[key]</c> lookup is a snapshot of the app theme at that moment; this reads
/// the element's own theme dictionary (HighContrast included), and <see cref="Track"/> re-applies on a theme change.
/// </summary>
internal static class ThemeColors
{
    public static bool HighContrast => new AccessibilitySettings().HighContrast;

    /// <summary>The <c>yolime.*</c> (or WinUI) brush for this element's current theme.</summary>
    public static Brush Brush(FrameworkElement element, string key)
    {
        var name = HighContrast ? "HighContrast" : element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        var themes = Application.Current.Resources.ThemeDictionaries;
        if (themes.TryGetValue(name, out var dict) && dict is ResourceDictionary rd && rd.TryGetValue(key, out var found) && found is Brush brush)
            return brush;
        return (Brush)Application.Current.Resources[key];
    }

    public static Windows.UI.Color Color(Rgb c) => Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);

    /// <summary>Runs <paramref name="apply"/> now and again whenever the element's theme or high contrast changes.</summary>
    public static void Track(FrameworkElement element, Action apply)
    {
        apply();
        element.ActualThemeChanged += (_, _) => apply();
        // High contrast on or off doesn't always change ActualTheme; the system colours event covers it.
        // (AccessibilitySettings.HighContrastChanged can't be used: in an unpackaged app subscribing throws 0x80070490.)
        // Subscribed only while the element is in the tree, so an element built and dropped before it ever loads (a
        // History detail pane rebuilt within one frame) holds nothing, and one that loads again subscribes again.
        UISettings? ui = null;
        Windows.Foundation.TypedEventHandler<UISettings, object>? changed = null;
        element.Loaded += (_, _) =>
        {
            if (ui is not null) return;
            var queue = element.DispatcherQueue;
            ui = new UISettings();
            changed = (_, _) => queue.TryEnqueue(() => apply());
            ui.ColorValuesChanged += changed;
            lock (Keep) Keep.Add(ui);
            // The theme may have changed while it was out of the tree.
            apply();
        };
        element.Unloaded += (_, _) =>
        {
            if (ui is null) return;
            ui.ColorValuesChanged -= changed;
            lock (Keep) Keep.Remove(ui);
            ui = null;
        };
    }

    // UISettings raises its events only while the object is alive.
    private static readonly List<UISettings> Keep = [];
}
