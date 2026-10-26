namespace SongPlayer.Models;

/// <summary>
/// The top level sections hosted in the main content area.
/// The immersive Now Playing view is deliberately not part of this enum: it is an overlay
/// that covers the shell instead of being one of its navigation targets.
/// </summary>
public enum PlayerPage
{
    /// <summary>Landing page with a hero banner and quick access cards.</summary>
    Home,

    /// <summary>The user's local music library.</summary>
    Library
}
