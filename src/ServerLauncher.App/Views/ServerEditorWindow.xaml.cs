using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServerLauncher.App.ViewModels;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Processes;

namespace ServerLauncher.App.Views;

/// <summary>
/// Editor for a single server. Fields are read into the definition only on save, so
/// cancelling leaves the original untouched.
/// </summary>
public partial class ServerEditorWindow : Window
{
    private readonly ObservableCollection<ScheduleRow> _schedule = new();

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

        ScheduleList.ItemsSource = _schedule;
        _schedule.CollectionChanged += (_, _) => RefreshScheduleEmptyState();

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
        CleanExitCodesBox.Text = string.Join(", ", d.CleanExitCodes);
        MaxRestartsBox.Text = d.MaxConsecutiveRestarts.ToString();
        StableMinutesBox.Text = d.StableUptimeMinutes.ToString();

        UpdateScriptBox.Text = d.UpdateScriptPath;
        UpdateArgumentsBox.Text = d.UpdateArguments;
        UpdateTimeoutBox.Text = d.UpdateTimeoutMinutes.ToString();
        RunUpdateBeforeStartBox.IsChecked = d.RunUpdateBeforeStart;

        // Migrated on load by ServerManager, so an old daily restart time is already a
        // schedule entry by the time the editor sees it.
        _schedule.Clear();
        foreach (var task in d.Schedule)
        {
            _schedule.Add(new ScheduleRow(task));
        }

        RefreshScheduleEmptyState();

        BackupSourceBox.Text = d.BackupSourceFolder;
        BackupDestBox.Text = d.BackupDestinationFolder;
        BackupModeBox.SelectedItem = d.BackupMode;
        RetentionBox.Text = d.BackupRetentionCount.ToString();
    }

    private void RefreshScheduleEmptyState() =>
        NoScheduleText.Visibility = _schedule.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddScheduleEntry(object sender, RoutedEventArgs e)
    {
        // Defaults to the commonest thing anyone adds a schedule for.
        _schedule.Add(new ScheduleRow(new ScheduledTask
        {
            Action = ScheduledAction.Restart,
            Time = "05:00",
            Days = ScheduleDays.EveryDay
        }));
    }

    private void OnRemoveScheduleEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id })
        {
            return;
        }

        var row = _schedule.FirstOrDefault(r => r.Id == id);
        if (row is not null)
        {
            _schedule.Remove(row);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Apply(out var error))
        {
            ValidationText.Text = error;
            return;
        }

        DialogResult = true;
    }

    /// <summary>
    /// Validates the form and writes every field into <see cref="Definition"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the Save button so the round trip can be tested: DialogResult can
    /// only be set on a window shown with ShowDialog, which a headless test cannot do.
    /// </remarks>
    /// <returns>False if the form is not valid, in which case nothing is written.</returns>
    public bool Apply(out string error)
    {
        if (!Validate(out error))
        {
            return false;
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
        d.CleanExitCodes = ParseExitCodes(CleanExitCodesBox.Text);
        d.MaxConsecutiveRestarts = ParseInt(MaxRestartsBox.Text, 5, min: 1);
        d.StableUptimeMinutes = ParseInt(StableMinutesBox.Text, 5, min: 1);

        d.UpdateScriptPath = UpdateScriptBox.Text.Trim();
        d.UpdateArguments = UpdateArgumentsBox.Text.Trim();
        d.UpdateTimeoutMinutes = ParseInt(UpdateTimeoutBox.Text, 30, min: 1);
        d.RunUpdateBeforeStart = RunUpdateBeforeStartBox.IsChecked == true;

        d.Schedule = _schedule.Select(row => row.ToTask()).ToList();

        // The old single-time fields are dead once a schedule exists; clearing them here
        // stops a stale value being migrated back in on the next load.
        d.ScheduledRestartTime = string.Empty;
        d.BackupScheduleTime = string.Empty;

        d.BackupSourceFolder = BackupSourceBox.Text.Trim();
        d.BackupDestinationFolder = BackupDestBox.Text.Trim();
        d.BackupMode = (BackupMode)(BackupModeBox.SelectedItem ?? BackupMode.SafeStopAndRestart);
        d.BackupRetentionCount = ParseInt(RetentionBox.Text, 5, min: 0);

        return true;
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

        var updateScript = UpdateScriptBox.Text.Trim();
        if (updateScript.Length > 0)
        {
            if (!File.Exists(updateScript))
            {
                error = "That update script does not exist.";
                return false;
            }

            if (!ScriptLauncher.IsSupportedScript(updateScript))
            {
                error = "An update script must be a .bat, .cmd, .ps1 or .exe file.";
                return false;
            }
        }

        if (RunUpdateBeforeStartBox.IsChecked == true && updateScript.Length == 0)
        {
            error = "Choose an update script, or untick running it before every start.";
            return false;
        }

        // A bad time is silently never due, so it has to be caught here rather than
        // saved as an entry that looks configured and does nothing.
        if (_schedule.FirstOrDefault(row => !row.HasUsableTime) is { } badTime)
        {
            error = $"The {ScheduledTask.DescribeAction(badTime.Action).ToLowerInvariant()} "
                    + "schedule entry needs a time in HH:mm form, such as 05:00.";
            return false;
        }

        if (_schedule.FirstOrDefault(row => row.ToTask().Days == ScheduleDays.None) is { } noDays)
        {
            error = $"The {ScheduledTask.DescribeAction(noDays.Action).ToLowerInvariant()} "
                    + "schedule entry needs at least one day ticked.";
            return false;
        }

        if (_schedule.Any(row => row.Action == ScheduledAction.RunUpdate) && updateScript.Length == 0)
        {
            error = "A schedule entry runs the update script, but no update script is set.";
            return false;
        }

        if (_schedule.Any(row => row.Action == ScheduledAction.Backup)
            && string.IsNullOrWhiteSpace(BackupDestBox.Text))
        {
            error = "A schedule entry runs a backup, so backups need a destination folder.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a comma or space separated list of exit codes, ignoring anything that is
    /// not a number rather than rejecting the whole field.
    /// </summary>
    private static List<int> ParseExitCodes(string text) =>
        text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out var code) ? code : (int?)null)
            .Where(code => code.HasValue)
            .Select(code => code!.Value)
            .Distinct()
            .ToList();

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

    private void OnBrowseUpdateScript(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the script that updates this server",
            Filter = "Update scripts (*.bat;*.cmd;*.ps1;*.exe)|*.bat;*.cmd;*.ps1;*.exe|All files (*.*)|*.*"
        };

        // Most update scripts sit next to the start script.
        var startScript = ScriptBox.Text.Trim();
        if (startScript.Length > 0 && Path.GetDirectoryName(startScript) is { } folder
            && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            UpdateScriptBox.Text = dialog.FileName;
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
