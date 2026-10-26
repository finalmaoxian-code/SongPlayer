namespace SongPlayer.Services.Storage;

/// <summary>
/// Asks the user for a folder on disk.
/// </summary>
/// <remarks>
/// The dialog itself is a view concern, but it has to be opened from a command. Hiding it behind
/// an interface keeps <c>Microsoft.Win32</c> out of the view models, so they stay unit-testable
/// and the picker can be replaced (a saved-folder list, a drag-and-drop target, tests).
/// </remarks>
public interface IFolderPicker
{
    /// <summary>
    /// Shows the folder picker.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <returns>
    /// The chosen absolute folder path, or <see langword="null"/> when the user cancelled.
    /// </returns>
    string? PickFolder(string title);
}
