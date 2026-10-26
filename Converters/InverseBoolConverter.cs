using System.Globalization;
using System.Windows.Data;

namespace SongPlayer.Converters;

/// <summary>
/// Converts a <see cref="bool"/> into its opposite.
/// </summary>
/// <remarks>
/// Used for <c>IsEnabled="{Binding IsBusy, Converter=...}"</c>: WPF has no built-in negation, and
/// a <c>Style</c> trigger per button would spread the same rule over three files.
/// </remarks>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
