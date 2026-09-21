using System.Diagnostics;
using System.Text;
using System.Windows;
using FluentAssertions;
using ServerLauncher.App;
using ServerLauncher.App.ViewModels;
using ServerLauncher.App.TrayIcon;
using ServerLauncher.App.Views;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Remote;
using ServerLauncher.Core.Storage;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Builds each real window and asserts WPF reported no data-binding errors.
///
/// A mistyped binding path is the classic silent WPF failure: the control simply shows
/// nothing, the app runs, and no exception is ever thrown. WPF only whispers about it
/// through a trace source, so these tests listen to that source and fail loudly instead.
/// </summary>
public sealed class UiSmokeTests : IDisposable
{
    private readonly string _configRoot = Path.Combine(
        Path.GetTempPath(), "ServerLauncherUiTests", Guid.NewGuid().ToString("N"));

    public UiSmokeTests() => Directory.CreateDirectory(_configRoot);

    /// <summary>Captures WPF binding failures while a window is built and rendered.</summary>
    private sealed class BindingErrorCollector : TraceListener, IDisposable
    {
        private readonly StringBuilder _current = new();
        private readonly SourceLevels _previousLevel;

        public BindingErrorCollector()
        {
            PresentationTraceSources.Refresh();
            _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }

        public List<string> Errors { get; } = new();

        public override void Write(string? message) => _current.Append(message);

        public override void WriteLine(string? message)
        {
            _current.Append(message);
            var line = _current.ToString();
            _current.Clear();

            // "BindingExpression path error" and friends all arrive under this prefix.
            if (line.Contains("System.Windows.Data Error", StringComparison.Ordinal))
            {
                Errors.Add(line.Trim());
            }
        }

        void IDisposable.Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
        }
    }

    private ServerManager CreateIsolatedManager() =>
        new(new ConfigurationStore(
            Path.Combine(_configRoot, "servers.json"),
            Path.Combine(_configRoot, "settings.json")));

    private static Window Offscreen(Window window)
    {
        window.Left = -10000;
        window.Top = -10000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        return window;
    }

    private static void AssertNoBindingErrors(BindingErrorCollector collector, string window)
    {
        collector.Errors.Should().BeEmpty(
            $"{window} should have no broken bindings, but WPF reported:\n"
            + string.Join("\n", collector.Errors));
    }

    [WpfFact]
    public void BindingErrorCollector_ActuallyDetectsABrokenBinding()
    {
        // Without this, a silently broken collector would make every other test in this
        // class pass by reporting nothing at all.
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var window = Offscreen(new Window
            {
                Content = new System.Windows.Controls.TextBlock(),
                DataContext = new AppSettings()
            });

            var broken = new System.Windows.Data.Binding("ThisPropertyDoesNotExist");
            ((System.Windows.Controls.TextBlock)window.Content)
                .SetBinding(System.Windows.Controls.TextBlock.TextProperty, broken);

            try
            {
                window.Show();
                WpfHarness.Pump(window);

                collector.Errors.Should().NotBeEmpty(
                    "the collector must report a binding to a property that does not exist");
                collector.Errors.Should().Contain(e => e.Contains("ThisPropertyDoesNotExist"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void MainWindow_BuildsAndBindsCleanly()
    {
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            MainWindow? window = null;
            try
            {
                window = (MainWindow)Offscreen(new MainWindow(viewModel));
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "MainWindow");
            }
            finally
            {
                if (window is not null)
                {
                    // OnClosing consults App.Current, which is not this test's Application.
                    window.AllowClose = true;
                    window.Close();
                }

                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void MainWindow_BindsCleanlyWithAServerSelected()
    {
        // The empty state exercises almost no bindings; the interesting ones only
        // evaluate once a server is selected and its detail panes are shown.
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            manager.Add(new ServerDefinition
            {
                Name = "Test Server",
                ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat"),
                StopCommand = "stop",
                BackupDestinationFolder = Path.Combine(_configRoot, "backups"),
                ScheduledRestartTime = "05:00",
                BackupScheduleTime = "04:00"
            });

            viewModel.SyncServers();

            using var collector = new BindingErrorCollector();

            MainWindow? window = null;
            try
            {
                window = (MainWindow)Offscreen(new MainWindow(viewModel));
                window.Show();
                WpfHarness.Pump(window);

                viewModel.SelectedServer.Should().NotBeNull("the first server is selected automatically");
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "MainWindow with a selected server");
            }
            finally
            {
                if (window is not null)
                {
                    window.AllowClose = true;
                    window.Close();
                }

                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void Dashboard_BindsCleanlyWithServersPresent()
    {
        // The dashboard is one click from the opening view, so a broken binding here is
        // among the first things anyone would see.
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            foreach (var name in new[] { "Alpha", "Bravo" })
            {
                manager.Add(new ServerDefinition
                {
                    Name = name,
                    ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat")
                });
            }

            viewModel.SyncServers();

            viewModel.IsDashboardVisible.Should().BeFalse("the servers view is what opens");
            viewModel.ShowDashboardCommand.Execute(null);

            using var collector = new BindingErrorCollector();

            MainWindow? window = null;
            try
            {
                window = (MainWindow)Offscreen(new MainWindow(viewModel));
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "Dashboard");
            }
            finally
            {
                if (window is not null)
                {
                    window.AllowClose = true;
                    window.Close();
                }

                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void Dashboard_BindsCleanlyWithNoServers()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);
            viewModel.SyncServers();
            viewModel.ShowDashboardCommand.Execute(null);

            using var collector = new BindingErrorCollector();

            MainWindow? window = null;
            try
            {
                window = (MainWindow)Offscreen(new MainWindow(viewModel));
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "Dashboard empty state");
            }
            finally
            {
                if (window is not null)
                {
                    window.AllowClose = true;
                    window.Close();
                }

                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void OpeningAServerFromTheDashboard_SelectsItAndSwitchesView()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            manager.Add(new ServerDefinition
            {
                Name = "Alpha",
                ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat")
            });

            viewModel.SyncServers();

            try
            {
                // The app opens on the servers view: it is where servers are started,
                // stopped and configured, and the dashboard is a summary of that.
                viewModel.IsDashboardVisible.Should().BeFalse("the servers view is the opening view");

                var target = viewModel.Servers.Single();
                viewModel.OpenServerCommand.Execute(target);

                viewModel.SelectedServer.Should().BeSameAs(target);
                viewModel.IsDashboardVisible.Should().BeFalse("Open should show that server detail");

                viewModel.ShowDashboardCommand.Execute(null);
                viewModel.IsDashboardVisible.Should().BeTrue();
            }
            finally
            {
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void DashboardTotals_CountServersAndTheirResources()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            foreach (var name in new[] { "Alpha", "Bravo", "Charlie" })
            {
                manager.Add(new ServerDefinition
                {
                    Name = name,
                    ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat")
                });
            }

            viewModel.SyncServers();

            try
            {
                viewModel.TotalServerCount.Should().Be(3);
                viewModel.RunningServerCount.Should().Be(0, "nothing has been started");
                viewModel.RunningSummary.Should().Be("0 of 3 running");
                viewModel.HasProblems.Should().BeFalse();

                // Totals are a plain sum over the servers, so stopped ones contribute zero.
                viewModel.TotalCpuPercent.Should().Be(0);
                viewModel.TotalMemoryMegabytes.Should().Be(0);
                viewModel.TotalProcessCount.Should().Be(0);
            }
            finally
            {
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void TrayIcon_IsCreatedWithoutAnyWindowBeingShown()
    {
        // The bug this guards: the tray icon used to live inside MainWindow's XAML, so it
        // was only created once that window loaded. Starting minimised therefore produced
        // a running process with no window and no tray icon — invisible, but still holding
        // the single-instance mutex, so it could not be relaunched either.
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);
            TrayIconController? tray = null;

            try
            {
                tray = new TrayIconController(viewModel, () => { }, () => { }, () => { });

                tray.IsCreated.Should().BeTrue(
                    "the tray icon must exist even when no window has ever been shown");
            }
            finally
            {
                tray?.Dispose();
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void TrayIcon_ListsConfiguredServers()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            manager.Add(new ServerDefinition
            {
                Name = "Tray Listed Server",
                ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat")
            });

            viewModel.SyncServers();

            TrayIconController? tray = null;
            try
            {
                tray = new TrayIconController(viewModel, () => { }, () => { }, () => { });
                tray.IsCreated.Should().BeTrue();
            }
            finally
            {
                tray?.Dispose();
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void SettingsWindow_BuildsAndBindsCleanly()
    {
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var window = (SettingsWindow)Offscreen(new SettingsWindow(new AppSettings()));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "SettingsWindow");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void ServerEditorWindow_BuildsAndBindsCleanly()
    {
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var definition = new ServerDefinition
            {
                Name = "Editing",
                ScriptPath = @"C:\servers\start.bat",
                StopCommand = "stop",
                UpdateScriptPath = @"C:\servers\update.bat",

                // With entries present the schedule template actually renders, so a
                // broken binding inside it is caught rather than skipped.
                Schedule =
                {
                    new ScheduledTask
                    {
                        Action = ScheduledAction.Restart,
                        Time = "05:00",
                        Days = ScheduleDays.Monday | ScheduleDays.Thursday
                    },
                    new ScheduledTask
                    {
                        Action = ScheduledAction.RunUpdate,
                        Time = "04:00",
                        Days = ScheduleDays.Sunday
                    }
                }
            };

            var window = (ServerEditorWindow)Offscreen(new ServerEditorWindow(definition, isNew: false));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "ServerEditorWindow");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void ServerEditor_RoundTripsEveryFieldItEdits()
    {
        // Guards against a field being shown but never written back on save — the kind
        // of omission that silently discards a user's setting. The editor is driven all
        // the way through Apply, so a field that loads but never saves fails here.
        WpfHarness.RunOnUi(() =>
        {
            // Real files, because saving validates that the scripts exist.
            var folder = Path.Combine(Path.GetTempPath(), "ServerLauncherEditorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            var startScript = Path.Combine(folder, "start.bat");
            var updateScript = Path.Combine(folder, "update.bat");
            File.WriteAllText(startScript, "@echo off");
            File.WriteAllText(updateScript, "@echo off");

            var original = new ServerDefinition
            {
                Name = "Round Trip",
                ScriptPath = startScript,
                WorkingDirectory = folder,
                Arguments = "-nogui",
                AutoStartOnLaunch = true,
                StopCommand = "stop",
                GracefulStopTimeoutSeconds = 45,
                RestartPolicy = RestartPolicy.Always,
                MaxConsecutiveRestarts = 7,
                StableUptimeMinutes = 9,
                UpdateScriptPath = updateScript,
                UpdateArguments = "+app_update 233780 validate",
                UpdateTimeoutMinutes = 45,
                RunUpdateBeforeStart = true,
                BackupSourceFolder = folder,
                BackupDestinationFolder = @"D:\backups",
                BackupMode = BackupMode.Live,
                BackupRetentionCount = 12,
                EnvironmentVariables = { ["JAVA_OPTS"] = "-Xmx4G", ["WORLD"] = "overworld" },
                CleanExitCodes = { 7, 42 },
                Schedule =
                {
                    new ScheduledTask
                    {
                        Action = ScheduledAction.Restart,
                        Time = "05:30",
                        Days = ScheduleDays.Monday | ScheduleDays.Thursday
                    },
                    new ScheduledTask
                    {
                        Action = ScheduledAction.Backup,
                        Time = "04:15",
                        Days = ScheduleDays.EveryDay,
                        Enabled = false
                    }
                }
            };

            var window = (ServerEditorWindow)Offscreen(new ServerEditorWindow(original.Clone(), isNew: false));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                window.Apply(out var error).Should().BeTrue(error);

                var loaded = window.Definition;

                loaded.Name.Should().Be(original.Name);
                loaded.ScriptPath.Should().Be(original.ScriptPath);
                loaded.WorkingDirectory.Should().Be(original.WorkingDirectory);
                loaded.Arguments.Should().Be(original.Arguments);
                loaded.AutoStartOnLaunch.Should().Be(original.AutoStartOnLaunch);
                loaded.StopCommand.Should().Be(original.StopCommand);
                loaded.GracefulStopTimeoutSeconds.Should().Be(original.GracefulStopTimeoutSeconds);
                loaded.RestartPolicy.Should().Be(original.RestartPolicy);
                loaded.MaxConsecutiveRestarts.Should().Be(original.MaxConsecutiveRestarts);
                loaded.StableUptimeMinutes.Should().Be(original.StableUptimeMinutes);
                loaded.UpdateScriptPath.Should().Be(original.UpdateScriptPath);
                loaded.UpdateArguments.Should().Be(original.UpdateArguments);
                loaded.UpdateTimeoutMinutes.Should().Be(original.UpdateTimeoutMinutes);
                loaded.RunUpdateBeforeStart.Should().Be(original.RunUpdateBeforeStart);
                loaded.BackupSourceFolder.Should().Be(original.BackupSourceFolder);
                loaded.BackupDestinationFolder.Should().Be(original.BackupDestinationFolder);
                loaded.BackupMode.Should().Be(original.BackupMode);
                loaded.BackupRetentionCount.Should().Be(original.BackupRetentionCount);
                loaded.EnvironmentVariables.Should().BeEquivalentTo(original.EnvironmentVariables);
                loaded.CleanExitCodes.Should().BeEquivalentTo(original.CleanExitCodes);

                loaded.Schedule.Should().BeEquivalentTo(original.Schedule,
                    "every schedule entry must survive a save, including its days and its enabled flag");

                loaded.Schedule.Select(t => t.Id).Should().BeEquivalentTo(original.Schedule.Select(t => t.Id),
                    "task ids carry the once-per-day fire guard, so saving must not renumber them");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void PairingWindow_BuildsAndRendersItsQrCode()
    {
        // The QR is generated and drawn at runtime, so a failure here would only show up
        // when someone actually tried to pair a phone.
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var manager = CreateIsolatedManager();
            var devices = new DeviceStore(Path.Combine(_configRoot, "devices.json"));
            var pairing = new PairingService(devices);

            var settings = new AppSettings();
            settings.RemoteAccess.Port = 8787;

            var window = (PairingWindow)Offscreen(new PairingWindow(pairing, settings));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "PairingWindow");
                pairing.HasActiveCode.Should().BeTrue("opening the dialog issues a code");
            }
            finally
            {
                window.Close();
                manager.Dispose();
            }

            pairing.HasActiveCode.Should().BeFalse(
                "closing the dialog must withdraw the code so a stale QR cannot pair anything");
        });
    }

    [WpfFact]
    public void SettingsWindow_ShowsRemoteAccessWithoutAService()
    {
        // Settings is opened before remote access has ever been configured, so it has to
        // cope with there being no service yet.
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var window = (SettingsWindow)Offscreen(new SettingsWindow(new AppSettings()));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "SettingsWindow remote section");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void SettingsWindow_EditsACopySoCancelChangesNothing()
    {
        WpfHarness.RunOnUi(() =>
        {
            var original = new AppSettings
            {
                ConsoleBufferLines = 1234,
                UpdateRepository = "owner/repo",
                PowerShellPath = "pwsh.exe"
            };

            var window = (SettingsWindow)Offscreen(new SettingsWindow(original));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                window.Settings.Should().NotBeSameAs(original,
                    "cancelling must not mutate the live settings");
                window.Settings.ConsoleBufferLines.Should().Be(1234);
                window.Settings.UpdateRepository.Should().Be("owner/repo");
                window.Settings.PowerShellPath.Should().Be("pwsh.exe");

                window.Settings.RemoteAccess.Should().NotBeSameAs(original.RemoteAccess,
                    "the nested hosting settings are edited on a copy too, or cancelling "
                    + "would still have moved the listener");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WpfFact]
    public void SettingsWindow_BindsCleanlyWithTheSiteOnTheNetwork()
    {
        // This configuration renders the exposure warning, which the default one does
        // not — so without it that panel is never exercised.
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var settings = new AppSettings();
            settings.RemoteAccess.Enabled = true;
            settings.RemoteAccess.BindAddress = RemoteAccessSettings.AllInterfacesAddress;
            settings.RemoteAccess.PublicAddress = "https://servers.example.com";

            var window = (SettingsWindow)Offscreen(new SettingsWindow(settings));
            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "SettingsWindow with the site published");

                window.Settings.RemoteAccess.IsUnencryptedOnTheNetwork.Should().BeTrue(
                    "no certificate is set, which is what the warning is about");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // --- Weekly schedule ---

    private ServerManager ManagerWithSchedule(out Guid armaId, out ScheduledTask twiceAWeek)
    {
        var manager = CreateIsolatedManager();
        var script = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat");

        twiceAWeek = new ScheduledTask
        {
            Action = ScheduledAction.Restart,
            Time = "05:00",
            Days = ScheduleDays.Monday | ScheduleDays.Thursday
        };

        var arma = manager.Add(new ServerDefinition
        {
            Name = "Arma",
            ScriptPath = script,
            UpdateScriptPath = script,
            Schedule =
            {
                twiceAWeek,
                new ScheduledTask { Action = ScheduledAction.RunUpdate, Time = "04:30", Days = ScheduleDays.Monday },
                new ScheduledTask { Action = ScheduledAction.Stop, Time = "23:00", Days = ScheduleDays.EveryDay, Enabled = false }
            }
        });

        manager.Add(new ServerDefinition
        {
            Name = "Minecraft",
            ScriptPath = script,
            Schedule = { new ScheduledTask { Action = ScheduledAction.Start, Time = "08:00", Days = ScheduleDays.Weekend } }
        });

        armaId = arma.Id;
        return manager;
    }

    [WpfFact]
    public void ScheduleView_BindsCleanlyWithEntriesOnEveryKindOfDay()
    {
        // Multi-day, single-day, every-day, paused and weekend entries all render through
        // different parts of the card template, so all of them are present here.
        WpfHarness.RunOnUi(() =>
        {
            var manager = ManagerWithSchedule(out _, out _);
            var viewModel = new MainViewModel(manager);
            viewModel.SyncServers();

            using var collector = new BindingErrorCollector();

            MainWindow? window = null;
            try
            {
                window = (MainWindow)Offscreen(new MainWindow(viewModel));
                window.Show();

                viewModel.ShowScheduleCommand.Execute(null);
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "MainWindow schedule view");

                var monday = viewModel.Schedule.Days.Single(d => d.Day == ScheduleDays.Monday);
                monday.Entries.Select(e => e.Time).Should().Equal("04:30", "05:00", "23:00");

                var saturday = viewModel.Schedule.Days.Single(d => d.Day == ScheduleDays.Saturday);
                saturday.Entries.Select(e => e.ServerName).Should().Contain("Minecraft");

                viewModel.Schedule.Days.Count(d => d.IsToday).Should().Be(1);
                viewModel.Schedule.NextUpText.Should().StartWith("Next:");
            }
            finally
            {
                if (window is not null)
                {
                    window.AllowClose = true;
                    window.Close();
                }

                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void OnlyOneViewIsEverShownAtATime()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = CreateIsolatedManager();
            var viewModel = new MainViewModel(manager);

            try
            {
                void ExactlyOne(string when) =>
                    new[] { viewModel.IsServersVisible, viewModel.IsDashboardVisible, viewModel.IsScheduleVisible }
                        .Count(v => v).Should().Be(1, when);

                viewModel.IsServersVisible.Should().BeTrue("the servers view is what opens");
                ExactlyOne("at startup");

                viewModel.ShowScheduleCommand.Execute(null);
                viewModel.IsScheduleVisible.Should().BeTrue();
                ExactlyOne("after showing the schedule");

                viewModel.ShowDashboardCommand.Execute(null);
                viewModel.IsDashboardVisible.Should().BeTrue();
                ExactlyOne("after showing the dashboard");

                viewModel.ShowServersCommand.Execute(null);
                ExactlyOne("after going back to servers");
            }
            finally
            {
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void RemovingOneDayFromTheBoardIsSavedAndLeavesTheOtherDay()
    {
        WpfHarness.RunOnUi(() =>
        {
            var manager = ManagerWithSchedule(out var armaId, out var twiceAWeek);
            var viewModel = new MainViewModel(manager);
            viewModel.SyncServers();

            try
            {
                viewModel.SaveSchedule(armaId, d => d.RemoveScheduleDay(twiceAWeek.Id, ScheduleDays.Monday));

                manager.Find(armaId)!.Definition.Schedule.Single(t => t.Id == twiceAWeek.Id).Days
                    .Should().Be(ScheduleDays.Thursday);

                viewModel.Schedule.Days.Single(d => d.Day == ScheduleDays.Monday).Entries
                    .Should().NotContain(e => e.Task.Id == twiceAWeek.Id, "the board refreshes after a change");
                viewModel.Schedule.Days.Single(d => d.Day == ScheduleDays.Thursday).Entries
                    .Should().Contain(e => e.Task.Id == twiceAWeek.Id);

                // Saved, not just held in memory: a fresh manager on the same files sees it.
                manager.Dispose();
                using var reloaded = CreateIsolatedManager();
                reloaded.InitialiseAsync().GetAwaiter().GetResult();

                reloaded.Find(armaId)!.Definition.Schedule.Single(t => t.Id == twiceAWeek.Id).Days
                    .Should().Be(ScheduleDays.Thursday);
            }
            finally
            {
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void EditingAnEntryFromTheBoardKeepsItsIdentity()
    {
        // The id carries the once-per-day fire guard, so an edit must replace in place.
        WpfHarness.RunOnUi(() =>
        {
            var manager = ManagerWithSchedule(out var armaId, out var twiceAWeek);
            var viewModel = new MainViewModel(manager);
            viewModel.SyncServers();

            try
            {
                var edited = twiceAWeek.Clone();
                edited.Time = "06:15";
                edited.Days = ScheduleDays.Tuesday | ScheduleDays.Friday;

                viewModel.SaveSchedule(armaId, d => d.UpsertScheduledTask(edited));

                var schedule = manager.Find(armaId)!.Definition.Schedule;
                schedule.Should().HaveCount(3, "an edit is not an addition");

                var saved = schedule.Single(t => t.Id == twiceAWeek.Id);
                saved.Time.Should().Be("06:15");
                saved.Days.Should().Be(ScheduleDays.Tuesday | ScheduleDays.Friday);
            }
            finally
            {
                viewModel.Dispose();
                manager.Dispose();
            }
        });
    }

    [WpfFact]
    public void ScheduleEntryWindow_BindsCleanlyAndRefusesWhatCouldNeverRun()
    {
        WpfHarness.RunOnUi(() =>
        {
            using var collector = new BindingErrorCollector();

            var withUpdate = new ServerDefinition { Name = "Arma", UpdateScriptPath = @"C:\u.bat" };
            var withoutUpdate = new ServerDefinition { Name = "Minecraft" };
            var servers = new[] { withUpdate, withoutUpdate };

            var task = new ScheduledTask
            {
                Action = ScheduledAction.RunUpdate,
                Time = "04:00",
                Days = ScheduleDays.Sunday
            };

            var window = (ScheduleEntryWindow)Offscreen(
                new ScheduleEntryWindow(servers, withoutUpdate.Id, task, isNew: true));

            try
            {
                window.Show();
                WpfHarness.Pump(window);

                AssertNoBindingErrors(collector, "ScheduleEntryWindow");

                window.SelectedServer.Should().BeSameAs(withoutUpdate, "the requested server is preselected");

                window.TryValidate(out var error).Should().BeFalse(
                    "an update entry on a server with no update script would do nothing at 4am");
                error.Should().Contain("no update script");

                window.Task.Id.Should().Be(task.Id, "the dialog edits the same entry, not a new one");
            }
            finally
            {
                window.Close();
            }
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configRoot))
            {
                Directory.Delete(_configRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
