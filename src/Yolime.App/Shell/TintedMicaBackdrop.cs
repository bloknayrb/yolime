using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Yolime.Core.Design;

namespace Yolime.App.Shell;

/// <summary>
/// Mica with yolime's green tint (<see cref="Palette.WindowTintOpacity"/> of the theme's WindowTint), following the
/// system theme and high contrast. In high contrast it adds no tint and falls back to the system window colour.
/// One instance per window.
/// </summary>
internal sealed class TintedMicaBackdrop : SystemBackdrop
{
    private MicaController? _mica;
    private SystemBackdropConfiguration? _config;
    private readonly UISettings _ui = new();
    private readonly AccessibilitySettings _access = new();
    private Microsoft.UI.Dispatching.DispatcherQueue? _queue;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!MicaController.IsSupported()) return;
        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _config = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _mica = new MicaController();
        Apply();
        _mica.AddSystemBackdropTarget(target);
        _mica.SetSystemBackdropConfiguration(_config);
        _ui.ColorValuesChanged += OnChanged;
        _access.HighContrastChanged += OnChanged;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _ui.ColorValuesChanged -= OnChanged;
        _access.HighContrastChanged -= OnChanged;
        _mica?.RemoveSystemBackdropTarget(target);
        _mica?.Dispose();
        _mica = null;
    }

    private void OnChanged<TSender>(TSender sender, object args) => _queue?.TryEnqueue(Apply);

    private void Apply()
    {
        if (_mica is null) return;
        var hc = _access.HighContrast;
        if (_config is not null) _config.IsHighContrast = hc;
        if (hc)
        {
            var window = _ui.UIElementColor(UIElementType.Window);
            _mica.TintOpacity = 0;
            _mica.LuminosityOpacity = 1;
            _mica.FallbackColor = window;
            _mica.TintColor = window;
            return;
        }
        var bg = _ui.GetColorValue(UIColorType.Background);
        var tokens = Palette.For(bg.R + bg.G + bg.B < 384);
        _mica.TintColor = ThemeColors.Color(tokens.WindowTint);
        _mica.TintOpacity = (float)Palette.WindowTintOpacity;
        _mica.FallbackColor = ThemeColors.Color(tokens.ContentBackground);
    }
}
