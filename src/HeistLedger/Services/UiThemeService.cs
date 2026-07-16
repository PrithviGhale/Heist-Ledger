using System.Windows;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;

namespace GTAEarningsTracker.Services;

public sealed class UiThemeService
{
    public static IReadOnlyList<string> ThemeNames { get; } =
        ["Dark", "Light", "Purple", "Galaxy", "Midnight", "Crimson"];

    private static readonly IReadOnlyDictionary<string, ThemePalette> Palettes =
        new Dictionary<string, ThemePalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dark"] = new(
                "#090D12", "#0E141C", "#151D27", "#1A2531", "#24313F", "#1C2733",
                "#F4F7FA", "#94A3B4", "#667587", "#A6FF5F", "#6FCB34", "#4DD7FF",
                "#FFC857", "#FF5C6C", "#090D12", "#101822", "#18232B", "#121B24"),
            ["Light"] = new(
                "#F4F7FB", "#FFFFFF", "#E8EEF5", "#DEE7F1", "#C6D1DE", "#D8E0EA",
                "#172033", "#526174", "#7B8999", "#2AA866", "#18814A", "#087EA4",
                "#A86810", "#D43D51", "#F8FAFD", "#E8EEF6", "#FFFFFF", "#EAF3EE"),
            ["Purple"] = new(
                "#0E0918", "#171024", "#21162F", "#2B1D3D", "#45315B", "#30223F",
                "#FAF6FF", "#B9A7CA", "#806F92", "#C98CFF", "#9B5DE5", "#70D7FF",
                "#FFD166", "#FF6685", "#0E0918", "#1A0E2B", "#2A193C", "#171022"),
            ["Galaxy"] = new(
                "#070818", "#0C1027", "#141A38", "#1B2348", "#29335E", "#20294D",
                "#F4F6FF", "#A9B3D5", "#6C789F", "#78E4FF", "#4AB7E8", "#B48CFF",
                "#FFD166", "#FF6482", "#08091B", "#17113A", "#211A4A", "#0E1731"),
            ["Midnight"] = new(
                "#06101C", "#0B1827", "#102236", "#17304A", "#27445F", "#1B344C",
                "#F1F7FC", "#9FB4C7", "#617A91", "#4CC9F0", "#2498C1", "#66E1C4",
                "#FFC857", "#FF6277", "#06101C", "#0D2237", "#13314A", "#0B1B2B"),
            ["Crimson"] = new(
                "#13090D", "#1D0E14", "#29151D", "#361C27", "#51303B", "#3B222C",
                "#FFF5F7", "#C9A5AE", "#8D6873", "#FF718A", "#D94763", "#65D8FF",
                "#FFD166", "#FF526F", "#13090D", "#271018", "#351A23", "#1C0E13")
        };

    public string Apply(string? requestedTheme)
    {
        var themeName = ThemeNames.FirstOrDefault(name =>
            string.Equals(name, requestedTheme, StringComparison.OrdinalIgnoreCase)) ?? "Dark";
        var palette = Palettes[themeName];
        var resources = WpfApplication.Current.Resources;

        SetColor(resources, "MainBackgroundColor", palette.Main);
        SetColor(resources, "SecondaryBackgroundColor", palette.Secondary);
        SetColor(resources, "ElevatedCardColor", palette.Card);
        SetColor(resources, "ElevatedCardHoverColor", palette.CardHover);
        SetColor(resources, "BorderColor", palette.Border);
        SetColor(resources, "MutedBorderColor", palette.MutedBorder);
        SetColor(resources, "PrimaryTextColor", palette.PrimaryText);
        SetColor(resources, "SecondaryTextColor", palette.SecondaryText);
        SetColor(resources, "MutedTextColor", palette.MutedText);
        SetColor(resources, "EarningsGreenColor", palette.Accent);
        SetColor(resources, "DarkGreenColor", palette.AccentDark);
        SetColor(resources, "ScannerCyanColor", palette.Cyan);
        SetColor(resources, "WarningGoldColor", palette.Warning);
        SetColor(resources, "ErrorRedColor", palette.Error);

        SetBrush(resources, "MainBackgroundBrush", palette.Main);
        SetBrush(resources, "SecondaryBackgroundBrush", palette.Secondary);
        SetBrush(resources, "ElevatedCardBrush", palette.Card);
        SetBrush(resources, "ElevatedCardHoverBrush", palette.CardHover);
        SetBrush(resources, "BorderBrush", palette.Border);
        SetBrush(resources, "MutedBorderBrush", palette.MutedBorder);
        SetBrush(resources, "PrimaryTextBrush", palette.PrimaryText);
        SetBrush(resources, "SecondaryTextBrush", palette.SecondaryText);
        SetBrush(resources, "MutedTextBrush", palette.MutedText);
        SetBrush(resources, "EarningsGreenBrush", palette.Accent);
        SetBrush(resources, "DarkGreenBrush", palette.AccentDark);
        SetBrush(resources, "ScannerCyanBrush", palette.Cyan);
        SetBrush(resources, "WarningGoldBrush", palette.Warning);
        SetBrush(resources, "ErrorRedBrush", palette.Error);
        SetBrush(resources, "AccentSurfaceBrush", WithAlpha(palette.Accent, 0x24));
        SetBrush(resources, "AccentBorderBrush", WithAlpha(palette.Accent, 0x66));
        SetBrush(resources, "PreviewBackgroundBrush", palette.Main);
        resources["DashboardBackgroundBrush"] = Gradient(palette.DashboardStart, palette.DashboardEnd);
        resources["HeroCardBrush"] = Gradient(palette.HeroStart, palette.HeroEnd, diagonal: true);
        return themeName;
    }

    private static void SetColor(ResourceDictionary resources, string key, string value)
    {
        resources[key] = (WpfColor)WpfColorConverter.ConvertFromString(value);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string value)
    {
        var brush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(value));
        brush.Freeze();
        resources[key] = brush;
    }

    private static string WithAlpha(string rgb, byte alpha)
    {
        return $"#{alpha:X2}{rgb.TrimStart('#')}";
    }

    private static LinearGradientBrush Gradient(string start, string end, bool diagonal = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = diagonal ? new WpfPoint(1, 1) : new WpfPoint(0, 1)
        };
        brush.GradientStops.Add(new GradientStop((WpfColor)WpfColorConverter.ConvertFromString(start), 0));
        brush.GradientStops.Add(new GradientStop((WpfColor)WpfColorConverter.ConvertFromString(end), 1));
        brush.Freeze();
        return brush;
    }

    private sealed record ThemePalette(
        string Main,
        string Secondary,
        string Card,
        string CardHover,
        string Border,
        string MutedBorder,
        string PrimaryText,
        string SecondaryText,
        string MutedText,
        string Accent,
        string AccentDark,
        string Cyan,
        string Warning,
        string Error,
        string DashboardStart,
        string DashboardEnd,
        string HeroStart,
        string HeroEnd);
}
