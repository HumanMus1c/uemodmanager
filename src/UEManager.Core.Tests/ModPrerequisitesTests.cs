using UEManager.Core.Games;
using UEManager.Core.Mods;
using UEManager.Core.Storage;
using Xunit;

namespace UEManager.Core.Tests;

public class ModPrerequisitesTests : IDisposable
{
    private readonly TestFolder folder = new();
    private readonly GameInstall game;
    private readonly ModManager manager;

    public ModPrerequisitesTests()
    {
        game = GameLocator.Resolve(TestFolder.Profile(), folder.Game(), GameStore.Manual)!;
        manager = new ModManager(game, new AppPaths(folder.Combine("Data")));
    }

    public void Dispose() => folder.Dispose();

    private ModRecord InstallAll(string source, string name)
    {
        using var preview = manager.Inspect(source);
        return manager.Install(preview, preview.Packages.Where(p => !p.IsAlternate).ToList(), name, null);
    }

    private static ModRecord RecordWith(params (DeployTarget Target, string FileName)[] files) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Mod",
        Enabled = true,
        Files = files.Select(f => new ModFileRecord { Target = f.Target, FileName = f.FileName }).ToList(),
    };

    [Theory]
    [InlineData(DeployTarget.Mods, "BetterCamera_P.pak")]
    [InlineData(DeployTarget.InstallRoot, "TestGame/Saved/Keys.sav")]
    [InlineData(DeployTarget.ExeFolder, "dxgi.dll")]
    public void Standalone_mods_have_no_prerequisites(DeployTarget target, string fileName)
    {
        var mod = RecordWith((target, fileName));
        var states = new ModPrerequisiteStates(false, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing);

        var info = ModPrerequisites.Analyze(mod, states);

        Assert.True(info.IsStandalone);
        Assert.False(info.IsLoader);
        Assert.False(info.HasPrerequisites);
        Assert.False(info.HasMissingOrDisabled);
        Assert.Empty(info.Prerequisites);
    }

    [Theory]
    [InlineData(DeployTarget.Ue4ssMods, "ModA/Scripts/main.lua")]
    [InlineData(DeployTarget.Ue4ssMods, "ModB/dlls/main.dll")]
    [InlineData(DeployTarget.LogicMods, "BlueprintMod.pak")]
    public void Ue4ss_and_blueprint_mods_require_ue4ss(DeployTarget target, string fileName)
    {
        var mod = RecordWith((target, fileName));

        var missing = ModPrerequisites.Analyze(mod,
            new ModPrerequisiteStates(false, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing));
        Assert.Equal([new ModPrerequisite(ModPrerequisiteKind.Ue4ss, ModPrerequisiteState.Missing)], missing.Prerequisites);
        Assert.True(missing.HasMissingOrDisabled);
        Assert.False(missing.IsLoader);
        Assert.False(missing.IsStandalone);

        var ready = ModPrerequisites.Analyze(mod,
            new ModPrerequisiteStates(true, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing));
        Assert.Equal([new ModPrerequisite(ModPrerequisiteKind.Ue4ss, ModPrerequisiteState.Ready)], ready.Prerequisites);
        Assert.False(ready.HasMissingOrDisabled);
    }

    [Fact]
    public void Ue4ss_lua_and_dll_mod_reports_single_ue4ss_prerequisite()
    {
        var mod = RecordWith(
            (DeployTarget.Ue4ssMods, "Combo/Scripts/main.lua"),
            (DeployTarget.Ue4ssMods, "Combo/dlls/main.dll"));
        var info = ModPrerequisites.Analyze(mod,
            new ModPrerequisiteStates(true, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing));

        var only = Assert.Single(info.Prerequisites);
        Assert.Equal(ModPrerequisiteKind.Ue4ss, only.Kind);
        Assert.Equal(ModPrerequisiteState.Ready, only.State);
    }

    [Theory]
    [InlineData(DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Regen/main.js", ModPrerequisiteKind.ModLoader)]
    [InlineData(DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/Markers/main.js", ModPrerequisiteKind.ModsMenu)]
    [InlineData(DeployTarget.InstallRoot, "TestGame/Binaries/Win64/CSharpLoader/Mods/Faster/Faster.dll", ModPrerequisiteKind.CSharpLoader)]
    [InlineData(DeployTarget.ExeFolder, "CSharpLoader/Mods/Faster/Faster.dll", ModPrerequisiteKind.CSharpLoader)]
    public void Loader_mods_reflect_the_state_of_their_prerequisite_loader(
        DeployTarget target, string fileName, ModPrerequisiteKind expectedKind)
    {
        var mod = RecordWith((target, fileName));

        foreach (var state in new[] { ModPrerequisiteState.Missing, ModPrerequisiteState.Disabled, ModPrerequisiteState.Ready })
        {
            var states = new ModPrerequisiteStates(
                false,
                expectedKind == ModPrerequisiteKind.ModLoader ? state : ModPrerequisiteState.Missing,
                expectedKind == ModPrerequisiteKind.ModsMenu ? state : ModPrerequisiteState.Missing,
                expectedKind == ModPrerequisiteKind.CSharpLoader ? state : ModPrerequisiteState.Missing);

            var info = ModPrerequisites.Analyze(mod, states);

            var prerequisite = Assert.Single(info.Prerequisites);
            Assert.Equal(expectedKind, prerequisite.Kind);
            Assert.Equal(state, prerequisite.State);
            Assert.Equal(state != ModPrerequisiteState.Ready, info.HasMissingOrDisabled);
            Assert.False(info.IsLoader);
            Assert.False(info.IsStandalone);
        }
    }

    [Fact]
    public void Hybrid_mod_reports_all_required_prerequisites_with_distinct_states()
    {
        var hybrid = RecordWith(
            (DeployTarget.Ue4ssMods, "Hybrid/Scripts/main.lua"),
            (DeployTarget.InstallRoot, "TestGame/PBZModLoader/mods/Hybrid/main.js"));

        var info = ModPrerequisites.Analyze(hybrid,
            new ModPrerequisiteStates(true, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing));

        Assert.Equal(2, info.Prerequisites.Count);
        Assert.Contains(new ModPrerequisite(ModPrerequisiteKind.Ue4ss, ModPrerequisiteState.Ready), info.Prerequisites);
        Assert.Contains(new ModPrerequisite(ModPrerequisiteKind.ModLoader, ModPrerequisiteState.Missing), info.Prerequisites);
        Assert.True(info.HasMissingOrDisabled);
    }

    [Fact]
    public void Loaders_themselves_report_is_loader_and_no_self_prerequisite()
    {
        var pbzModLoader = new ModRecord
        {
            Id = "loader-pbz",
            Name = "PBZModLoader",
            Enabled = true,
            SourceFileName = "PBZModLoader-2.1.0.zip",
            Files =
            [
                new ModFileRecord { Target = DeployTarget.ExeFolder, FileName = "version.dll" },
                new ModFileRecord { Target = DeployTarget.InstallRoot, FileName = "TestGame/PBZModLoader/mods/Sample/main.js" },
            ],
        };
        var modsMenu = RecordWith(
            (DeployTarget.ExeFolder, "hid.dll"),
            (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/bootstrap.js"),
            (DeployTarget.InstallRoot, "TestGame/Content/JavaScript/PBZModsMenu/Mods/PBZModsMenu/main.js"));
        var csharpLoaderInstallRoot = RecordWith(
            (DeployTarget.InstallRoot, "b1/Binaries/Win64/dwmapi.dll"),
            (DeployTarget.InstallRoot, "b1/Binaries/Win64/CSharpLoader/CSharpManager.bin"),
            (DeployTarget.InstallRoot, "b1/Binaries/Win64/CSharpLoader/Mods/Sample/Sample.dll"));
        var csharpLoaderExeFolder = RecordWith(
            (DeployTarget.ExeFolder, "dwmapi.dll"),
            (DeployTarget.ExeFolder, "CSharpLoader/CSharpManager.bin"),
            (DeployTarget.ExeFolder, "CSharpLoader/Mods/Sample/Sample.dll"));

        var states = ModPrerequisites.FromRecords([pbzModLoader, modsMenu, csharpLoaderInstallRoot], ue4ssInstalled: true);
        Assert.Equal(ModPrerequisiteState.Ready, states.ModLoader);
        Assert.Equal(ModPrerequisiteState.Ready, states.ModsMenu);
        Assert.Equal(ModPrerequisiteState.Ready, states.CSharpLoader);

        foreach (var loader in new[] { pbzModLoader, modsMenu, csharpLoaderInstallRoot, csharpLoaderExeFolder })
        {
            var info = ModPrerequisites.Analyze(loader, states);
            Assert.True(info.IsLoader);
            Assert.False(info.IsStandalone);
            Assert.Empty(info.Prerequisites);
        }
    }

    [Fact]
    public void Manager_prerequisite_states_track_install_disable_enable_and_hand_placed_loaders()
    {
        var initial = manager.PrerequisiteStates(ue4ssInstalled: false);
        Assert.Equal(new ModPrerequisiteStates(false, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing, ModPrerequisiteState.Missing), initial);

        folder.File(@"Downloads\PBZModLoader\TestGame\Binaries\Win64\version.dll", "[PBZMOD] loader");
        folder.File(@"Downloads\PBZModLoader\TestGame\PBZModLoader\LICENSES.txt", "MIT");
        var pbzLoader = InstallAll(folder.Combine("Downloads", "PBZModLoader"), "PBZModLoader");

        folder.File(@"Downloads\ModsMenu\TestGame\Binaries\Win64\hid.dll", "menu entry");
        folder.File(@"Downloads\ModsMenu\TestGame\Content\JavaScript\PBZModsMenu\bootstrap.js", "menu start");
        folder.File(@"Downloads\ModsMenu\TestGame\Content\JavaScript\PBZModsMenu\Mods\PBZModsMenu\main.js", "menu ui");
        var modsMenu = InstallAll(folder.Combine("Downloads", "ModsMenu"), "Mods Menu");

        folder.File(@"Downloads\CSharpLoader\TestGame\Binaries\Win64\dwmapi.dll", "proxy");
        folder.File(@"Downloads\CSharpLoader\TestGame\Binaries\Win64\CSharpLoader\CSharpManager.bin", "csharp");
        var csharpLoader = InstallAll(folder.Combine("Downloads", "CSharpLoader"), "C# Loader");

        var ready = manager.PrerequisiteStates(ue4ssInstalled: true);
        Assert.True(ready.Ue4ssInstalled);
        Assert.Equal(ModPrerequisiteState.Ready, ready.ModLoader);
        Assert.Equal(ModPrerequisiteState.Ready, ready.ModsMenu);
        Assert.Equal(ModPrerequisiteState.Ready, ready.CSharpLoader);

        manager.SetEnabled(pbzLoader.Id, false);
        manager.SetEnabled(modsMenu.Id, false);
        manager.SetEnabled(csharpLoader.Id, false);

        var disabled = manager.PrerequisiteStates(ue4ssInstalled: true);
        Assert.Equal(ModPrerequisiteState.Disabled, disabled.ModLoader);
        Assert.Equal(ModPrerequisiteState.Disabled, disabled.ModsMenu);
        Assert.Equal(ModPrerequisiteState.Disabled, disabled.CSharpLoader);

        manager.SetEnabled(pbzLoader.Id, true);
        manager.SetEnabled(modsMenu.Id, true);
        manager.SetEnabled(csharpLoader.Id, true);

        var reenabled = manager.PrerequisiteStates(ue4ssInstalled: true);
        Assert.Equal(ModPrerequisiteState.Ready, reenabled.ModLoader);
        Assert.Equal(ModPrerequisiteState.Ready, reenabled.ModsMenu);
        Assert.Equal(ModPrerequisiteState.Ready, reenabled.CSharpLoader);

        manager.Uninstall(pbzLoader.Id);
        manager.Uninstall(modsMenu.Id);
        manager.Uninstall(csharpLoader.Id);

        Assert.Equal(ModPrerequisiteState.Missing, manager.PrerequisiteStates(false).ModLoader);
        Assert.Equal(ModPrerequisiteState.Missing, manager.PrerequisiteStates(false).ModsMenu);
        Assert.Equal(ModPrerequisiteState.Missing, manager.PrerequisiteStates(false).CSharpLoader);

        folder.File(@"Game\TestGame\Binaries\Win64\version.dll", "[PBZMOD] by hand");
        folder.File(@"Game\TestGame\Content\JavaScript\PBZModsMenu\bootstrap.js", "by hand");
        folder.File(@"Game\TestGame\Binaries\Win64\CSharpLoader\CSharpManager.bin", "by hand");

        var handPlaced = manager.PrerequisiteStates(ue4ssInstalled: false);
        Assert.Equal(ModPrerequisiteState.Ready, handPlaced.ModLoader);
        Assert.Equal(ModPrerequisiteState.Ready, handPlaced.ModsMenu);
        Assert.Equal(ModPrerequisiteState.Ready, handPlaced.CSharpLoader);
    }

    [Fact]
    public void Disabling_or_uninstalling_loader_cascades_to_dependents_and_enforces_prerequisites()
    {
        var gameRunning = false;
        var paths = new AppPaths(folder.Combine("Data"));
        var mgr = new ModManager(game, paths, _ => gameRunning);

        ModRecord Install(string source, string name)
        {
            using var preview = mgr.Inspect(source);
            return mgr.Install(preview, preview.Packages.Where(p => !p.IsAlternate).ToList(), name, null);
        }

        folder.File(@"Downloads\PBZModLoader\TestGame\Binaries\Win64\version.dll", "[PBZMOD] loader");
        folder.File(@"Downloads\PBZModLoader\TestGame\PBZModLoader\LICENSES.txt", "MIT");
        var pbzLoader = Install(folder.Combine("Downloads", "PBZModLoader"), "PBZModLoader");

        folder.File(@"Downloads\ModsMenu\TestGame\Binaries\Win64\hid.dll", "menu entry");
        folder.File(@"Downloads\ModsMenu\TestGame\Content\JavaScript\PBZModsMenu\bootstrap.js", "menu start");
        var modsMenu = Install(folder.Combine("Downloads", "ModsMenu"), "Mods Menu");

        folder.File(@"Downloads\CSharpLoader\TestGame\Binaries\Win64\dwmapi.dll", "proxy");
        folder.File(@"Downloads\CSharpLoader\TestGame\Binaries\Win64\CSharpLoader\CSharpManager.bin", "csharp");
        var csharpLoader = Install(folder.Combine("Downloads", "CSharpLoader"), "C# Loader");

        folder.File(@"Downloads\Brighter\TestGame\Content\JavaScript\PBZModsMenu\Mods\BrighterLoot\main.js", "brighter");
        var brighter = Install(folder.Combine("Downloads", "Brighter"), "Brighter Loot Glow");

        folder.File(@"Downloads\Regen\TestGame\PBZModLoader\mods\Regen\main.js", "regen");
        var regen = Install(folder.Combine("Downloads", "Regen"), "Regen");

        folder.File(@"Downloads\Faster\TestGame\Binaries\Win64\CSharpLoader\Mods\Faster\Faster.dll", "faster");
        var faster = Install(folder.Combine("Downloads", "Faster"), "Faster");

        var brighterGameFile = folder.Combine("Game", "TestGame", "Content", "JavaScript", "PBZModsMenu", "Mods", "BrighterLoot", "main.js");
        var regenGameFile = folder.Combine("Game", "TestGame", "PBZModLoader", "mods", "Regen", "main.js");
        Assert.True(File.Exists(brighterGameFile));
        Assert.True(File.Exists(regenGameFile));

        // Disabling Mods Menu cascades only to Mods Menu dependents.
        var disabledWithMenu = mgr.ToggleEnabled(modsMenu.Id, false);
        Assert.Equal(["Brighter Loot Glow"], disabledWithMenu);
        Assert.False(mgr.Mods.Single(m => m.Id == brighter.Id).Enabled);
        Assert.False(File.Exists(brighterGameFile));
        Assert.True(mgr.Mods.Single(m => m.Id == regen.Id).Enabled);
        Assert.True(mgr.Mods.Single(m => m.Id == faster.Id).Enabled);

        // Enabling a dependent while its loader is disabled throws ModException.
        var blocked = Assert.Throws<ModException>(() => mgr.SetEnabled(brighter.Id, true));
        Assert.Contains("Mods Menu", blocked.Message);

        // Installing a new dependent while its loader is disabled keeps it disabled in the library without deploying.
        folder.File(@"Downloads\Markers\TestGame\Content\JavaScript\PBZModsMenu\Mods\Markers\main.js", "markers");
        var markers = Install(folder.Combine("Downloads", "Markers"), "Markers");
        var markersGameFile = folder.Combine("Game", "TestGame", "Content", "JavaScript", "PBZModsMenu", "Mods", "Markers", "main.js");
        Assert.False(markers.Enabled);
        Assert.False(File.Exists(markersGameFile));

        // Re-enabling Mods Menu unlocks dependents without auto-enabling them; player enables manually.
        mgr.SetEnabled(modsMenu.Id, true);
        Assert.False(mgr.Mods.Single(m => m.Id == brighter.Id).Enabled);
        Assert.False(mgr.Mods.Single(m => m.Id == markers.Id).Enabled);
        mgr.SetEnabled(brighter.Id, true);
        Assert.True(mgr.Mods.Single(m => m.Id == brighter.Id).Enabled);
        Assert.True(File.Exists(brighterGameFile));

        // Uninstalling an active loader cascades to its active dependents.
        var uninstalled = mgr.UninstallWithDetails(pbzLoader.Id);
        Assert.Null(mgr.Mods.SingleOrDefault(m => m.Id == pbzLoader.Id));
        Assert.Equal(["Regen"], uninstalled.DisabledDependents);
        Assert.False(mgr.Mods.Single(m => m.Id == regen.Id).Enabled);
        Assert.False(File.Exists(regenGameFile));

        // Simulate legacy inconsistent state where Mods Menu is disabled while Brighter Loot Glow stayed Enabled = true.
        mgr.SetEnabled(modsMenu.Id, false);
        var statePath = paths.StateFile(TestFolder.Profile().Id);
        var state = JsonFile.Read<ModState>(statePath)!;
        state.Mods.Single(m => m.Id == brighter.Id).Enabled = true;
        JsonFile.Write(statePath, state);
        var reloaded = new ModManager(game, paths, _ => gameRunning);

        gameRunning = true;
        Assert.Empty(reloaded.EnforcePrerequisites());
        Assert.True(reloaded.Mods.Single(m => m.Id == brighter.Id).Enabled);

        gameRunning = false;
        Assert.Equal(["Brighter Loot Glow"], reloaded.EnforcePrerequisites());
        Assert.False(reloaded.Mods.Single(m => m.Id == brighter.Id).Enabled);
        Assert.False(File.Exists(brighterGameFile));

        // If a hand-placed PBZModLoader remains on disk, uninstalling the managed PBZModLoader keeps dependents enabled.
        using (var pbzPreview = reloaded.Inspect(folder.Combine("Downloads", "PBZModLoader")))
            pbzLoader = reloaded.Install(pbzPreview, pbzPreview.Packages.Where(p => !p.IsAlternate).ToList(), "PBZModLoader", null);
        reloaded.SetEnabled(regen.Id, true);
        folder.File(@"Game\TestGame\Binaries\Win64\hid.dll", "[PBZMOD] hand-placed loader");
        var keptByHand = reloaded.UninstallWithDetails(pbzLoader.Id);
        Assert.Empty(keptByHand.DisabledDependents);
        Assert.True(reloaded.Mods.Single(m => m.Id == regen.Id).Enabled);
    }
}
