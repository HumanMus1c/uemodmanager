namespace UEManager.Core.Mods;

/// <summary>A runtime or script loader that an installed mod requires in order to run.</summary>
public enum ModPrerequisiteKind
{
    Ue4ss,
    ModLoader,
    ModsMenu,
    CSharpLoader,
}

/// <summary>Current readiness of a prerequisite runtime or loader in the game install.</summary>
public enum ModPrerequisiteState
{
    /// <summary>Installed and active (or placed in the game by hand).</summary>
    Ready,

    /// <summary>Installed in the manager's mod list, but currently turned off.</summary>
    Disabled,

    /// <summary>Not installed in the game.</summary>
    Missing,
}

/// <summary>One prerequisite dependency of an installed mod and whether that prerequisite is ready.</summary>
public sealed record ModPrerequisite(ModPrerequisiteKind Kind, ModPrerequisiteState State);

/// <summary>Readiness snapshot of every prerequisite runtime and loader in a game install.</summary>
public sealed record ModPrerequisiteStates(
    bool Ue4ssInstalled,
    ModPrerequisiteState ModLoader,
    ModPrerequisiteState ModsMenu,
    ModPrerequisiteState CSharpLoader)
{
    public ModPrerequisiteState StateOf(ModPrerequisiteKind kind) => kind switch
    {
        ModPrerequisiteKind.Ue4ss => Ue4ssInstalled ? ModPrerequisiteState.Ready : ModPrerequisiteState.Missing,
        ModPrerequisiteKind.ModLoader => ModLoader,
        ModPrerequisiteKind.ModsMenu => ModsMenu,
        ModPrerequisiteKind.CSharpLoader => CSharpLoader,
        _ => ModPrerequisiteState.Missing,
    };
}

/// <summary>Prerequisite classification of an installed mod: required loaders, or whether the mod itself is a loader.</summary>
public sealed record ModDependencyInfo(IReadOnlyList<ModPrerequisite> Prerequisites, bool IsLoader)
{
    public bool HasPrerequisites => Prerequisites.Count > 0;

    public bool IsStandalone => !IsLoader && Prerequisites.Count == 0;

    public bool HasMissingOrDisabled => Prerequisites.Any(p => p.State != ModPrerequisiteState.Ready);

    public IReadOnlyList<ModPrerequisite> Unmet => [.. Prerequisites.Where(p => p.State != ModPrerequisiteState.Ready)];
}

/// <summary>Result of applying a mod set, including any mods skipped because a prerequisite runtime or loader is not ready.</summary>
public sealed record ModBatchResult(int On, int Off, IReadOnlyList<string> SkippedMods);

/// <summary>Inspects installed mods to classify what prerequisite loader or runtime each one depends on.</summary>
public static class ModPrerequisites
{
    /// <summary>Canonical display name of a prerequisite runtime or loader.</summary>
    public static string NameOf(ModPrerequisiteKind kind) => kind switch
    {
        ModPrerequisiteKind.Ue4ss => "UE4SS",
        ModPrerequisiteKind.ModLoader => "PBZModLoader",
        ModPrerequisiteKind.ModsMenu => "Mods Menu",
        ModPrerequisiteKind.CSharpLoader => "C# Loader",
        _ => "",
    };

    /// <summary>Loader kind(s) that <paramref name="mod"/> itself provides when installed and enabled.</summary>
    public static IReadOnlyList<ModPrerequisiteKind> LoaderKindsOf(ModRecord mod)
    {
        var kinds = new List<ModPrerequisiteKind>(1);
        if (ModManager.IsModLoaderRecord(mod))
            kinds.Add(ModPrerequisiteKind.ModLoader);
        if (ModManager.IsModsMenuRecord(mod))
            kinds.Add(ModPrerequisiteKind.ModsMenu);
        if (ModManager.IsCSharpLoaderRecord(mod))
            kinds.Add(ModPrerequisiteKind.CSharpLoader);
        return kinds;
    }

    /// <summary>Inspects <paramref name="mod"/>'s deployed files and evaluates detected prerequisites against <paramref name="states"/>.</summary>
    public static ModDependencyInfo Analyze(ModRecord mod, ModPrerequisiteStates states)
    {
        if (LoaderKindsOf(mod).Count > 0)
            return new ModDependencyInfo([], IsLoader: true);

        var required = new List<ModPrerequisite>(2);
        if (mod.NeedsUe4ss)
            required.Add(new ModPrerequisite(ModPrerequisiteKind.Ue4ss, states.StateOf(ModPrerequisiteKind.Ue4ss)));

        var deployedFiles = mod.Files.Where(f => !f.Made);
        if (deployedFiles.Any(f => f.Target == DeployTarget.InstallRoot && PackageScanner.IsModLoaderModPath(f.FileName)))
            required.Add(new ModPrerequisite(ModPrerequisiteKind.ModLoader, states.StateOf(ModPrerequisiteKind.ModLoader)));
        if (deployedFiles.Any(f => f.Target == DeployTarget.InstallRoot && PackageScanner.IsModsMenuPath(f.FileName)))
            required.Add(new ModPrerequisite(ModPrerequisiteKind.ModsMenu, states.StateOf(ModPrerequisiteKind.ModsMenu)));
        if (deployedFiles.Any(f => PackageScanner.IsCSharpModPath(f.Target, f.FileName)))
            required.Add(new ModPrerequisite(ModPrerequisiteKind.CSharpLoader, states.StateOf(ModPrerequisiteKind.CSharpLoader)));

        return new ModDependencyInfo(required, IsLoader: false);
    }

    /// <summary>
    /// Computes loader readiness purely from a list of installed mod records and UE4SS status, used when no
    /// <see cref="ModManager"/> is attached.
    /// </summary>
    public static ModPrerequisiteStates FromRecords(IEnumerable<ModRecord> mods, bool ue4ssInstalled)
    {
        var list = mods as IReadOnlyList<ModRecord> ?? mods.ToList();
        return new ModPrerequisiteStates(
            ue4ssInstalled,
            StateOf(list, ModManager.IsModLoaderRecord),
            StateOf(list, ModManager.IsModsMenuRecord),
            StateOf(list, ModManager.IsCSharpLoaderRecord));
    }

    private static ModPrerequisiteState StateOf(IReadOnlyList<ModRecord> mods, Func<ModRecord, bool> match)
    {
        var found = false;
        foreach (var mod in mods)
        {
            if (!match(mod))
                continue;
            if (mod.Enabled)
                return ModPrerequisiteState.Ready;
            found = true;
        }
        return found ? ModPrerequisiteState.Disabled : ModPrerequisiteState.Missing;
    }
}
