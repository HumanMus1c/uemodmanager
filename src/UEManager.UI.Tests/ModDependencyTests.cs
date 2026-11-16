using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using UEManager.Core.Games;
using UEManager.Core.Mods;
using UEManager.Core.Storage;
using UEManager.UI.Services;
using UEManager.UI.ViewModels;
using Xunit;
using L = UEManager.UI.Localizer;

namespace UEManager.UI.Tests;

/// <summary>Prerequisite badges, blue type hints, dependency filter pills, search matching, and live localization.</summary>
[Collection("Language")]
public sealed class ModDependencyTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "uemm-deps-" + Guid.NewGuid().ToString("N"));
    private readonly string originalLanguage = L.Current.Language;

    public ModDependencyTests()
    {
        L.Current.SetLanguage(L.English);
    }

    public void Dispose()
    {
        L.Current.SetLanguage(originalLanguage);
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    private MainViewModel CreateModel()
    {
        var paths = new AppPaths(Path.Combine(folder, "Data"));
        paths.EnsureWritable();
        return new MainViewModel(
            new GameProfile { Id = "test", DisplayName = "Phantom Blade Zero", ProjectFolder = "TestGame" },
            paths,
            new ArtSet(null, null));
    }

    private ModItemViewModel AddMod(
        MainViewModel model,
        string name,
        bool enabled = true,
        string? sourceFileName = null,
        string[]? tags = null,
        params (DeployTarget Target, string FileName)[] files)
    {
        var record = new ModRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Enabled = enabled,
            SourceFileName = sourceFileName,
            Tags = tags is { Length: > 0 } ? [.. tags] : null,
            Files = files.Select(f => new ModFileRecord { Target = f.Target, FileName = f.FileName }).ToList(),
        };
        var item = new ModItemViewModel(model, record, Path.Combine(folder, "library"));
        model.Mods.Add(item);
        return item;
    }

    private static string[] Shown(MainViewModel model) =>
        model.ModsView.Cast<ModItemViewModel>().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();

    private static TagFilterItem DependencyPill(MainViewModel model, string label) =>
        model.DependencyFilters.Single(f => f.Label == label);

    private static ModRecord MakeLoaderRecord(string name, string? sourceFileName, params (DeployTarget Target, string FileName)[] files) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Enabled = true,
        SourceFileName = sourceFileName,
        Files = files.Select(f => new ModFileRecord { Target = f.Target, FileName = f.FileName }).ToList(),
    };

    private string CreateFakeGame(string name = "Game", string project = "TestGame")
    {
        var root = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(root, project, "Content", "Paks"));
        Directory.CreateDirectory(Path.Combine(root, project, "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(root, project, "Binaries", "Win64", project + "-Win64-Shipping.exe"), "");
        File.WriteAllText(Path.Combine(root, project + ".exe"), "");
        return root;
    }

    private static void WaitFor(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Every_dependency_category_builds_expected_badges_hints_and_details() => UiThread.Run(() =>
    {
        var model = CreateModel();
        var pak = AddMod(model, "Camera PAK", files: [(DeployTarget.Mods, "Camera_P.pak")]);
        var pbzLoader = AddMod(model, "PBZModLoader", enabled: false, sourceFileName: "PBZModLoader-2.1.0.zip",
            files: [(DeployTarget.ExeFolder, "version.dll")]);
        var modsMenu = AddMod(model, "Mods Menu", enabled: true, sourceFileName: "ModsMenu.zip",
            files: [(DeployTarget.ExeFolder, "hid.dll"), (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js")]);
        var luaMissing = AddMod(model, "Lua Mod", files: [(DeployTarget.Ue4ssMods, "LuaMod/Scripts/main.lua")]);
        var pbzModDisabledLoader = AddMod(model, "Regen", files: [(DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Regen/main.js")]);
        var menuModReady = AddMod(model, "Markers", files: [(DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/Markers/main.js")]);
        var csharpMissing = AddMod(model, "Faster", files: [(DeployTarget.ExeFolder, "CSharpLoader/Mods/Faster/Faster.dll")]);
        var hybrid = AddMod(model, "Hybrid", files:
        [
            (DeployTarget.Ue4ssMods, "Hybrid/Scripts/main.lua"),
            (DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Hybrid/main.js"),
        ]);

        var pakBadge = Assert.Single(pak.DependencyBadges);
        Assert.Equal("No prerequisite", pakBadge.Label);
        Assert.Equal("muted", pakBadge.Tone);
        Assert.Equal("\uE73E", pakBadge.Glyph);
        Assert.Equal(Format.KindHint("PAK"), pak.TypeHint);
        Assert.Contains(".pak", pak.TypeHint);

        var loaderBadge = Assert.Single(pbzLoader.DependencyBadges);
        Assert.Equal("Prerequisite loader", loaderBadge.Label);
        Assert.Equal("good", loaderBadge.Tone);
        Assert.Equal("\uE81E", loaderBadge.Glyph);
        Assert.Equal(loaderBadge, Assert.Single(modsMenu.DependencyBadges));

        var ue4ssMissing = Assert.Single(luaMissing.DependencyBadges);
        Assert.Equal("UE4SS · missing", ue4ssMissing.Label);
        Assert.Equal("bad", ue4ssMissing.Tone);
        Assert.Equal("\uE7BA", ue4ssMissing.Glyph);
        Assert.Contains("UE4SS", ue4ssMissing.Hint);

        var loaderDisabled = Assert.Single(pbzModDisabledLoader.DependencyBadges);
        Assert.Equal("PBZModLoader · disabled", loaderDisabled.Label);
        Assert.Equal("warn", loaderDisabled.Tone);
        Assert.Contains("disabled", loaderDisabled.Hint);

        var menuReady = Assert.Single(menuModReady.DependencyBadges);
        Assert.Equal("Needs Mods Menu", menuReady.Label);
        Assert.Equal("info", menuReady.Tone);
        Assert.Contains("installed and ready", menuReady.Hint);

        var csharpBadge = Assert.Single(csharpMissing.DependencyBadges);
        Assert.Equal("C# Loader · missing", csharpBadge.Label);
        Assert.Equal("bad", csharpBadge.Tone);

        Assert.Equal(2, hybrid.DependencyBadges.Count);
        Assert.Equal(ue4ssMissing.Label, hybrid.DependencyBadges[0].Label);
        Assert.Equal(loaderDisabled.Label, hybrid.DependencyBadges[1].Label);

        var details = new ModDetailsViewModel(model, hybrid.Record, [], [], folder);
        Assert.Equal(hybrid.DependencyBadges, details.DependencyBadges);

        // Standalone direct state check for UE4SS ready (Lua vs DLL hint) and C# Loader ready.
        var readyStates = new ModPrerequisiteStates(true, ModPrerequisiteState.Ready, ModPrerequisiteState.Ready, ModPrerequisiteState.Ready);
        var dllInfo = ModPrerequisites.Analyze(MakeLoaderRecord("DllMod", null, (DeployTarget.Ue4ssMods, "DllMod/dlls/main.dll")), readyStates);
        var dllBadge = Assert.Single(ModDependencyBadge.Build(
            MakeLoaderRecord("DllMod", null, (DeployTarget.Ue4ssMods, "DllMod/dlls/main.dll")),
            dllInfo));
        Assert.Equal("Needs UE4SS", dllBadge.Label);
        Assert.Equal("info", dllBadge.Tone);
        Assert.Contains("C++ DLL", dllBadge.Hint);

        var csharpReadyInfo = ModPrerequisites.Analyze(
            MakeLoaderRecord("CSharpInstallRoot", null, (DeployTarget.InstallRoot, "TestGame/Binaries/Win64/CSharpLoader/Mods/F/F.dll")),
            readyStates);
        var csharpReadyBadge = Assert.Single(ModDependencyBadge.Build(
            MakeLoaderRecord("CSharpInstallRoot", null, (DeployTarget.InstallRoot, "TestGame/Binaries/Win64/CSharpLoader/Mods/F/F.dll")),
            csharpReadyInfo));
        Assert.Equal("Needs C# Loader", csharpReadyBadge.Label);
        Assert.Equal("info", csharpReadyBadge.Tone);
    });

    [Fact]
    public void Filter_buttons_toggle_combine_with_state_tag_and_search_and_reset_when_empty() => UiThread.Run(() =>
    {
        var model = CreateModel();
        AddMod(model, "CamA", files: [(DeployTarget.Mods, "CamA_P.pak")]);
        model.RefreshTagFilters();

        // Only 1 prerequisite category ("No prerequisite"): pill bar stays hidden.
        Assert.False(model.HasDependencyFilters);
        Assert.Empty(model.DependencyFilters);

        AddMod(model, "PBZModLoader", enabled: false, sourceFileName: "PBZModLoader-2.1.0.zip",
            files: [(DeployTarget.ExeFolder, "version.dll")]);
        AddMod(model, "LuaMod", enabled: true, tags: ["Combat"],
            files: [(DeployTarget.Ue4ssMods, "LuaMod/Scripts/main.lua")]);
        AddMod(model, "Regen", enabled: false, tags: ["Combat"],
            files: [(DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Regen/main.js")]);
        AddMod(model, "Markers", enabled: true, tags: ["Maps"],
            files: [(DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/Markers/main.js")]);
        AddMod(model, "Faster", enabled: true,
            files: [(DeployTarget.ExeFolder, "CSharpLoader/Mods/Faster/Faster.dll")]);
        model.RefreshTagFilters();

        Assert.True(model.HasDependencyFilters);
        Assert.Equal(
        [
            "All (6)",
            "Missing prerequisite (4)",
            "UE4SS (1)",
            "PBZModLoader (1)",
            "Mods Menu (1)",
            "C# Loader (1)",
            "Prerequisite loader (1)",
            "No prerequisite (1)",
        ], model.DependencyFilters.Select(f => f.Label));
        Assert.Equal("bad", DependencyPill(model, "Missing prerequisite (4)").Tone);
        Assert.True(DependencyPill(model, "All (6)").IsActive);

        // Missing prerequisite filter includes both Missing (LuaMod, Markers, Faster) and Disabled (Regen) prerequisites.
        DependencyPill(model, "Missing prerequisite (4)").Select.Execute(null);
        Assert.Equal(["Faster", "LuaMod", "Markers", "Regen"], Shown(model));

        // Combine with StateFilter and SelectedTag.
        model.StateFilter = ModStateFilter.Enabled;
        Assert.Equal(["Faster", "LuaMod", "Markers"], Shown(model));
        model.SelectedTag = "Combat";
        Assert.Equal(["LuaMod"], Shown(model));

        // Clear state + tag, switch to UE4SS, then click UE4SS again to return to All.
        model.StateFilter = ModStateFilter.All;
        model.SelectedTag = null;
        DependencyPill(model, "UE4SS (1)").Select.Execute(null);
        Assert.Equal(["LuaMod"], Shown(model));
        DependencyPill(model, "UE4SS (1)").Select.Execute(null);
        Assert.Equal(ModDependencyFilter.All, model.DependencyFilter);
        Assert.Equal(6, Shown(model).Length);

        // Search matches badge labels and canonical search terms.
        model.SearchText = "disabled";
        Assert.Equal(["Regen"], Shown(model));
        model.SearchText = "Mods Menu";
        Assert.Equal(["Markers"], Shown(model));
        model.SearchText = "Standalone";
        Assert.Equal(["CamA"], Shown(model));

        // Active DependencyFilter resets to All if no remaining mod belongs to that category.
        model.SearchText = "";
        DependencyPill(model, "C# Loader (1)").Select.Execute(null);
        Assert.Equal(["Faster"], Shown(model));
        model.Mods.Remove(model.Mods.Single(m => m.Name == "Faster"));
        model.RefreshTagFilters();
        Assert.Equal(ModDependencyFilter.All, model.DependencyFilter);
        Assert.Equal(5, Shown(model).Length);
    });

    [Fact]
    public void Switching_language_updates_badges_filters_and_search_without_rebuilding_mods() => UiThread.Run(() =>
    {
        var model = CreateModel();
        var pak = AddMod(model, "Camera PAK", files: [(DeployTarget.Mods, "Camera_P.pak")]);
        var loader = AddMod(model, "PBZModLoader", enabled: false, sourceFileName: "PBZModLoader-2.1.0.zip",
            files: [(DeployTarget.ExeFolder, "version.dll")]);
        var menu = AddMod(model, "Mods Menu", enabled: true, sourceFileName: "ModsMenu.zip",
            files: [(DeployTarget.ExeFolder, "hid.dll"), (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js")]);
        var lua = AddMod(model, "LuaMod", files: [(DeployTarget.Ue4ssMods, "LuaMod/Scripts/main.lua")]);
        var regen = AddMod(model, "Regen", files: [(DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Regen/main.js")]);
        var markers = AddMod(model, "Markers", files: [(DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/Markers/main.js")]);
        var details = new ModDetailsViewModel(model, lua.Record, [], [], folder);
        model.RefreshTagFilters();

        try
        {
            L.Current.SetLanguage("zh-CN");

            // Existing ModItemViewModel and ModDetailsViewModel instances update dynamically without rebuilding model.Mods.
            Assert.Same(pak, model.Mods[0]);
            Assert.Equal("无需前置", Assert.Single(pak.DependencyBadges).Label);
            Assert.Equal("前置加载器", Assert.Single(loader.DependencyBadges).Label);
            Assert.Equal("UE4SS · 未安装", Assert.Single(lua.DependencyBadges).Label);
            Assert.Equal("UE4SS · 未安装", Assert.Single(details.DependencyBadges).Label);
            Assert.Equal("PBZModLoader · 已禁用", Assert.Single(regen.DependencyBadges).Label);
            Assert.Equal("前置：Mods Menu", Assert.Single(markers.DependencyBadges).Label);
            Assert.Equal("前置加载器", Assert.Single(menu.DependencyBadges).Label);

            Assert.Equal(
            [
                "全部（6）",
                "缺少前置（2）",
                "UE4SS（1）",
                "PBZModLoader（1）",
                "Mods Menu（1）",
                "前置加载器（2）",
                "无需前置（1）",
            ], model.DependencyFilters.Select(f => f.Label));

            model.SearchText = "未安装";
            Assert.Equal(["LuaMod"], Shown(model));
            model.SearchText = "已禁用";
            Assert.Equal(["Regen"], Shown(model));
            model.SearchText = "前置加载器";
            Assert.Equal(["Mods Menu", "PBZModLoader"], Shown(model));
        }
        finally
        {
            L.Current.SetLanguage(L.English);
        }
    });

    [Fact]
    public void Initialize_detects_ue4ss_and_loader_states_before_building_mod_items() => UiThread.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var gameRoot = CreateFakeGame();
        var profile = new GameProfile { Id = "test-init", DisplayName = "Phantom Blade Zero", ProjectFolder = "TestGame" };
        var paths = new AppPaths(Path.Combine(folder, "Data"));
        paths.EnsureWritable();

        // Install UE4SS, disabled PBZModLoader, enabled Lua mod, enabled PBZModLoader mod, and standalone PAK mod.
        Directory.CreateDirectory(Path.Combine(gameRoot, "TestGame", "Binaries", "Win64", "ue4ss"));
        File.WriteAllText(Path.Combine(gameRoot, "TestGame", "Binaries", "Win64", "ue4ss", "UE4SS.dll"), "ue4ss");
        File.WriteAllText(Path.Combine(gameRoot, "TestGame", "Binaries", "Win64", "dwmapi.dll"), "proxy");

        var dlLoader = Path.Combine(folder, "Downloads", "PBZModLoader");
        Directory.CreateDirectory(Path.Combine(dlLoader, "TestGame", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(dlLoader, "TestGame", "Binaries", "Win64", "version.dll"), "[PBZMOD] loader");

        var dlLua = Path.Combine(folder, "Downloads", "LuaMod", "ue4ss", "Mods", "LuaMod", "Scripts");
        Directory.CreateDirectory(dlLua);
        File.WriteAllText(Path.Combine(dlLua, "main.lua"), "print('hi')");

        var dlPbz = Path.Combine(folder, "Downloads", "RegenMod", "TestGame", "PBZModLoader", "mods", "Regen");
        Directory.CreateDirectory(dlPbz);
        File.WriteAllText(Path.Combine(dlPbz, "main.js"), "regen");

        var dlPak = Path.Combine(folder, "Downloads", "SkinMod");
        Directory.CreateDirectory(dlPak);
        File.WriteAllText(Path.Combine(dlPak, "Skin_P.pak"), "pak");

        var install = GameLocator.Resolve(profile, gameRoot, GameStore.Manual)!;
        var manager = new ModManager(install, paths);
        using (var p = manager.Inspect(dlLoader))
            manager.Install(p, p.Packages, "PBZModLoader", "2.1.0");
        using (var p = manager.Inspect(Path.Combine(folder, "Downloads", "LuaMod")))
            manager.Install(p, p.Packages, "LuaMod", "1.0");
        using (var p = manager.Inspect(Path.Combine(folder, "Downloads", "RegenMod")))
            manager.Install(p, p.Packages, "RegenMod", "1.0");
        using (var p = manager.Inspect(dlPak))
            manager.Install(p, p.Packages, "SkinMod", "1.0");
        manager.SetEnabled(manager.Mods.Single(m => m.Name == "PBZModLoader").Id, false);

        var settings = new AppSettings
        {
            Language = "en",
            DeveloperMessageShown = true,
            Game = profile.Id,
        };
        settings.GamePaths[profile.Id] = gameRoot;
        settings.Save(paths);

        var model = new MainViewModel(
            profile,
            paths,
            new ArtSet(null, null),
            catalogUrl: "http://127.0.0.1:1/catalog.json",
            updateUrl: "http://127.0.0.1:1/release.json",
            ue4ssManifestUrl: "http://127.0.0.1:1/ue4ss.json");

        WaitFor(model.InitializeAsync([]));

        Assert.True(model.IsUe4ssInstalled);
        Assert.Equal("Needs UE4SS", Assert.Single(model.Mods.Single(m => m.Name == "LuaMod").DependencyBadges).Label);
        Assert.Equal("PBZModLoader · disabled", Assert.Single(model.Mods.Single(m => m.Name == "RegenMod").DependencyBadges).Label);
        Assert.Equal("Prerequisite loader", Assert.Single(model.Mods.Single(m => m.Name == "PBZModLoader").DependencyBadges).Label);
        Assert.Equal("No prerequisite", Assert.Single(model.Mods.Single(m => m.Name == "SkinMod").DependencyBadges).Label);
        Assert.True(model.HasDependencyFilters);

        var regenItem = model.Mods.Single(m => m.Name == "RegenMod");
        Assert.False(regenItem.Enabled);
        Assert.False(regenItem.CanToggle);
        Assert.False(regenItem.ToggleCommand.CanExecute(null));
        Assert.Contains("PBZModLoader", regenItem.ToggleHint);
        Assert.True(model.Mods.Single(m => m.Name == "LuaMod").CanToggle);
    });

    [Fact]
    public void Legacy_enabled_mod_with_disabled_loader_is_auto_disabled_locked_and_unlocks_when_loader_returns() => UiThread.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var gameRoot = CreateFakeGame();
        var profile = new GameProfile { Id = "test-enforce", DisplayName = "Phantom Blade Zero", ProjectFolder = "TestGame" };
        var paths = new AppPaths(Path.Combine(folder, "Data"));
        paths.EnsureWritable();

        var dlMenu = Path.Combine(folder, "Downloads", "ModsMenu");
        Directory.CreateDirectory(Path.Combine(dlMenu, "TestGame", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(dlMenu, "TestGame", "Binaries", "Win64", "hid.dll"), "menu proxy");
        Directory.CreateDirectory(Path.Combine(dlMenu, "TestGame", "Content", "JavaScript", "PBZModsMenu"));
        File.WriteAllText(Path.Combine(dlMenu, "TestGame", "Content", "JavaScript", "PBZModsMenu", "bootstrap.js"), "boot");

        var dlBrighter = Path.Combine(folder, "Downloads", "Brighter", "TestGame", "Content", "JavaScript", "PBZModsMenu", "Mods", "BrighterLoot");
        Directory.CreateDirectory(dlBrighter);
        File.WriteAllText(Path.Combine(dlBrighter, "main.js"), "brighter");

        var install = GameLocator.Resolve(profile, gameRoot, GameStore.Manual)!;
        var manager = new ModManager(install, paths);
        using (var p = manager.Inspect(dlMenu))
            manager.Install(p, p.Packages, "Mods Menu", "0.9.8");
        using (var p = manager.Inspect(Path.Combine(folder, "Downloads", "Brighter")))
            manager.Install(p, p.Packages, "Brighter Loot Glow", "1.3.0");

        // Disable Mods Menu, then manually restore legacy inconsistent Enabled = true on Brighter Loot Glow in mods.json.
        manager.SetEnabled(manager.Mods.Single(m => m.Name == "Mods Menu").Id, false);
        var stateFile = paths.StateFile(profile.Id);
        var state = JsonFile.Read<ModState>(stateFile)!;
        state.Mods.Single(m => m.Name == "Brighter Loot Glow").Enabled = true;
        JsonFile.Write(stateFile, state);

        var settings = new AppSettings
        {
            Language = "en",
            DeveloperMessageShown = true,
            Game = profile.Id,
        };
        settings.GamePaths[profile.Id] = gameRoot;
        settings.Save(paths);

        var model = new MainViewModel(
            profile,
            paths,
            new ArtSet(null, null),
            catalogUrl: "http://127.0.0.1:1/catalog.json",
            updateUrl: "http://127.0.0.1:1/release.json",
            ue4ssManifestUrl: "http://127.0.0.1:1/ue4ss.json");

        WaitFor(model.InitializeAsync([]));

        var brighter = model.Mods.Single(m => m.Name == "Brighter Loot Glow");
        var modsMenu = model.Mods.Single(m => m.Name == "Mods Menu");
        Assert.False(brighter.Enabled);
        Assert.False(brighter.CanToggle);
        Assert.False(brighter.ToggleCommand.CanExecute(null));
        Assert.Contains("Mods Menu", brighter.ToggleHint);
        Assert.NotNull(model.Toast);
        Assert.Contains("Brighter Loot Glow", model.Toast!.Text);
        Assert.False(JsonFile.Read<ModState>(stateFile)!.Mods.Single(m => m.Name == "Brighter Loot Glow").Enabled);

        // Re-enabling Mods Menu unlocks Brighter Loot Glow's switch while keeping it disabled.
        WaitFor(model.SetEnabledAsync(modsMenu, true));
        modsMenu = model.Mods.Single(m => m.Name == "Mods Menu");
        brighter = model.Mods.Single(m => m.Name == "Brighter Loot Glow");
        Assert.True(modsMenu.Enabled);
        Assert.True(brighter.CanToggle);
        Assert.True(brighter.ToggleCommand.CanExecute(null));
        Assert.False(brighter.Enabled);

        // Player re-enables Brighter Loot Glow, then disabling Mods Menu cascades back to Brighter Loot Glow.
        WaitFor(model.SetEnabledAsync(brighter, true));
        Assert.True(model.Mods.Single(m => m.Name == "Brighter Loot Glow").Enabled);

        WaitFor(model.SetEnabledAsync(model.Mods.Single(m => m.Name == "Mods Menu"), false));
        brighter = model.Mods.Single(m => m.Name == "Brighter Loot Glow");
        Assert.False(brighter.Enabled);
        Assert.False(brighter.CanToggle);
        Assert.False(brighter.ToggleCommand.CanExecute(null));
        Assert.NotNull(model.Toast);
        Assert.Contains("Brighter Loot Glow", model.Toast!.Text);
    });

    [Fact]
    public void Unmet_prerequisites_render_lines_jump_to_disabled_loader_and_probe_missing_links() => UiThread.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var model = new MainViewModel(
            new GameProfile
            {
                Id = "phantom-blade-zero",
                DisplayName = "Phantom Blade Zero",
                ProjectFolder = "TestGame",
                Ue4ssDownloadUrl = "https://dead-ue4ss.example/releases",
                ModPageUrl = "https://www.nexusmods.com/phantombladezero/mods/{id}",
            },
            CreatePaths(),
            new ArtSet(null, null));

        var modsMenu = AddMod(model, "Mods Menu", enabled: false,
            "Mods Menu 149 0.9.8.zip", null,
            (DeployTarget.InstallRoot, "TestGame/Binaries/Win64/hid.dll"),
            (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js"));

        // Hybrid mod with BOTH a disabled prerequisite (Mods Menu) and a missing prerequisite (UE4SS).
        var hybrid = AddMod(model, "HybridMod", enabled: false, null, ["Combat"],
            (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/HybridMod/main.js"),
            (DeployTarget.Ue4ssMods, "HybridMod/Scripts/main.lua"));
        hybrid.Record.PrerequisiteUrls = new Dictionary<ModPrerequisiteKind, List<string>>
        {
            [ModPrerequisiteKind.Ue4ss] = ["https://dead-declared.example/ue4ss", "https://live-declared.example/ue4ss/releases"],
        };

        Assert.True(hybrid.HasUnmetPrerequisites);
        Assert.Equal(2, hybrid.UnmetPrerequisites.Count);
        Assert.Equal("Not installed", hybrid.UnmetPrerequisites[0].StatusPrefix);
        Assert.Equal("UE4SS", hybrid.UnmetPrerequisites[0].PrerequisiteName);
        Assert.True(hybrid.UnmetPrerequisites[0].IsMissing);
        Assert.Equal("Not enabled", hybrid.UnmetPrerequisites[1].StatusPrefix);
        Assert.Equal("Mods Menu", hybrid.UnmetPrerequisites[1].PrerequisiteName);
        Assert.True(hybrid.UnmetPrerequisites[1].IsDisabled);

        L.Current.SetLanguage("zh-CN");
        Assert.Equal("未安装", hybrid.UnmetPrerequisites[0].StatusPrefix);
        Assert.Equal("未启用", hybrid.UnmetPrerequisites[1].StatusPrefix);
        L.Current.SetLanguage(L.English);

        // Apply restrictive filters that hide Mods Menu; clicking [Mods Menu] clears conflicting filters, requests scroll, and flashes row.
        model.SelectedPage = "About";
        model.SearchText = "HybridMod";
        model.SelectedTag = "Combat";
        model.StateFilter = ModStateFilter.Enabled;
        model.DependencyFilter = ModDependencyFilter.Missing;
        Assert.DoesNotContain(modsMenu, model.ModsView.Cast<ModItemViewModel>());

        ModItemViewModel? scrolledTarget = null;
        model.ScrollToModRequested += target => scrolledTarget = target;

        hybrid.UnmetPrerequisites[1].NavigateCommand.Execute(null);

        Assert.Equal("Mods", model.SelectedPage);
        Assert.Equal("", model.SearchText);
        Assert.Null(model.SelectedTag);
        Assert.Equal(ModStateFilter.All, model.StateFilter);
        Assert.Equal(ModDependencyFilter.All, model.DependencyFilter);
        Assert.Contains(modsMenu, model.ModsView.Cast<ModItemViewModel>());
        Assert.Same(modsMenu, scrolledTarget);
        Assert.True(modsMenu.IsHighlighted);

        // Clicking missing [UE4SS] probes declared/official URLs sequentially, skipping 404s and opening the first reachable URL.
        var probedUrls = new List<string>();
        var openedUrls = new List<string>();
        using var testClient = new HttpClient(new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            probedUrls.Add(url);
            return Task.FromResult(new HttpResponseMessage(
                url.Contains("live-declared.example", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        }));
        model.PrerequisiteHttpForTest = testClient;
        model.OpenPathForTest = url => openedUrls.Add(url);

        var navTask = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)hybrid.UnmetPrerequisites[0].NavigateCommand).ExecuteAsync(null);
        WaitFor(navTask);

        Assert.Equal(["https://dead-declared.example/ue4ss", "https://live-declared.example/ue4ss/releases"], probedUrls);
        Assert.Equal(["https://live-declared.example/ue4ss/releases"], openedUrls);

        // When all candidate URLs fail or 404, show an error toast without calling OpenPath.
        openedUrls.Clear();
        using var failingClient = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        model.PrerequisiteHttpForTest = failingClient;
        var failTask = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)hybrid.UnmetPrerequisites[0].NavigateCommand).ExecuteAsync(null);
        WaitFor(failTask);
        Assert.Empty(openedUrls);
        Assert.NotNull(model.Toast);
        Assert.True(model.Toast!.IsError);
        Assert.Contains("UE4SS", model.Toast.Text);
    });

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(Path.Combine(folder, "Data"));
        paths.EnsureWritable();
        return paths;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
