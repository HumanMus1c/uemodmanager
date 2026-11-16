using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
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
            var selector = Descendants(surface).OfType<ComboBox>().Single();
            Assert.Equal("en", selector.SelectedValue);
            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "Settings");

            // The control changes the existing model and saves the choice, without replacing the window or page.
            stage("switching to Chinese");
            selector.SelectedValue = "zh-CN";
            FlushBindings();
            Assert.Same(model, window.DataContext);
            Assert.Equal("Settings", model.SelectedPage);
            Assert.Equal("蓝图", model.SearchText);
            Assert.Equal("zh-CN", AppSettings.Load(paths).Language);
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

            // Render unmet prerequisite warning line + pale-yellow clickable pill in ModsView and jump to disabled loader row.
            var depMenu = new ModItemViewModel(model, new ModRecord
            {
                Id = "dep-menu",
                Name = "Mods Menu",
                Enabled = false,
                Files =
                [
                    new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/Binaries/Win64/hid.dll" },
                    new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js" },
                ],
            }, folder);
            model.Mods.Add(depMenu);
            model.Mods.Add(new ModItemViewModel(model, new ModRecord
            {
                Id = "dep-brighter",
                Name = "Brighter Loot Glow",
                Enabled = false,
                Files = [new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/Content/JavaScript/PBZModsMenu/Mods/Brighter/main.js" }],
            }, folder));
            model.RefreshTagFilters();
            FlushBindings();
            surface.Measure(new Size(1360, 800));
            surface.Arrange(new Rect(0, 0, 1360, 800));
            surface.UpdateLayout();
            FlushBindings();

            Assert.Contains(Descendants(surface).OfType<TextBlock>(), t => t.Text == "未启用");
            var pillButton = Descendants(surface).OfType<Button>().First(b => b.DataContext is UnmetPrerequisiteItem { IsDisabled: true, PrerequisiteName: "Mods Menu" });
            pillButton.Command.Execute(null);
            FlushBindings();
            Assert.True(depMenu.IsHighlighted);

            selector.SelectedValue = "en";
            FlushBindings();
            model.ModDetails = null;
            foreach (var added in model.Mods.Where(m => m.Id.StartsWith("dep-", StringComparison.Ordinal)).ToList())
                model.Mods.Remove(added);
            model.RefreshTagFilters();

            stage("checking the fixed transparent header on the Mods page");
            VerifyFixedModsHeader(surface, model, folder);
        }
        finally
        {
            stage("closing the window");
            window?.Close();
            Localizer.Current.SetLanguage(originalLanguage);
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

    private static void VerifyFixedModsHeader(FrameworkElement surface, MainViewModel model, string folder)
    {
        var modsView = Descendants(surface).OfType<Views.ModsView>().Single();
        Assert.Same(Brushes.Transparent, modsView.HeaderContainer.Background);

        var headerTexts = Descendants(modsView.HeaderContainer).OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Format.Tracked("Collection"), headerTexts);
        Assert.Contains("Mods", headerTexts);
        var headerButtons = Descendants(modsView.HeaderContainer).OfType<Button>().Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("Install from 7z/zip/rar…", headerButtons);
        Assert.Contains("Install from folder…", headerButtons);

        Assert.DoesNotContain(modsView.SearchBox, Descendants(modsView.HeaderContainer));
        Assert.DoesNotContain(modsView.Table, Descendants(modsView.HeaderContainer));
        Assert.Contains(modsView.SearchBox, Descendants(modsView.ModsScroll));
        Assert.Contains(modsView.Table, Descendants(modsView.ModsScroll));

        void LayoutAt(double width, double height)
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            FlushBindings();
            surface.UpdateLayout();
        }

        LayoutAt(1360, 800);
        Assert.Equal(Visibility.Collapsed, modsView.ModsScroll.ComputedVerticalScrollBarVisibility);
        Assert.Equal(
            modsView.HeaderContent.TranslatePoint(new Point(0, 0), surface).X,
            modsView.Table.TranslatePoint(new Point(0, 0), surface).X,
            precision: 1);
        Assert.Equal(modsView.HeaderContent.ActualWidth, modsView.Table.ActualWidth, precision: 1);

        var loader = new ModItemViewModel(model, new ModRecord
        {
            Id = "scroll-mod-00",
            Name = "AAA Mods Menu",
            Enabled = false,
            Files =
            [
                new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/Binaries/Win64/hid.dll" },
                new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js" },
            ],
        }, folder);
        model.Mods.Add(loader);
        for (var i = 1; i <= 24; i++)
        {
            model.Mods.Add(new ModItemViewModel(model, new ModRecord
            {
                Id = $"scroll-mod-{i:D2}",
                Name = $"Scroll Mod {i:D2}",
                Enabled = false,
                Files = [new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = $"TestGame/Content/JavaScript/PBZModsMenu/Mods/Mod{i:D2}/main.js" }],
            }, folder));
        }
        model.RefreshTagFilters();

        try
        {
            foreach (var (width, height) in new[] { (1360.0, 800.0), (1920.0, 1080.0) })
            {
                modsView.ModsScroll.ScrollToTop();
                LayoutAt(width, height);
                Assert.Equal(Visibility.Visible, modsView.ModsScroll.ComputedVerticalScrollBarVisibility);
                Assert.True(modsView.ModsScroll.ScrollableHeight > 220);

                Assert.Equal(
                    modsView.HeaderContent.TranslatePoint(new Point(0, 0), surface).X,
                    modsView.Table.TranslatePoint(new Point(0, 0), surface).X,
                    precision: 1);
                Assert.Equal(modsView.HeaderContent.ActualWidth, modsView.Table.ActualWidth, precision: 1);

                var headerBefore = modsView.HeaderContainer.TranslatePoint(new Point(0, 0), surface);
                var headerBottomBefore = modsView.HeaderContainer.TranslatePoint(new Point(0, modsView.HeaderContainer.ActualHeight), surface);
                var scrollBefore = modsView.ModsScroll.TranslatePoint(new Point(0, 0), surface);
                var tableBefore = modsView.Table.TranslatePoint(new Point(0, 0), modsView);
                Assert.Equal(headerBottomBefore.Y, scrollBefore.Y, precision: 1);

                modsView.ModsScroll.ScrollToVerticalOffset(200);
                surface.UpdateLayout();
                FlushBindings();

                var headerAfter = modsView.HeaderContainer.TranslatePoint(new Point(0, 0), surface);
                var tableAfter = modsView.Table.TranslatePoint(new Point(0, 0), modsView);
                Assert.Equal(headerBefore.Y, headerAfter.Y, precision: 1);
                Assert.Equal(tableBefore.Y - 200, tableAfter.Y, precision: 1);
            }

            // Clicking an unmet prerequisite pill from the bottom scrolls ModsScroll so the target row is visible below the fixed header.
            LayoutAt(1360, 800);
            modsView.ModsScroll.ScrollToEnd();
            surface.UpdateLayout();
            var bottomOffset = modsView.ModsScroll.VerticalOffset;
            Assert.True(bottomOffset > 200);

            var bottomMod = model.Mods.Single(m => m.Id == "scroll-mod-24");
            bottomMod.UnmetPrerequisites.Single(p => p.IsDisabled).NavigateCommand.Execute(null);
            FlushBindings();
            surface.UpdateLayout();

            Assert.True(modsView.ModsScroll.VerticalOffset < bottomOffset);
            var loaderRow = Descendants(modsView.ModsList).OfType<FrameworkElement>()
                .First(e => ReferenceEquals(e.DataContext, loader) && e.Name == "Row");
            var loaderTopInScroll = loaderRow.TranslatePoint(new Point(0, 0), modsView.ModsScroll).Y;
            Assert.InRange(loaderTopInScroll, 0, modsView.ModsScroll.ViewportHeight - loaderRow.ActualHeight);
        }
        finally
        {
            modsView.ModsScroll.ScrollToTop();
            foreach (var added in model.Mods.Where(m => m.Id.StartsWith("scroll-mod-", StringComparison.Ordinal)).ToList())
                model.Mods.Remove(added);
            model.RefreshTagFilters();
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
