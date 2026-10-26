namespace SongPlayer.Services.Storage;

/// <summary>
/// Asks the user to confirm a destructive action.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="IFolderPicker"/>: the dialog is a view concern but has to be raised
/// from a command, so it sits behind an interface and <c>MessageBox</c> never appears in a view
/// model. The signature takes plain strings rather than an enum so the caller owns the wording —
/// the question belongs next to the code that knows what is about to be lost.
/// </remarks>
public interface IConfirmationPrompt
{
    /// <summary>
    /// Shows a yes/no question.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Question shown in the body.</param>
    /// <returns><see langword="true"/> when the user agreed.</returns>
    bool Confirm(string title, string message);
}
