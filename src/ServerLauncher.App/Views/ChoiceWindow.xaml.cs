using System.Windows;
using System.Windows.Controls;

namespace ServerLauncher.App.Views;

/// <summary>
/// A themed question with buttons that say what they do.
/// </summary>
/// <remarks>
/// MessageBox only offers Yes/No/Cancel, which forces "Yes means just this day, No means
/// every day" into the message text — exactly the kind of mapping people get backwards
/// when they are deleting something.
/// </remarks>
public partial class ChoiceWindow : Window
{
    /// <summary>A button: its label, and whether it is the destructive option.</summary>
    public sealed record Choice(string Label, bool IsDanger = false);

    private ChoiceWindow(string title, string message, IReadOnlyList<Choice> choices)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;

        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var choice = choices[i];

            var button = new Button
            {
                Content = choice.Label,
                Style = (Style)FindResource(choice.IsDanger ? "DangerButton" : "ToolButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };

            button.Click += (_, _) =>
            {
                SelectedIndex = index;
                DialogResult = true;
            };

            ButtonPanel.Children.Add(button);
        }

        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("ToolButton"),
            IsCancel = true,
            Margin = new Thickness(0)
        };

        ButtonPanel.Children.Add(cancel);
    }

    /// <summary>Which choice was picked, or -1 for cancel.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Shows the question and returns the index of the choice, or -1 if cancelled.</summary>
    public static int Ask(Window? owner, string title, string message, params Choice[] choices)
    {
        var window = new ChoiceWindow(title, message, choices) { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedIndex : -1;
    }
}
