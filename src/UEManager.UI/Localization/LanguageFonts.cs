using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using UEManager.Core.Storage;

namespace UEManager.UI;

/// <summary>A font shown in Settings: its saved name (empty for the theme default, "$system" for the Windows font) and its display name.</summary>
public sealed class FontOption : INotifyPropertyChanged
{
    private readonly Func<string> name;

    public FontOption(string code, string name) : this(code, () => name)
    {
    }

    internal FontOption(string code, Func<string> name)
    {
        Code = code;
        this.name = name;
    }

    public string Code { get; }

    public string Name => name();

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));

    public override string ToString() => Name;
}

/// <summary>
/// The East Asian fallback and user font choices for the interface. Palette.xaml names Microsoft YaHei UI;
/// Traditional Chinese gets Microsoft JhengHei UI, Japanese Yu Gothic UI, and Korean Malgun Gothic.
/// A chosen font (or the Windows font) leads the font list while keeping the language's fallback; Font.Scale
/// scales the UI from the base font size.
/// </summary>
internal static class LanguageFonts
{
    public const string Default = "";
    public const string System = "$system";

    private const string Simplified = "Microsoft YaHei UI";
    private const string Traditional = "Microsoft JhengHei UI";

    /// <summary>The fonts with an East Asian fallback; DynamicResource lets views follow a language or font switch.</summary>
    private static readonly string[] Keys = ["Font.Body", "Font.Heading", "Font.Label"];

    private static readonly Lazy<IReadOnlyList<FontFamily>> SystemFontFamilies = new(() =>
        Fonts.SystemFontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f.Source))
            .DistinctBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static event EventHandler? FontChanged;
    private static event EventHandler? ScaleChanged;

    public static string? Font { get; private set; }

    public static int FontSize { get; private set; } = AppSettings.DefaultFontSize;

    public static void SetFont(string? font)
    {
        var normalized = string.IsNullOrWhiteSpace(font) ? null : font.Trim();
        if (string.Equals(Font, normalized, StringComparison.Ordinal))
            return;
        Font = normalized;
        FontChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SetFontSize(int size)
    {
        var normalized = AppSettings.NormalizeFontSize(size);
        if (FontSize == normalized)
            return;
        FontSize = normalized;
        ScaleChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Sets the fonts and scale for the language and user choices now, and again whenever any of them changes.</summary>
    public static void Follow(ResourceDictionary resources)
    {
        var fonts = Keys.ToDictionary(key => key, key => (FontFamily)resources[key]);
        void ApplyFonts()
        {
            foreach (var (key, font) in fonts)
                resources[key] = For(font, L.Current.Language, Font);
        }
        void ApplyScale() => resources["Font.Scale"] = ScaleFor(FontSize);

        ApplyFonts();
        ApplyScale();
        L.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Localizer.Language))
                ApplyFonts();
        };
        FontChanged += (_, _) => ApplyFonts();
        ScaleChanged += (_, _) => ApplyScale();
    }

    /// <summary>Replaces YaHei with the Windows fallback for Traditional Chinese, Japanese or Korean, or puts a chosen font first.</summary>
    internal static FontFamily For(FontFamily font, string language, string? choice = null)
    {
        if (!font.Source.Contains(Simplified, StringComparison.OrdinalIgnoreCase))
            return font;
        var fallback = FallbackFor(language);
        if (string.IsNullOrWhiteSpace(choice))
        {
            return fallback != Simplified
                ? new FontFamily(font.BaseUri, font.Source.Replace(Simplified, fallback, StringComparison.OrdinalIgnoreCase))
                : font;
        }
        var chosen = choice.Equals(System, StringComparison.OrdinalIgnoreCase)
            ? SystemFonts.MessageFontFamily.Source
            : choice.Trim();
        return new FontFamily(chosen.Contains(fallback, StringComparison.OrdinalIgnoreCase) ? chosen : $"{chosen}, {fallback}");
    }

    internal static ScaleTransform ScaleFor(int size)
    {
        var scale = AppSettings.NormalizeFontSize(size) / (double)AppSettings.DefaultFontSize;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        return transform;
    }

    public static IReadOnlyList<FontOption> Options() =>
    [
        new FontOption(Default, () => L.T("Theme default")),
        new FontOption(System, () => L.T("System font")),
        .. SystemFontFamilies.Value.Select(f => new FontOption(f.Source, () => DisplayName(f, CultureInfo.GetCultureInfo(L.Current.Language)))),
    ];

    public static string Resolve(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return Default;
        var trimmed = choice.Trim();
        if (trimmed.Equals(System, StringComparison.OrdinalIgnoreCase))
            return System;
        return SystemFontFamilies.Value.FirstOrDefault(f => f.Source.Equals(trimmed, StringComparison.OrdinalIgnoreCase))?.Source ?? Default;
    }

    private static string FallbackFor(string language) =>
        IsTraditionalChinese(language) ? Traditional
            : CultureInfo.GetCultureInfo(language).TwoLetterISOLanguageName switch
            {
                "ja" => "Yu Gothic UI",
                "ko" => "Malgun Gothic",
                _ => Simplified,
            };

    private static string DisplayName(FontFamily family, CultureInfo culture)
    {
        var names = family.FamilyNames;
        if (TryName(names, culture.IetfLanguageTag) is { } exact)
            return exact;
        try
        {
            var specific = CultureInfo.CreateSpecificCulture(culture.Name).IetfLanguageTag;
            if (TryName(names, specific) is { } regional)
                return regional;
        }
        catch (CultureNotFoundException)
        {
        }
        var prefix = culture.TwoLetterISOLanguageName + "-";
        foreach (var entry in names)
        {
            if (entry.Key.IetfLanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }
        return TryName(names, "en-us") ?? family.Source;
    }

    private static string? TryName(LanguageSpecificStringDictionary names, string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;
        try
        {
            return names.TryGetValue(XmlLanguage.GetLanguage(tag), out var name) ? name : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>zh-TW, and any other language written in Traditional characters, such as zh-HK.</summary>
    private static bool IsTraditionalChinese(string language)
    {
        for (var culture = CultureInfo.GetCultureInfo(language); culture.Name.Length > 0; culture = culture.Parent)
        {
            if (culture.Name.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
