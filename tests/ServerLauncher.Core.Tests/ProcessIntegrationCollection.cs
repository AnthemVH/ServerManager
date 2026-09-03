namespace ServerLauncher.Core.Tests;

/// <summary>
/// Groups the tests that launch real processes so they run one at a time.
///
/// xUnit runs test classes in parallel by default. These classes each spawn cmd.exe,
/// powershell.exe and their children, so running them concurrently puts a dozen real
/// processes in flight at once and their start-up times balloon past the waits the tests
/// allow — producing failures that have nothing to do with the code. Serialising them
/// keeps the suite trustworthy, which matters because a flake here blocks a release.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessIntegrationCollection
{
    public const string Name = "Process integration";
}
