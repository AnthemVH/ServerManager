using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Readability audit for the dark theme.
///
/// A control styled with only Background/Foreground setters keeps WPF's default
/// template, which hard-codes light chrome for popups, dropdown rows and menus — the
/// result is pale text on a pale background. These tests apply the real theme, render
/// each control, read the brushes that actually end up on screen, and assert the
/// contrast between text and the surface behind it clears the WCAG AA threshold.
/// </summary>
public class ThemeContrastTests
{
    /// <summary>WCAG AA minimum for normal-size body text.</summary>
    private const double MinimumContrast = 4.5;

    /// <summary>WCAG AA minimum for large or de-emphasised text such as hints.</summary>
    private const double MinimumContrastLargeText = 3.0;

    // --- ComboBox: the case that was actually broken ---

    [WpfFact]
    public void ComboBoxDropDownItems_AreReadableInEveryState()
    {
        WpfHarness.Run(host =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "Never", "OnCrash", "Always" } };
            host.Content = combo;
            host.ForceRender();

            combo.IsDropDownOpen = true;
            host.ForceRender();

            var popupSurface = ThemeProbe.Brush("Bg.Popup");

            for (var i = 0; i < 3; i++)
            {
                var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(i);
                item.Should().NotBeNull("the dropdown must generate its item containers");

                // Unselected row sits directly on the popup surface.
                item.ApplyTemplate();
                host.ForceRender();

                var background = VisualProbe.EffectiveBackground(item, popupSurface);
                var foreground = VisualProbe.EffectiveForeground(item);

                Contrast.Between(foreground, background)
                    .Should().BeGreaterThanOrEqualTo(MinimumContrast,
                        $"dropdown row '{combo.Items[i]}' must be legible against the popup");
            }

            combo.IsDropDownOpen = false;
        });
    }

    [WpfFact]
    public void ComboBoxSelectedItem_IsReadableAgainstTheHighlight()
    {
        WpfHarness.Run(host =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "Never", "OnCrash", "Always" } };
            host.Content = combo;
            host.ForceRender();

            combo.IsDropDownOpen = true;
            host.ForceRender();

            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
            item.IsSelected = true;
            item.ApplyTemplate();
            host.ForceRender();

            var background = VisualProbe.EffectiveBackground(item, ThemeProbe.Brush("Bg.Popup"));
            var foreground = VisualProbe.EffectiveForeground(item);

            background.Should().NotBe(Colors.White,
                "the selected row must not fall back to the default light highlight");

            Contrast.Between(foreground, background)
                .Should().BeGreaterThanOrEqualTo(MinimumContrast,
                    "the highlighted row is the one the user is reading");

            combo.IsDropDownOpen = false;
        });
    }

    [WpfFact]
    public void ComboBoxClosedState_ShowsTheSelectionLegibly()
    {
        WpfHarness.Run(host =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "Never", "OnCrash" }, SelectedIndex = 0 };
            host.Content = combo;
            host.ForceRender();

            var foreground = VisualProbe.EffectiveForeground(combo);
            var background = VisualProbe.EffectiveBackground(combo, ThemeProbe.Brush("Bg.Panel"));

            Contrast.Between(foreground, background).Should().BeGreaterThanOrEqualTo(MinimumContrast);
        });
    }

    // --- Every other control the app uses ---

    [WpfFact]
    public void TextBox_IsReadableWhenEnabledAndDisabled()
    {
        WpfHarness.Run(host =>
        {
            var box = new TextBox { Text = "C:\\servers\\start.bat" };
            host.Content = box;
            host.ForceRender();

            Contrast.Between(VisualProbe.EffectiveForeground(box),
                             VisualProbe.EffectiveBackground(box, ThemeProbe.Brush("Bg.Panel")))
                .Should().BeGreaterThanOrEqualTo(MinimumContrast, "an editable path must be readable");

            // The console command box is disabled whenever the server is stopped.
            box.IsEnabled = false;
            host.ForceRender();

            Contrast.Between(VisualProbe.EffectiveForeground(box),
                             VisualProbe.EffectiveBackground(box, ThemeProbe.Brush("Bg.Panel")))
                .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText,
                    "a disabled box should read as muted, not vanish");
        });
    }

    [WpfFact]
    public void CheckBox_LabelIsReadable()
    {
        WpfHarness.Run(host =>
        {
            var check = new CheckBox { Content = "Start this server when the launcher starts" };
            host.Content = check;
            host.ForceRender();

            Contrast.Between(VisualProbe.EffectiveForeground(check), ThemeProbe.Color("Bg.Panel"))
                .Should().BeGreaterThanOrEqualTo(MinimumContrast);
        });
    }

    [WpfFact]
    public void MenuItems_AreReadableIncludingDisabledStatusRows()
    {
        WpfHarness.Run(host =>
        {
            var menu = new ContextMenu();
            var enabled = new MenuItem { Header = "Open Server Launcher" };
            var disabled = new MenuItem { Header = "Running", IsEnabled = false };
            menu.Items.Add(enabled);
            menu.Items.Add(disabled);

            // A ContextMenu renders in its own popup; opening it generates the templates.
            host.Content = new Border { ContextMenu = menu };
            host.ForceRender();
            menu.IsOpen = true;
            host.ForceRender();

            var surface = ThemeProbe.Color("Bg.Popup");

            Contrast.Between(VisualProbe.EffectiveForeground(enabled), surface)
                .Should().BeGreaterThanOrEqualTo(MinimumContrast, "tray menu commands must be readable");

            // Status rows are disabled on purpose, but must still be legible.
            Contrast.Between(VisualProbe.EffectiveForeground(disabled), surface)
                .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText,
                    "the disabled status row still conveys information");

            menu.IsOpen = false;
        });
    }

    [WpfFact]
    public void ListBoxItems_AreReadableSelectedAndUnselected()
    {
        WpfHarness.Run(host =>
        {
            var list = new ListBox { ItemsSource = new[] { "Demo Server", "Second Server" } };
            host.Content = list;
            host.ForceRender();

            var unselected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Contrast.Between(VisualProbe.EffectiveForeground(unselected),
                             VisualProbe.EffectiveBackground(unselected, ThemeProbe.Brush("Bg.Panel")))
                .Should().BeGreaterThanOrEqualTo(MinimumContrast);

            list.SelectedIndex = 0;
            host.ForceRender();

            Contrast.Between(VisualProbe.EffectiveForeground(unselected),
                             VisualProbe.EffectiveBackground(unselected, ThemeProbe.Brush("Bg.Panel")))
                .Should().BeGreaterThanOrEqualTo(MinimumContrast, "the selected server must stay readable");
        });
    }

    [WpfFact]
    public void TabHeaders_AreReadableSelectedAndUnselected()
    {
        WpfHarness.Run(host =>
        {
            var tabs = new TabControl();
            var console = new TabItem { Header = "Console" };
            var monitoring = new TabItem { Header = "Monitoring" };
            tabs.Items.Add(console);
            tabs.Items.Add(monitoring);
            host.Content = tabs;
            host.ForceRender();

            // Selected tab sits on the panel colour, unselected on the window colour.
            Contrast.Between(VisualProbe.EffectiveForeground(console), ThemeProbe.Color("Bg.Panel"))
                .Should().BeGreaterThanOrEqualTo(MinimumContrast, "the active tab must be readable");

            Contrast.Between(VisualProbe.EffectiveForeground(monitoring), ThemeProbe.Color("Bg.Window"))
                .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText,
                    "an inactive tab is de-emphasised but must still be legible");
        });
    }

    [WpfFact]
    public void Buttons_AreReadableInEachVariant()
    {
        WpfHarness.Run(host =>
        {
            foreach (var styleKey in new[] { "ToolButton", "PrimaryButton", "DangerButton" })
            {
                var button = new Button
                {
                    Content = "Start",
                    Style = (Style)ThemeProbe.Resources[styleKey]
                };

                host.Content = button;
                host.ForceRender();

                var background = VisualProbe.EffectiveBackground(button, ThemeProbe.Brush("Bg.Panel"));
                var foreground = VisualProbe.EffectiveForeground(button);

                Contrast.Between(foreground, background)
                    .Should().BeGreaterThanOrEqualTo(MinimumContrast, $"{styleKey} label must be readable");
            }
        });
    }

    [WpfFact]
    public void TextStyles_MeetContrastAgainstTheirPanels()
    {
        WpfHarness.Run(_ =>
        {
            var panel = ThemeProbe.Color("Bg.Panel");

            Contrast.Between(ThemeProbe.Color("Fg.Primary"), panel)
                .Should().BeGreaterThanOrEqualTo(MinimumContrast, "body text");

            Contrast.Between(ThemeProbe.Color("Fg.Secondary"), panel)
                .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText, "labels");

            Contrast.Between(ThemeProbe.Color("Fg.Muted"), panel)
                .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText, "hint text");
        });
    }

    [WpfFact]
    public void ConsoleStreamColours_AreReadableOnTheConsoleBackground()
    {
        WpfHarness.Run(_ =>
        {
            var console = ThemeProbe.Color("Bg.Console");

            // These are the literals used by LogStreamToBrushConverter.
            var streams = new (string Name, Color Colour)[]
            {
                ("stdout", (Color)ColorConverter.ConvertFromString("#D4D4D4")),
                ("stderr", (Color)ColorConverter.ConvertFromString("#F48771")),
                ("launcher", (Color)ColorConverter.ConvertFromString("#6A9955"))
            };

            foreach (var (name, colour) in streams)
            {
                Contrast.Between(colour, console)
                    .Should().BeGreaterThanOrEqualTo(MinimumContrastLargeText,
                        $"{name} output must be readable in the console");
            }
        });
    }

    [WpfFact]
    public void NoControlFallsBackToWindowsDefaultLightChrome()
    {
        // Regression guard for the original bug: a control left with only colour setters
        // keeps its default template, and the light surfaces reappear.
        WpfHarness.Run(host =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "Never" } };
            host.Content = combo;
            host.ForceRender();
            combo.IsDropDownOpen = true;
            host.ForceRender();

            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
            var background = VisualProbe.EffectiveBackground(item, ThemeProbe.Brush("Bg.Popup"));

            Contrast.Relative(background).Should().BeLessThan(0.5,
                "every surface in this app should be dark; a light one means a default template leaked through");

            combo.IsDropDownOpen = false;
        });
    }
}
