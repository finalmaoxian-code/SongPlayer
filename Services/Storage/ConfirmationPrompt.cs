using System.Windows;

namespace SongPlayer.Services.Storage;

/// <summary>
/// <see cref="IConfirmationPrompt"/> backed by a standard message box.
/// </summary>
/// <remarks>
/// <c>MessageBoxResult.No</c> is the default button and <c>No</c> is also what the window's close
/// box produces, so both ways out of the dialog are the safe one. The caption wording is set by
/// the caller because only the caller knows what the user is about to lose.
/// </remarks>
public sealed class ConfirmationPrompt : IConfirmationPrompt
{
    /// <inheritdoc />
    public bool Confirm(string title, string message)
    {
        Window? owner = Application.Current?.MainWindow;

        // An owned dialog is modal to the shell and centres on it; without an owner (only
        // possible during shutdown) the overload with four arguments still works.
        MessageBoxResult result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }
}
