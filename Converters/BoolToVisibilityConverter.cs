using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SongPlayer.Converters;

/// <summary>
/// Converts a <see cref="bool"/> into a <see cref="Visibility"/>.
/// Set <see cref="Invert"/> to <c>true</c> to collapse when the value is <c>true</c>.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// When true, the conversion result is flipped (true becomes Collapsed).
    /// </summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
