using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenMacro.App;

/// <summary>
/// A length minus a fixed reservation, floored at zero. The sidebar name's
/// MaxWidth is the label block's width less the room its app icon needs, so
/// the name's column shrink-wraps while the name is short — the icon stays
/// beside it — and stops growing once it isn't, which is what gives
/// TextTrimming something to trim against. One way only: the source is a
/// read-only ActualWidth.
/// </summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double length && parameter is double reserved
            ? Math.Max(0, length - reserved)
            : DependencyProperty.UnsetValue;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
