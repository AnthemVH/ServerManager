using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ServerLauncher.Core.Tests;

/// <summary>Marks a test that must run on the shared STA UI thread.</summary>
public sealed class WpfFactAttribute : FactAttribute
{
}

/// <summary>
/// Hosts real WPF controls so the theme can be measured as it actually renders.
///
/// Everything runs on one shared STA thread: WPF allows only a single Application per
/// process, and popup content (ComboBox dropdowns, context menus) only builds its
/// visual tree once it belongs to a window that has been shown. The window is placed
/// far off-screen so tests do not flash windows over the desktop.
/// </summary>
public static class WpfHarness
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    public sealed class Host
    {
        internal Host(Window window) => Window = window;

        internal Window Window { get; }

        public object? Content
        {
            get => ((ContentControl)Window.Content).Content;
            set => ((ContentControl)Window.Content).Content = value;
        }

        /// <summary>
        /// Runs layout and lets the dispatcher drain, so templates are applied and
        /// item containers exist before anything is measured.
        /// </summary>
        public void ForceRender()
        {
            Window.UpdateLayout();

            // Drain queued work down to Loaded priority; container generation and
            // template application are queued below Render.
            for (var i = 0; i < 3; i++)
            {
                Window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                Window.UpdateLayout();
            }
        }
    }

    /// <summary>
    /// Runs work on the shared STA thread without creating a host window, for tests
    /// that build their own windows.
    /// </summary>
    public static void RunOnUi(Action body)
    {
        var dispatcher = EnsureDispatcher();
        Exception? failure = null;

        dispatcher.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Drains queued dispatcher work so templates and bindings are applied.</summary>
    public static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        }
    }

    public static void Run(Action<Host> body)
    {
        var dispatcher = EnsureDispatcher();

        Exception? failure = null;

        dispatcher.Invoke(() =>
        {
            Window? window = null;
            try
            {
                window = new Window
                {
                    Width = 420,
                    Height = 320,
                    // Off-screen: shown (so popups work) but never visible to the user.
                    Left = -10000,
                    Top = -10000,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Background = ThemeProbe.Brush("Bg.Panel"),
                    Content = new ContentControl
                    {
                        Background = ThemeProbe.Brush("Bg.Panel"),
                        Padding = new Thickness(10)
                    }
                };

                window.Show();

                var host = new Host(window);
                host.ForceRender();

                body(host);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch (Exception)
                {
                    // Teardown only.
                }
            }
        });

        if (failure is not null)
        {
            // Preserve the original assertion message rather than wrapping it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Dispatcher EnsureDispatcher()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                // One Application per process, holding the theme so every control
                // resolves the same resources the real app does.
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(ThemeProbe.Resources);
                }

                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WpfHarness"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            ready.Wait(TimeSpan.FromSeconds(30));
            return _dispatcher ?? throw new InvalidOperationException("Failed to start the WPF harness thread.");
        }
    }
}

/// <summary>Loads the app's real theme dictionary and exposes its brushes.</summary>
public static class ThemeProbe
{
    /// <summary>
    /// Loads Theme.xaml from disk rather than from the app assembly. This keeps the
    /// audit pointed at the real source file and avoids depending on the WPF
    /// executable, which Windows Smart App Control can block from loading.
    /// </summary>
    private static readonly Lazy<ResourceDictionary> Theme = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "theme", "Theme.xaml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Theme file not found at {path}.", path);
        }

        using var stream = File.OpenRead(path);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    });

    public static ResourceDictionary Resources => Theme.Value;

    public static SolidColorBrush Brush(string key) =>
        Resources[key] as SolidColorBrush
        ?? throw new InvalidOperationException($"Theme resource '{key}' is missing or not a SolidColorBrush.");

    public static Color Color(string key) => Brush(key).Color;
}

/// <summary>Reads the colours a control actually renders with.</summary>
public static class VisualProbe
{
    /// <summary>
    /// The colour of the text inside a control, taken from the first TextBlock its
    /// template produced — that is what the user actually sees.
    /// </summary>
    public static Color EffectiveForeground(FrameworkElement element)
    {
        element.ApplyTemplate();

        var text = FindDescendant<TextBlock>(element);
        if (text?.Foreground is SolidColorBrush textBrush)
        {
            return textBrush.Color;
        }

        if (element is Control control && control.Foreground is SolidColorBrush controlBrush)
        {
            return controlBrush.Color;
        }

        throw new InvalidOperationException(
            $"Could not determine a foreground colour for {element.GetType().Name}.");
    }

    /// <summary>
    /// The surface behind a control: the nearest opaque background on the control's own
    /// template or, failing that, on an ancestor. Falls back to the supplied brush when
    /// everything in between is transparent.
    /// </summary>
    public static Color EffectiveBackground(FrameworkElement element, SolidColorBrush fallback)
    {
        element.ApplyTemplate();

        var own = FindOpaqueBackground(element, depth: 0, maxDepth: 4);
        if (own.HasValue)
        {
            return own.Value;
        }

        DependencyObject? current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (BackgroundOf(current) is { } colour)
            {
                return colour;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return fallback.Color;
    }

    private static Color? FindOpaqueBackground(DependencyObject node, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            return null;
        }

        // Skip the element itself when it is the control: its Background is often
        // Transparent while the template's root Border carries the real colour.
        if (depth > 0 && BackgroundOf(node) is { } colour)
        {
            return colour;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var found = FindOpaqueBackground(VisualTreeHelper.GetChild(node, i), depth + 1, maxDepth);
            if (found.HasValue)
            {
                return found;
            }
        }

        return null;
    }

    private static Color? BackgroundOf(DependencyObject node)
    {
        var brush = node switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control control => control.Background,
            _ => null
        };

        // Transparent surfaces let whatever is behind them show through, so they are
        // not the colour the text is actually read against.
        return brush is SolidColorBrush { Color.A: > 0 } solid && solid.Color.A > 8
            ? solid.Color
            : null;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
            {
                return match;
            }

            var deeper = FindDescendant<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }
}

/// <summary>WCAG 2.1 relative luminance and contrast ratios.</summary>
public static class Contrast
{
    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    public static double Relative(Color colour)
    {
        static double Channel(byte raw)
        {
            var c = raw / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R))
             + (0.7152 * Channel(colour.G))
             + (0.0722 * Channel(colour.B));
    }

    /// <summary>Contrast ratio between two colours, from 1:1 to 21:1.</summary>
    public static double Between(Color first, Color second)
    {
        var a = Relative(first);
        var b = Relative(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);

        return (lighter + 0.05) / (darker + 0.05);
    }
}
