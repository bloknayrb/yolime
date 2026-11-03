using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Yolime.Core.Design;

namespace Yolime.App.Shell;

/// <summary>
/// yolime's own title bar. Windows draws a window's standard bar from the system's light/dark mode, while yolime's
/// windows follow the app mode, so a dark window got a white bar (Bryan, 0.3.0). The content is extended under the
/// bar instead: the tinted Mica shows through, with the app icon and the window's title, and the system caption
/// buttons are recoloured to match. Dragging, double-click to maximise, the system menu (Alt+Space, right-click) and
/// snap layouts stay Windows' own.
/// </summary>
internal static class TitleBar
{
    public const double Height = 32;

    // Room for the system's caption buttons over the bar's right end (three 46 DIP buttons). A fixed margin, not
    // AppWindow.TitleBar.RightInset: the inset is in physical pixels, reads 0 before the first show and isn't
    // re-raised on a DPI change, and the titles are short.
    private const double CaptionButtons = 140;

    /// <summary>Puts <paramref name="content"/> under a yolime title bar and makes it the window's content.</summary>
    public static void Install(Window window, UIElement content)
    {
        window.ExtendsContentIntoTitleBar = true;
        var titleBar = window.AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        // The system menu (Alt+Space) and the frame follow the app's mode too, not the system's.
        titleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        var title = new TextBlock
        {
            Text = window.Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            // Like the system's own bar: a fixed 32 DIP row would clip a scaled-up title.
            IsTextScaleFactorEnabled = false,
        };
        var bar = new Grid
        {
            Height = Height,
            // Transparent, not null, so the whole bar takes input as the drag region.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0, 0, CaptionButtons, 0),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yolime.ico");
        var hasIcon = File.Exists(iconPath);
        if (hasIcon)
        {
            // Decoded at 32 px, set before the source so it applies: crisp up to 200%, scaled down below that.
            var source = new BitmapImage { DecodePixelWidth = 32, UriSource = new Uri(iconPath) };
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Source = source };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            bar.Children.Add(icon);
        }
        title.Margin = new Thickness(hasIcon ? 8 : 12, 0, 0, 0);
        Grid.SetColumn(title, 1);
        bar.Children.Add(title);
        // The window's own name already says this; Narrator needn't read the bar too (Raw on each element: a
        // container's Raw doesn't hide its children from the content view).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(title, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        if (content is FrameworkElement fe) Grid.SetRow(fe, 1);
        root.Children.Add(content);
        window.Content = root;
        window.SetTitleBar(bar);

        var active = true;
        void Paint()
        {
            title.Foreground = ThemeColors.Brush(bar, active ? "yolime.Text" : "yolime.Text.Muted");
            if (ThemeColors.HighContrast)
            {
                // Windows' own caption colours in high contrast.
                titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = null;
                titleBar.ButtonForegroundColor = titleBar.ButtonInactiveForegroundColor = null;
                titleBar.ButtonHoverBackgroundColor = titleBar.ButtonPressedBackgroundColor = null;
                titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = null;
                return;
            }
            var tokens = Palette.For(bar.ActualTheme == ElementTheme.Dark);
            var text = ThemeColors.Color(tokens.Text);
            titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = text;
            titleBar.ButtonInactiveForegroundColor = ThemeColors.Color(tokens.TextMuted);
            // Hover and pressed take no transparency, so they're mixed here: the text colour over the window's tint.
            titleBar.ButtonHoverBackgroundColor = ThemeColors.Color(tokens.Text.Over(tokens.WindowTint, 0.08));
            titleBar.ButtonPressedBackgroundColor = ThemeColors.Color(tokens.Text.Over(tokens.WindowTint, 0.14));
        }
        ThemeColors.Track(bar, Paint);
        window.Activated += (_, e) =>
        {
            active = e.WindowActivationState != WindowActivationState.Deactivated;
            Paint();
        };
    }
}
