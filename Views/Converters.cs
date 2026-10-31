using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lucy.Views;

public sealed class IndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness((value is int n ? n : 0) * 22, 0, 0, 2);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonEmptyToVisibility : IValueConverter
{
    public static readonly NonEmptyToVisibility Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToVisibility : IValueConverter
{
    public static readonly ZeroToVisibility Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UpperConverter : IValueConverter
{
    public static readonly UpperConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? "").ToUpperInvariant();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
