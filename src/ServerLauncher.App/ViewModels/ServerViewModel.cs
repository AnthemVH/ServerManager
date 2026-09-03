using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.App.ViewModels;

/// <summary>
/// Binds one supervised server to the UI. Core raises its events on background
/// threads, so everything here marshals onto the dispatcher, and console output is
/// buffered and applied in batches rather than one line at a time.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject, IDisposable
{
    private readonly ConcurrentQueue<LogLine> _pendingLines = new();
    private readonly int _consoleCapacity;
    private bool _consoleAttached;

    [ObservableProperty]
    private ServerState _state;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _memoryMegabytes;

    [ObservableProperty]
    private int _processCount;

    [ObservableProperty]
    private string _uptimeText = "—";

    public ServerViewModel(ServerInstance instance, int consoleCapacity)
    {
        Instance = instance;
        _consoleCapacity = Math.Max(200, consoleCapacity);
        _name = instance.Definition.Name;
        _state = instance.State;

        instance.StateChanged += OnStateChanged;
        instance.LineAppended += OnLineAppended;
        instance.ResourceSampled += OnResourceSampled;
    }

    public ServerInstance Instance { get; }

    public Guid Id => Instance.Id;

    public ServerDefinition Definition => Instance.Definition;

    /// <summary>Console lines currently shown. Only populated while this server is selected.</summary>
    public ObservableCollection<ConsoleLineViewModel> ConsoleLines { get; } = new();

    /// <summary>Recent CPU samples, oldest first, for the sparkline.</summary>
    public IReadOnlyList<double> CpuHistory =>
        Instance.ResourceHistory().Select(s => s.CpuPercent).ToList();

    public bool IsRunning => State is ServerState.Running or ServerState.Starting;

    public bool CanStart => State is ServerState.Stopped or ServerState.Crashed or ServerState.Failed;

    public bool CanStop => State is ServerState.Running or ServerState.Starting;

    public string StatusText => State switch
    {
        ServerState.Stopped => "Stopped",
        ServerState.Starting => "Starting…",
        ServerState.Running => "Running",
        ServerState.Stopping => "Stopping…",
        ServerState.Crashed => "Crashed",
        ServerState.Failed => "Failed",
        _ => State.ToString()
    };

    public string ResourceSummary => State == ServerState.Running
        ? $"{CpuPercent:0.0}% CPU · {MemoryMegabytes:0} MB"
        : "—";

    /// <summary>Begins mirroring console output into <see cref="ConsoleLines"/>.</summary>
    public void AttachConsole()
    {
        ConsoleLines.Clear();
        while (_pendingLines.TryDequeue(out _))
        {
        }

        foreach (var line in Instance.ConsoleSnapshot())
        {
            ConsoleLines.Add(new ConsoleLineViewModel(line));
        }

        _consoleAttached = true;
    }

    /// <summary>Stops mirroring, so unselected servers cost nothing on the UI thread.</summary>
    public void DetachConsole()
    {
        _consoleAttached = false;
        ConsoleLines.Clear();
        while (_pendingLines.TryDequeue(out _))
        {
        }
    }

    /// <summary>Applies buffered console lines. Called on the shared UI timer.</summary>
    public bool DrainConsole()
    {
        if (!_consoleAttached || _pendingLines.IsEmpty)
        {
            return false;
        }

        var applied = 0;
        while (applied < 500 && _pendingLines.TryDequeue(out var line))
        {
            ConsoleLines.Add(new ConsoleLineViewModel(line));
            applied++;
        }

        // Mirror the Core ring buffer's cap so a long session cannot grow unbounded.
        while (ConsoleLines.Count > _consoleCapacity)
        {
            ConsoleLines.RemoveAt(0);
        }

        return applied > 0;
    }

    public void RefreshUptime()
    {
        var uptime = Instance.Uptime;
        UptimeText = uptime is null
            ? "—"
            : uptime.Value.TotalDays >= 1
                ? $"{(int)uptime.Value.TotalDays}d {uptime.Value.Hours}h {uptime.Value.Minutes}m"
                : $"{uptime.Value.Hours:00}:{uptime.Value.Minutes:00}:{uptime.Value.Seconds:00}";
    }

    public void NotifyDefinitionChanged()
    {
        Name = Definition.Name;
        OnPropertyChanged(nameof(Definition));
    }

    private void OnStateChanged(ServerInstance _, ServerState state) =>
        OnUiThread(() =>
        {
            State = state;

            if (state != ServerState.Running)
            {
                CpuPercent = 0;
                MemoryMegabytes = 0;
                ProcessCount = 0;
            }

            RefreshUptime();
        });

    private void OnLineAppended(ServerInstance _, LogLine line)
    {
        if (_consoleAttached)
        {
            _pendingLines.Enqueue(line);
        }
    }

    private void OnResourceSampled(ServerInstance _, ResourceSample sample) =>
        OnUiThread(() =>
        {
            CpuPercent = sample.CpuPercent;
            MemoryMegabytes = sample.WorkingSetMegabytes;
            ProcessCount = sample.ProcessCount;
            OnPropertyChanged(nameof(ResourceSummary));
            OnPropertyChanged(nameof(CpuHistory));
        });

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    partial void OnStateChanged(ServerState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(ResourceSummary));
    }

    partial void OnCpuPercentChanged(double value) => OnPropertyChanged(nameof(ResourceSummary));

    partial void OnMemoryMegabytesChanged(double value) => OnPropertyChanged(nameof(ResourceSummary));

    public void Dispose()
    {
        Instance.StateChanged -= OnStateChanged;
        Instance.LineAppended -= OnLineAppended;
        Instance.ResourceSampled -= OnResourceSampled;
    }
}

/// <summary>A single console line prepared for display.</summary>
public sealed class ConsoleLineViewModel
{
    public ConsoleLineViewModel(LogLine line)
    {
        Timestamp = line.Timestamp.ToString("HH:mm:ss");
        Text = line.Text;
        Stream = line.Stream;
    }

    public string Timestamp { get; }

    public string Text { get; }

    public LogStream Stream { get; }
}
