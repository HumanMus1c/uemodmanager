using System.Windows.Input;
using UEManager.Core.Mods;

namespace UEManager.UI.ViewModels;

/// <summary>
/// One unmet-prerequisite warning line rendered directly beneath a disabled mod's toggle switch, pairing a
/// light-red status prefix ("Not enabled" / "Not installed") with a clickable pale-yellow prerequisite capsule.
/// </summary>
public sealed record UnmetPrerequisiteItem(
    ModPrerequisiteKind Kind,
    ModPrerequisiteState State,
    string StatusPrefix,
    string PrerequisiteName,
    string ToolTipText,
    ICommand NavigateCommand)
{
    public bool IsDisabled => State == ModPrerequisiteState.Disabled;

    public bool IsMissing => State == ModPrerequisiteState.Missing;
}
