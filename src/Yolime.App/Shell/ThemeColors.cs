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
        var ui = new UISettings();
        var queue = element.DispatcherQueue;
        Windows.Foundation.TypedEventHandler<UISettings, object> changed = (_, _) => queue.TryEnqueue(() => apply());
        ui.ColorValuesChanged += changed;
        var access = new AccessibilitySettings();
        Windows.Foundation.TypedEventHandler<AccessibilitySettings, object> hc = (_, _) => queue.TryEnqueue(() => apply());
        access.HighContrastChanged += hc;
        element.Unloaded += (_, _) =>
        {
            ui.ColorValuesChanged -= changed;
            access.HighContrastChanged -= hc;
            lock (Keep) { Keep.Remove(ui); Keep.Remove(access); }
        };
        lock (Keep) { Keep.Add(ui); Keep.Add(access); }
    }

    // These raise their events only while the objects are alive.
    private static readonly List<object> Keep = [];
}
