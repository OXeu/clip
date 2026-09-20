using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Clip.Desktop.Design;

/// <summary>Peace 的中性表面与选中态，搭配 Clip 标志的玫瑰色。</summary>
internal static class UiTheme
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("ColorNeutralBackground1", "#FFFFFF", "#161616"),
        ("ColorNeutralBackground2", "#F9FAFB", "#111111"),
        ("ColorNeutralBackground3", "#FFFFFF", "#0A0A0A"),
        ("ColorNeutralForeground1", "#171717", "#EDEDED"),
        ("ColorNeutralForeground2", "#4B5563", "#C4C4C4"),
        ("ColorNeutralForeground3", "#5F6773", "#A0A0A0"),
        ("ColorDisabledForeground", "#9298A1", "#757575"),
        ("ColorNeutralStroke1", "#D1D5DB", "#4A4A4A"),
        ("ColorNeutralStroke2", "#E5E7EB", "#2E2E2E"),
        ("ColorSubtleStroke", "#F0F1F3", "#242424"),
        ("ColorSelectionBackground", "#EDEDED", "#303030"),
        ("ColorSelectionForeground", "#171717", "#EDEDED"),
        ("ColorBrandBackground", "#F79FB9", "#F79FB9"),
        ("ColorBrandBackgroundHover", "#F9B4C9", "#F9B4C9"),
        ("ColorBrandBackgroundPressed", "#ED8DAA", "#ED8DAA"),
        ("ColorBrandBackground2", "#FCEEF3", "#302029"),
        ("ColorBrandForeground", "#A1325A", "#F79FB9"),
        ("ColorBrandStroke", "#D66B94", "#D66B94"),
        ("ColorOnBrand", "#FFFFFF", "#FFFFFF"),
        ("ColorDangerForeground", "#B4233C", "#FDA4AF"),
        ("ColorDangerBackground", "#FFF1F2", "#32191E"),
        ("ColorPreviewBackground", "#171717", "#080808"),
        ("ColorPreviewForeground", "#FFFFFF", "#EDEDED"),
        ("ColorTimelineClipBackground", "#F7F1F4", "#262126"),
        ("ColorTimelineClipBorder", "#E5D8DF", "#45373F"),
        ("ColorTimelineClipSelected", "#E4E4E7", "#3F3F43"),
        ("ColorTimelineClipSelectedBorder", "#71717A", "#A1A1AA"),
        ("ColorPlayhead", "#AC3863", "#F2A0BD"),
        ("ColorScrollbar", "#D1D5DB", "#4A4A4A")
    ];

    internal static event Action? Changed;
    internal static bool IsDark { get; private set; }

    internal static void Initialize()
    {
        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        SystemParameters.StaticPropertyChanged += ParametersChanged;
        Application.Current.Exit += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            SystemParameters.StaticPropertyChanged -= ParametersChanged;
        };
    }

    private static void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => QueueRefresh();
    private static void ParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) QueueRefresh();
    }

    private static void QueueRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            _ = dispatcher.InvokeAsync(ApplySystemTheme);
    }

    internal static void ApplySystemTheme()
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            StartupDiagnostics.Write("System theme unavailable; using light theme.", error);
        }
        Apply(dark);
    }

    internal static void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        // .NET 10 将代码入口标记为实验性；与 App.xaml 已使用的 ThemeMode 是同一 API。
#pragma warning disable WPF0001
        app.ThemeMode = SystemParameters.HighContrast ? ThemeMode.System : dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (var (key, light, night) in Palette)
        {
            var color = SystemParameters.HighContrast ? ContrastColor(key) : (Color)ColorConverter.ConvertFromString(dark ? night : light);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }
        // 原生 Fluent 模板的交互状态与应用共享同一组语义色。
        Alias("ColorNeutralBackground1", "ButtonBackground", "ComboBoxBackground", "TextControlBackground", "TextControlBackgroundFocused",
            "ContextMenuBackground", "FlyoutBackground", "ComboBoxDropDownBackground", "ExpanderContentBackground", "ToolTipBackground");
        Alias("ColorNeutralBackground2", "ButtonBackgroundPointerOver", "ButtonBackgroundDisabled", "ComboBoxBackgroundPointerOver",
            "ComboBoxBackgroundDisabled", "TextControlBackgroundPointerOver", "TextControlBackgroundDisabled", "ExpanderHeaderBackground");
        Alias("ColorSelectionBackground", "ButtonBackgroundPressed", "ComboBoxBackgroundPressed", "MenuBarItemBackgroundSelected",
            "MenuBarItemBackgroundPointerOver", "ComboBoxItemBackgroundSelected");
        Alias("ColorNeutralForeground1", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ComboBoxForeground",
            "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused", "ComboBoxItemForeground",
            "ComboBoxItemForegroundSelected", "ContextMenuForeground", "TextControlForeground", "TextControlForegroundPointerOver",
            "TextControlForegroundFocused", "ExpanderHeaderForeground", "ToolTipForeground");
        Alias("ColorDisabledForeground", "ButtonForegroundDisabled", "AccentButtonForegroundDisabled", "ComboBoxForegroundDisabled",
            "TextControlForegroundDisabled", "ExpanderHeaderDisabledForeground", "TextFillColorDisabledBrush");
        app.Resources["TextFillColorDisabled"] = ((SolidColorBrush)app.Resources["ColorDisabledForeground"]).Color;
        Alias("ColorNeutralStroke2", "ButtonBorderBrush", "ButtonBorderBrushDisabled", "ComboBoxBorderBrush", "ComboBoxBorderBrushDisabled",
            "TextControlBorderBrush", "TextControlElevationBorderBrush", "TextControlBorderBrushDisabled", "ContextMenuBorderBrush",
            "FlyoutBorderBrush", "ComboBoxDropDownBorderBrush", "ExpanderHeaderBorderBrush", "MenuBarItemBorderBrush", "ToolTipBorderBrush", "ProgressBarBackground");
        Alias("ColorNeutralStroke1", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ComboBoxBorderBrushPointerOver",
            "ComboBoxBorderBrushPressed", "TextControlBorderBrushPointerOver", "ExpanderHeaderBorderPointerOverBrush");
        Alias("ColorBrandBackground", "AccentButtonBackground", "AccentButtonBorderBrush", "TextControlBorderBrushFocused",
            "TextControlFocusedBorderBrush", "TextControlElevationBorderFocusedBrush", "ComboBoxBorderBrushFocused", "ProgressBarForeground");
        Alias("ColorBrandBackgroundHover", "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver");
        Alias("ColorBrandBackgroundPressed", "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed");
        Alias("ColorSelectionBackground", "AccentButtonBackgroundDisabled", "AccentButtonBorderBrushDisabled", "TextControlSelectionHighlightColor");
        Alias("ColorOnBrand", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        Alias("ColorScrollbar", "ScrollBarThumbFill", "ScrollBarButtonArrowForeground");
        Changed?.Invoke();
    }

    private static void Alias(string key, params string[] targets)
    {
        foreach (var target in targets) Application.Current.Resources[target] = Application.Current.Resources[key];
    }

    private static Color ContrastColor(string key) => key switch
    {
        "ColorOnBrand" or "ColorSelectionForeground" => SystemColors.HighlightTextColor,
        "ColorBrandBackground" or "ColorBrandBackgroundHover" or "ColorBrandBackgroundPressed" or
            "ColorSelectionBackground" or "ColorTimelineClipSelected" => SystemColors.HighlightColor,
        "ColorDisabledForeground" => SystemColors.GrayTextColor,
        _ when key.Contains("Background", StringComparison.Ordinal) => SystemColors.WindowColor,
        _ => SystemColors.WindowTextColor
    };
}
