using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ServerLauncher.Core.Models;

namespace ServerLauncher.App.Views;

/// <summary>Maps a server state to the colour of its status dot.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = Frozen("#4CAF50");
    private static readonly SolidColorBrush Transitional = Frozen("#FFB300");
    private static readonly SolidColorBrush Crashed = Frozen("#E53935");
    private static readonly SolidColorBrush Failed = Frozen("#B71C1C");
    private static readonly SolidColorBrush Stopped = Frozen("#6E7681");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ServerState state
            ? state switch
            {
                ServerState.Running => Running,
                ServerState.Starting or ServerState.Stopping => Transitional,
                ServerState.Crashed => Crashed,
                ServerState.Failed => Failed,
                _ => Stopped
            }
            : Stopped;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Colours a console line by the stream it came from.</summary>
public sealed class LogStreamToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Output = Frozen("#D4D4D4");
    private static readonly SolidColorBrush Error = Frozen("#F48771");
    private static readonly SolidColorBrush Launcher = Frozen("#6A9955");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LogStream stream
            ? stream switch
            {
                LogStream.StandardError => Error,
                LogStream.Launcher => Launcher,
                _ => Output
            }
            : Output;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>True becomes Visible; false becomes Collapsed. Invert with parameter "Invert".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null becomes Collapsed, anything else Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null becomes false, anything else true. For enabling controls.</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Substitutes a placeholder for null or empty text. TargetNullValue cannot do this:
/// the definition's string fields default to string.Empty rather than null, so a
/// TargetNullValue placeholder never fires and the field renders blank.
/// </summary>
public sealed class EmptyToPlaceholderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) ? parameter as string ?? string.Empty : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
