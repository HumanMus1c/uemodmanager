using UEManager.Core.Mods;
using UEManager.UI.Services;

namespace UEManager.UI.ViewModels;

/// <summary>One prerequisite dependency capsule badge shown on an installed mod row and in its details dialog.</summary>
public sealed record ModDependencyBadge(string Label, string Tone, string Glyph, string Hint, string SearchTerms)
{
    /// <summary>Canonical name of a prerequisite runtime or loader.</summary>
    public static string PrerequisiteName(ModPrerequisiteKind kind) => ModPrerequisites.NameOf(kind);

    /// <summary>Localized tooltip explaining why a mod's switch is clickable or locked by unmet prerequisites.</summary>
    public static string ToggleHint(ModDependencyInfo info)
    {
        var unmet = info.Unmet;
        if (unmet.Count == 0)
            return L.T("Enable or disable");
        if (unmet.Count == 1)
        {
            var name = PrerequisiteName(unmet[0].Kind);
            return unmet[0].State == ModPrerequisiteState.Disabled
                ? L.F($"Cannot enable: {name} is disabled. Enable {name} first.")
                : L.F($"Cannot enable: {name} is not installed.");
        }
        var names = string.Join(", ", unmet.Select(u => PrerequisiteName(u.Kind)));
        return L.F($"Cannot enable: missing or disabled prerequisites ({names}).");
    }

    /// <summary>Builds localized capsule badge(s) describing a mod's prerequisite dependencies and readiness.</summary>
    public static IReadOnlyList<ModDependencyBadge> Build(ModRecord record, ModDependencyInfo info)
    {
        if (info.IsLoader)
        {
            return
            [
                new ModDependencyBadge(
                    L.T("Prerequisite loader"),
                    "good",
                    "\uE81E",
                    L.T("This mod is a loader or framework that other mods depend on; it needs no other mod loader."),
                    "Loader Prerequisite loader"),
            ];
        }
        if (info.IsStandalone)
        {
            var hint = record.TypeLabel == "PAK"
                ? L.T("Packaged mod (.pak). Loaded directly by the game without UE4SS or any script loader.")
                : L.T("Does not require UE4SS or a script loader.");
            return
            [
                new ModDependencyBadge(
                    L.T("No prerequisite"),
                    "muted",
                    "\uE73E",
                    hint,
                    "Standalone No prerequisite"),
            ];
        }
        return info.Prerequisites.Select(p => ForPrerequisite(record, p)).ToList();
    }

    private static ModDependencyBadge ForPrerequisite(ModRecord record, ModPrerequisite prerequisite)
    {
        var name = PrerequisiteName(prerequisite.Kind);
        return prerequisite.State switch
        {
            ModPrerequisiteState.Ready => new ModDependencyBadge(
                L.F($"Needs {name}"),
                "info",
                "\uE73E",
                prerequisite.Kind == ModPrerequisiteKind.Ue4ss && ModTypes.HasUe4ssDll(record.Files.Select(f => (f.Target, f.FileName)))
                    ? L.F($"Requires {name} (installed). Note: C++ DLL mods are built for one UE4SS version and may not load with another.")
                    : L.F($"Requires {name} (installed and ready)."),
                $"{name} Ready"),
            ModPrerequisiteState.Disabled => new ModDependencyBadge(
                L.F($"{name} · disabled"),
                "warn",
                "\uE7BA",
                L.F($"Requires {name}, which is installed in your mod list but currently disabled."),
                $"{name} Disabled Missing"),
            _ => new ModDependencyBadge(
                L.F($"{name} · missing"),
                "bad",
                "\uE7BA",
                MissingHint(prerequisite.Kind, name),
                $"{name} Missing"),
        };
    }

    private static string MissingHint(ModPrerequisiteKind kind, string name) => kind switch
    {
        ModPrerequisiteKind.Ue4ss => L.T("Requires UE4SS, which is not installed in this game. Open the UE4SS page to install it."),
        ModPrerequisiteKind.ModsMenu => L.T("Requires Mods Menu, which is not installed in this game."),
        _ => L.F($"Requires {name}, which is not installed in this game. Install it from the top of the Mods page or from openmods."),
    };
}
