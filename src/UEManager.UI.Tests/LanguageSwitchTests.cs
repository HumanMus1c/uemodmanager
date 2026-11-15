using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UEManager.Core.Games;
using UEManager.Core.Mods;
using UEManager.Core.Storage;
using UEManager.UI.Services;
using UEManager.UI.ViewModels;
using Xunit;
using UEManager.UI.Catalog;

namespace UEManager.UI.Tests;

[Collection("Language")]
public class LanguageSwitchTests
{
    [Fact]
    public void Existing_window_switches_languages_without_losing_state_and_cached_errors_follow_it()
    {
        Exception? failure = null;
        var stage = "starting the STA thread";
        var thread = new Thread(() =>
        {
            try { VerifySwitch(value => stage = value); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"The WPF language check did not finish: {stage}.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifySwitch(Action<string> stage)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var originalLanguage = Localizer.Current.Language;
        var originalFont = LanguageFonts.Font;
        var originalFontSize = LanguageFonts.FontSize;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefault = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        var folder = Path.Combine(Path.GetTempPath(), "uemm-ui-test-" + Guid.NewGuid().ToString("N"));
        // Use an inert WPF host: constructing the real App queues its production startup on the dispatcher.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow? window = null;
        try
        {
            stage("loading application resources");
            foreach (var resource in new[] { "Theme/Palette.xaml", "Theme/Controls.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/UEModManager;component/{resource}", UriKind.Relative),
                });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/UEModManager;component/Catalog/CatalogResources.xaml", UriKind.Relative),
            });
            Localizer.Current.SetLanguage("en");
            var paths = new AppPaths(folder);
            paths.EnsureWritable();
            stage("creating the view model");
            var model = new MainViewModel(new GameProfile { Id = "test", DisplayName = "Phantom Blade Zero" }, paths,
                new ArtSet(null, null));
            model.SelectedPage = "Settings";
            model.SearchText = "蓝图";
            var row = new ModItemViewModel(model, new ModRecord
            {
                Id = "blueprint",
                Name = "User mod {0} 中文",
                Files = [new ModFileRecord { Target = DeployTarget.LogicMods, FileName = "User.pak" }],
            }, Path.Combine(folder, "library"));
            model.Mods.Add(row);
            model.ModsView.Refresh();
            Assert.True(model.ModsView.IsEmpty);

            stage("constructing the window");
            window = new MainWindow { DataContext = model };
            FlushBindings();
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            stage("laying out the window");
            surface.Measure(new Size(1360, 800));
            surface.Arrange(new Rect(0, 0, 1360, 800));
            FlushBindings();
            var selectors = Descendants(surface).OfType<ComboBox>().ToList();
            Assert.Equal(2, selectors.Count);
            var selector = selectors.Single(c => ReferenceEquals(c.ItemsSource, model.Languages));
            var fontSelector = selectors.Single(c => ReferenceEquals(c.ItemsSource, model.Fonts));
            Assert.Equal("en", selector.SelectedValue);
            Assert.Equal(LanguageFonts.Default, fontSelector.SelectedValue);
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "Settings");

            // The control changes the existing model and saves the choice, without replacing the window or page.
            stage("switching to Chinese");
            selector.SelectedValue = "zh-CN";
            FlushBindings();
            Assert.Same(model, window.DataContext);
            Assert.Equal("Settings", model.SelectedPage);
            Assert.Equal("蓝图", model.SearchText);
            Assert.Equal("zh-CN", AppSettings.Load(paths).Language);
            Assert.Equal("主题默认", Assert.IsType<FontOption>(fontSelector.SelectedItem).Name);
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "设置");
            Assert.Equal("zh-CN", window.Language.IetfLanguageTag, ignoreCase: true);
            Assert.Equal("影之刃零", model.GameDisplayName);
            Assert.Equal("User mod {0} 中文", row.Name);
            Assert.Equal("蓝图", row.KindText);
            Assert.Single(model.ModsView.Cast<ModItemViewModel>());
            Assert.True(row.Matches("Blueprint"));

            using var http = new HttpClient(new MissingHandler());
            var update = new AppUpdateViewModel(model, http, paths, ["https://example.invalid/latest"], AppInfo.ReleaseFileName);
            stage("checking an unavailable release");
            WaitFor(update.CheckAsync());
            Assert.Equal("尚无发布版本。", update.StatusHint);
            var catalog = new CatalogViewModel(model, http, "https://example.invalid/catalog", null, true,
                CatalogSortField.Name, false);
            stage("loading an unavailable catalog");
            WaitFor(catalog.LoadAsync());
            Assert.StartsWith("测试列表：", catalog.TestCatalogText);
            Assert.StartsWith("无法加载 openmods 列表：", catalog.Error);
            stage("switching back to English");
            selector.SelectedValue = "en";
            FlushBindings();
            Assert.Equal("en", AppSettings.Load(paths).Language);
            Assert.Equal("Settings", model.SelectedPage);
            Assert.Equal("蓝图", model.SearchText);
            Assert.True(model.ModsView.IsEmpty);
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "Settings");
            Assert.Equal("No release is published yet.", update.StatusHint);
            Assert.StartsWith("Test list:", catalog.TestCatalogText);
            Assert.StartsWith("The openmods list could not be loaded:", catalog.Error);

            stage("checking the font size slider");
            VerifyFontSizeSlider(app, surface, model, paths);

            stage("checking dependency badges and filter pills");
            model.SelectedPage = "Mods";
            model.SearchText = "";
            model.Mods.Add(new ModItemViewModel(model, new ModRecord
            {
                Id = "dep-loader",
                Name = "PBZModLoader",
                Enabled = false,
                SourceFileName = "PBZModLoader-2.1.0.zip",
                Files = [new ModFileRecord { Target = DeployTarget.ExeFolder, FileName = "version.dll" }],
            }, folder));
            model.Mods.Add(new ModItemViewModel(model, new ModRecord
            {
                Id = "dep-lua",
                Name = "Sample Lua",
                Enabled = true,
                Files = [new ModFileRecord { Target = DeployTarget.Ue4ssMods, FileName = "Sample/Scripts/main.lua" }],
            }, folder));
            model.Mods.Add(new ModItemViewModel(model, new ModRecord
            {
                Id = "dep-pak",
                Name = "Sample PAK",
                Enabled = true,
                Files = [new ModFileRecord { Target = DeployTarget.Mods, FileName = "Sample_P.pak" }],
            }, folder));
            model.RefreshTagFilters();
            model.ModDetails = new ModDetailsViewModel(model, model.Mods.Single(m => m.Id == "dep-lua").Record, [], [], folder);
            FlushBindings();
            surface.Measure(new Size(1360, 800));
            surface.Arrange(new Rect(0, 0, 1360, 800));
            surface.UpdateLayout();
            FlushBindings();

            Assert.Contains(Descendants(surface).OfType<Views.DependencyBadges>(), view => view.Visibility == Visibility.Visible);
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "Prerequisite loader");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "No prerequisite");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "UE4SS · missing");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == Format.Tracked("Prerequisites"));

            selector.SelectedValue = "zh-CN";
            FlushBindings();
            surface.Measure(new Size(1360, 800));
            surface.Arrange(new Rect(0, 0, 1360, 800));
            surface.UpdateLayout();
            FlushBindings();
            Assert.Contains(model.DependencyFilters, f => f.Label == "缺少前置（2）");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "前置加载器");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "无需前置");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "UE4SS · 未安装");
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == Format.Tracked("前置依赖"));
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "缺少前置（2）");

            selector.SelectedValue = "en";
            FlushBindings();
            model.ModDetails = null;
            foreach (var added in model.Mods.Where(m => m.Id.StartsWith("dep-", StringComparison.Ordinal)).ToList())
                model.Mods.Remove(added);
            model.RefreshTagFilters();
        }
        finally
        {
            stage("closing the window");
            window?.Close();
            Localizer.Current.SetLanguage(originalLanguage);
            LanguageFonts.SetFont(originalFont);
            LanguageFonts.SetFontSize(originalFontSize);
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = originalDefault;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUi;
            stage("shutting down the test application");
            app.Shutdown();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void VerifyFontSizeSlider(Application app, FrameworkElement surface, MainViewModel model, AppPaths paths)
    {
        LanguageFonts.SetFontSize(model.FontSize);
        LanguageFonts.Follow(app.Resources);
        var slider = Descendants(surface).OfType<Slider>().Single(s => s.Name == "FontSizeSlider");
        var label = Descendants(VisualTreeHelper.GetParent(slider)).OfType<TextBlock>().Single();
        Assert.Equal(13, slider.Value);
        Assert.Equal("13", label.Text);

        // Dragging previews the numeric value without scaling the UI or writing settings until DragCompleted.
        slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        slider.Value = 18;
        FlushBindings();
        Assert.Equal("18", label.Text);
        Assert.Equal(13, model.FontSize);
        Assert.Equal(13, AppSettings.Load(paths).FontSize);
        Assert.Equal(1.0, Assert.IsType<ScaleTransform>(app.Resources["Font.Scale"]).ScaleX, precision: 6);

        slider.Value = 15;
        FlushBindings();
        Assert.Equal("15", label.Text);
        Assert.Equal(13, model.FontSize);

        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: false) { RoutedEvent = Thumb.DragCompletedEvent });
        FlushBindings();
        Assert.Equal(15, model.FontSize);
        Assert.Equal(15, AppSettings.Load(paths).FontSize);
        Assert.Equal(15.0 / 13.0, Assert.IsType<ScaleTransform>(app.Resources["Font.Scale"]).ScaleX, precision: 6);

        // Pressing on the track also waits until the pointer is released.
        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        slider.Value = 17;
        FlushBindings();
        Assert.Equal("17", label.Text);
        Assert.Equal(15, model.FontSize);
        slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
        });
        FlushBindings();
        Assert.Equal(17, model.FontSize);
        Assert.Equal(17, AppSettings.Load(paths).FontSize);

        // Non-pointer changes (such as keyboard navigation) still apply immediately.
        slider.Value = 12;
        FlushBindings();
        Assert.Equal(12, model.FontSize);
        Assert.Equal(12, AppSettings.Load(paths).FontSize);
        Assert.Equal(12.0 / 13.0, Assert.IsType<ScaleTransform>(app.Resources["Font.Scale"]).ScaleX, precision: 6);

        // Binding Minimum=11 before Value=16 on a fresh window must not overwrite the saved FontSize.
        var savedModel = new MainViewModel(new GameProfile { Id = "test", DisplayName = "Phantom Blade Zero" }, paths,
            new ArtSet(null, null)) { SelectedPage = "Settings" };
        savedModel.FontSize = 16;
        var secondWindow = new MainWindow { DataContext = savedModel };
        try
        {
            FlushBindings();
            var secondSurface = Assert.IsAssignableFrom<FrameworkElement>(secondWindow.Content);
            secondSurface.Measure(new Size(1360, 800));
            secondSurface.Arrange(new Rect(0, 0, 1360, 800));
            FlushBindings();
            var secondSlider = Descendants(secondSurface).OfType<Slider>().Single(s => s.Name == "FontSizeSlider");
            var secondLabel = Descendants(VisualTreeHelper.GetParent(secondSlider)).OfType<TextBlock>().Single();
            Assert.Equal(16, secondSlider.Value);
            Assert.Equal(16, savedModel.FontSize);
            Assert.Equal("16", secondLabel.Text);
        }
        finally
        {
            secondWindow.Close();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void WaitFor(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class MissingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
    }
}
