using System.Globalization;
using UEManager.Core.Mods;

namespace UEManager.UI.Services;

internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    /// <summary>A mod's type for the player: Blueprint, UE4SS files, game files, exe files and other are translated; PAK, IoStore and UE4SS are names.</summary>
    public static string Kind(string typeLabel) => typeLabel switch
    {
        "Blueprint" => L.T("Blueprint"),
        ModTypes.Ue4ssFiles => L.T("UE4SS files"),
        ModTypes.GameFiles => L.T("Game files"),
        ModTypes.ExeFiles => L.T("Exe files"),
        ModTypes.Other => L.T("Other"),
        _ => typeLabel,
    };

    /// <summary>What each mod type badge explains on hover, reusing the openmods type descriptions.</summary>
    public static string? KindHint(string typeLabel) => typeLabel switch
    {
        "UE4SS Lua" => L.T("A script mod for UE4SS. It needs UE4SS in the game."),
        "UE4SS DLL" => L.T("A C++ mod for UE4SS. It is built for one UE4SS version and may not load with another."),
        "UE4SS Lua + DLL" => L.T("A UE4SS mod with both: scripts and a C++ part."),
        ModTypes.Ue4ssFiles => L.T("Files for a folder in UE4SS's Mods folder without code, such as the settings that another mod reads."),
        "PAK" => L.T("A .pak file that the game loads from its mods folder, in Unreal Engine 5 games often with .utoc and .ucas files beside it. It needs no UE4SS."),
        "Blueprint" => L.T("A package in the LogicMods folder, which UE4SS's Blueprint mod loader starts. It needs UE4SS."),
        ModTypes.ModLoader => L.T("A script mod for PBZModLoader, the script loader of Phantom Blade Zero. It needs PBZModLoader in the game."),
        ModTypes.GameFiles => L.T("Files that go into the game's own folders, such as a settings file or a script. The app keeps the game's own file and puts it back when the mod leaves."),
        ModTypes.ExeFiles => L.T("Files next to the game's exe, such as a proxy DLL of ReShade or of a mod loader."),
        _ => null,
    };

    /// <summary>
    /// Wide letter spacing for labels, which WPF text cannot do by itself.
    /// Letters are joined with narrow no-break spaces so a word never wraps inside.
    /// Chinese and Japanese text, which has no capitals and no spaces between words, stays as it is.
    /// </summary>
    public static string Tracked(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Any(c => c >= '⺀'))
            return text;
        var words = text.ToUpper(CultureInfo.CurrentCulture).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("  ", words.Select(word => string.Join(' ', word.ToCharArray())));
    }
}
