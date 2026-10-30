using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DuplicadosApp.Converters;

/// <summary>Invierte un valor booleano sin hacer boxing en cada evaluación.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    private static readonly object TrueBox = true;
    private static readonly object FalseBox = false;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && !flag ? TrueBox : FalseBox;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && !flag ? TrueBox : FalseBox;
}

/// <summary>Muestra el elemento cuando el valor booleano es false.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    private static readonly object VisibleBox = Visibility.Visible;
    private static readonly object CollapsedBox = Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag && flag ? CollapsedBox : VisibleBox;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}