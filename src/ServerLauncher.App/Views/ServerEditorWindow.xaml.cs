using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Processes;

namespace ServerLauncher.App.Views;

/// <summary>
/// Editor for a single server. Fields are read into the definition only on save, so
/// cancelling leaves the original untouched.
/// </summary>
public partial class ServerEditorWindow : Window
{
    public ServerEditorWindow(ServerDefinition definition, bool isNew)
    {
        InitializeComponent();

        Definition = definition;
        Title = isNew ? "Add server" : $"Settings — {definition.Name}";

        RestartPolicyBox.ItemsSource = new[]
        {
            RestartPolicy.Never,
            RestartPolicy.OnCrash,
            RestartPolicy.Always
        };

        BackupModeBox.ItemsSource = new[]
        {
            BackupMode.SafeStopAndRestart,
            BackupMode.Live
        };

        Load(definition);
    }

    public ServerDefinition Definition { get; }

    private void Load(ServerDefinition d)
    {
        NameBox.Text = d.Name;
        ScriptBox.Text = d.ScriptPath;
        WorkingDirBox.Text = d.WorkingDirectory;
        ArgumentsBox.Text = d.Arguments;
        EnvironmentBox.Text = ServerDefinition.FormatEnvironment(d.EnvironmentVariables);
        AutoStartBox.IsChecked = d.AutoStartOnLaunch;

        StopCommandBox.Text = d.StopCommand;
        GraceBox.Text = d.GracefulStopTimeoutSeconds.ToString();

        RestartPolicyBox.SelectedItem = d.RestartPolicy;
        MaxRestartsBox.Text = d.MaxConsecutiveRestarts.ToString();
        StableMinutesBox.Text = d.StableUptimeMinutes.ToString();
        ScheduledRestartBox.Text = d.ScheduledRestartTime;

        BackupEnabledBox.IsChecked = d.BackupEnabled;
        BackupSourceBox.Text = d.BackupSourceFolder;
        BackupDestBox.Text = d.BackupDestinationFolder;
        BackupModeBox.SelectedItem = d.BackupMode;
        BackupTimeBox.Text = d.BackupScheduleTime;
        RetentionBox.Text = d.BackupRetentionCount.ToString();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            ValidationText.Text = error;
            return;
        }

        var d = Definition;
        d.Name = NameBox.Text.Trim();
        d.ScriptPath = ScriptBox.Text.Trim();
        d.WorkingDirectory = WorkingDirBox.Text.Trim();
        d.Arguments = ArgumentsBox.Text.Trim();
        d.EnvironmentVariables = ServerDefinition.ParseEnvironment(EnvironmentBox.Text);
        d.AutoStartOnLaunch = AutoStartBox.IsChecked == true;

        d.StopCommand = StopCommandBox.Text.Trim();
        d.GracefulStopTimeoutSeconds = ParseInt(GraceBox.Text, 30, min: 1);

        d.RestartPolicy = (RestartPolicy)(RestartPolicyBox.SelectedItem ?? RestartPolicy.OnCrash);
        d.MaxConsecutiveRestarts = ParseInt(MaxRestartsBox.Text, 5, min: 1);
        d.StableUptimeMinutes = ParseInt(StableMinutesBox.Text, 5, min: 1);
        d.ScheduledRestartTime = ScheduledRestartBox.Text.Trim();

        d.BackupEnabled = BackupEnabledBox.IsChecked == true;
        d.BackupSourceFolder = BackupSourceBox.Text.Trim();
        d.BackupDestinationFolder = BackupDestBox.Text.Trim();
        d.BackupMode = (BackupMode)(BackupModeBox.SelectedItem ?? BackupMode.SafeStopAndRestart);
        d.BackupScheduleTime = BackupTimeBox.Text.Trim();
        d.BackupRetentionCount = ParseInt(RetentionBox.Text, 5, min: 0);

        DialogResult = true;
    }

    private bool Validate(out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            error = "Give the server a name.";
            return false;
        }

        var script = ScriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(script))
        {
            error = "Choose the script that starts the server.";
            return false;
        }

        if (!File.Exists(script))
        {
            error = "That script does not exist.";
            return false;
        }

        if (!ScriptLauncher.IsSupportedScript(script))
        {
            error = "Only .bat, .cmd, .ps1 and .exe files can be launched.";
            return false;
        }

        var workingDir = WorkingDirBox.Text.Trim();
        if (workingDir.Length > 0 && !Directory.Exists(workingDir))
        {
            error = "That working directory does not exist.";
            return false;
        }

        if (!IsValidTime(ScheduledRestartBox.Text))
        {
            error = "Daily restart time must be in HH:mm form, or empty.";
            return false;
        }

        if (!IsValidTime(BackupTimeBox.Text))
        {
            error = "Daily backup time must be in HH:mm form, or empty.";
            return false;
        }

        if (BackupEnabledBox.IsChecked == true && string.IsNullOrWhiteSpace(BackupDestBox.Text))
        {
            error = "Scheduled backups need a destination folder.";
            return false;
        }

        return true;
    }

    private static bool IsValidTime(string text)
    {
        text = text.Trim();
        return text.Length == 0
            || TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture,
                                     DateTimeStyles.None, out _);
    }

    private static int ParseInt(string text, int fallback, int min)
    {
        return int.TryParse(text.Trim(), out var value) && value >= min ? value : fallback;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnBrowseScript(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the script that starts the server",
            Filter = "Server scripts (*.bat;*.cmd;*.ps1;*.exe)|*.bat;*.cmd;*.ps1;*.exe|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            ScriptBox.Text = dialog.FileName;

            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "New Server")
            {
                // Folder name is usually a better guess than "start.bat".
                var folder = Path.GetFileName(Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
                NameBox.Text = string.IsNullOrWhiteSpace(folder)
                    ? Path.GetFileNameWithoutExtension(dialog.FileName)
                    : folder;
            }
        }
    }

    private void OnBrowseWorkingDir(object sender, RoutedEventArgs e) =>
        BrowseFolder("Choose the working directory", WorkingDirBox.Text, path => WorkingDirBox.Text = path);

    private void OnBrowseBackupSource(object sender, RoutedEventArgs e) =>
        BrowseFolder("Choose the folder to back up", BackupSourceBox.Text, path => BackupSourceBox.Text = path);

    private void OnBrowseBackupDest(object sender, RoutedEventArgs e) =>
        BrowseFolder("Choose where to store archives", BackupDestBox.Text, path => BackupDestBox.Text = path);

    private void BrowseFolder(string title, string current, Action<string> assign)
    {
        var dialog = new OpenFolderDialog { Title = title };

        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            assign(dialog.FolderName);
        }
    }
}
