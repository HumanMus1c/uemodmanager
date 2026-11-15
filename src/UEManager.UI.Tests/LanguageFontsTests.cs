using System.Windows;
using System.Windows.Media;
using Xunit;

namespace UEManager.UI.Tests;

[Collection("Language")]
public class LanguageFontsTests
{
    [Theory]
    [InlineData("zh-TW", "Segoe UI, Microsoft JhengHei UI")]
    [InlineData("zh-HK", "Segoe UI, Microsoft JhengHei UI")]
    [InlineData("zh-CN", "Segoe UI, Microsoft YaHei UI")]
    [InlineData("vi", "Segoe UI, Microsoft YaHei UI")]
    [InlineData("en", "Segoe UI, Microsoft YaHei UI")]
    [InlineData("ja", "Segoe UI, Yu Gothic UI")]
    [InlineData("ja-JP", "Segoe UI, Yu Gothic UI")]
    [InlineData("ko", "Segoe UI, Malgun Gothic")]
    [InlineData("ko-KR", "Segoe UI, Malgun Gothic")]
    public void Each_language_gets_its_font(string language, string expected) =>
        Assert.Equal(expected, LanguageFonts.For(new FontFamily("Segoe UI, Microsoft YaHei UI"), language).Source);

    [Fact]
    public void A_font_without_a_Chinese_font_stays_as_it_is()
    {
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        Assert.Same(icons, LanguageFonts.For(icons, "zh-TW"));
        Assert.Same(icons, LanguageFonts.For(icons, "zh-TW", "Consolas"));
    }

    [Theory]
    [InlineData("zh-CN", "Consolas, Microsoft YaHei UI")]
    [InlineData("zh-TW", "Consolas, Microsoft JhengHei UI")]
    [InlineData("ja", "Consolas, Yu Gothic UI")]
    [InlineData("ko", "Consolas, Malgun Gothic")]
    public void A_custom_font_leads_the_language_fallback(string language, string expected) =>
        Assert.Equal(expected, LanguageFonts.For(new FontFamily("Segoe UI, Microsoft YaHei UI"), language, "Consolas").Source);

    [Fact]
    public void Default_choice_uses_the_theme_font_and_system_choice_uses_the_windows_font()
    {
        var theme = new FontFamily("Palatino Linotype, Georgia, Microsoft YaHei UI");
        Assert.Equal("Palatino Linotype, Georgia, Microsoft JhengHei UI",
            LanguageFonts.For(theme, "zh-TW", LanguageFonts.Default).Source);

        var systemFont = SystemFonts.MessageFontFamily.Source;
        var expected = systemFont.Contains("Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)
            ? systemFont
            : $"{systemFont}, Microsoft YaHei UI";
        Assert.Equal(expected, LanguageFonts.For(theme, "zh-CN", LanguageFonts.System).Source);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(11, 11.0 / 13.0)]
    [InlineData(13, 1.0)]
    [InlineData(18, 18.0 / 13.0)]
    [InlineData(30, 18.0 / 13.0)]
    public void Font_scale_matches_the_normalized_base_font_size(int size, double expected)
    {
        var scale = LanguageFonts.ScaleFor(size);
        Assert.Equal(expected, scale.ScaleX, precision: 6);
        Assert.Equal(expected, scale.ScaleY, precision: 6);
    }

    [Fact]
    public void Follow_updates_scale_without_replacing_fonts_when_only_font_size_changes()
    {
        var originalFont = LanguageFonts.Font;
        var originalSize = LanguageFonts.FontSize;
        try
        {
            LanguageFonts.SetFont(null);
            LanguageFonts.SetFontSize(13);
            var resources = new ResourceDictionary
            {
                ["Font.Body"] = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                ["Font.Heading"] = new FontFamily("Palatino Linotype, Georgia, Microsoft YaHei UI"),
                ["Font.Label"] = new FontFamily("Bahnschrift, Segoe UI, Microsoft YaHei UI"),
                ["Font.Scale"] = new ScaleTransform(1, 1),
            };
            LanguageFonts.Follow(resources);

            var body = resources["Font.Body"];
            var heading = resources["Font.Heading"];
            var label = resources["Font.Label"];

            LanguageFonts.SetFontSize(16);

            Assert.Same(body, resources["Font.Body"]);
            Assert.Same(heading, resources["Font.Heading"]);
            Assert.Same(label, resources["Font.Label"]);
            var scale = Assert.IsType<ScaleTransform>(resources["Font.Scale"]);
            Assert.Equal(16.0 / 13.0, scale.ScaleX, precision: 6);
            Assert.Equal(16.0 / 13.0, scale.ScaleY, precision: 6);
        }
        finally
        {
            LanguageFonts.SetFont(originalFont);
            LanguageFonts.SetFontSize(originalSize);
        }
    }
}
