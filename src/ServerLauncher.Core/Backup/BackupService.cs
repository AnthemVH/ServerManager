using System.IO.Compression;
using System.Runtime.Versioning;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Backup;

/// <summary>Outcome of a single backup run.</summary>
/// <param name="Success">Whether an archive was produced.</param>
/// <param name="ArchivePath">Path to the archive, when one was created.</param>
/// <param name="FilesArchived">Number of files written into the archive.</param>
/// <param name="FilesSkipped">Files that could not be read, typically because the server held them open.</param>
/// <param name="Message">Human-readable summary for the console log.</param>
public readonly record struct BackupResult(
    bool Success,
    string? ArchivePath,
    int FilesArchived,
    int FilesSkipped,
    string Message);

/// <summary>
/// Creates zip archives of a server's folder.
///
/// The hard part is file locking: a running server holds its world and database files
/// open. Live mode copes by opening files with a permissive share mode and skipping
/// anything still locked, which is fast but not guaranteed consistent. Safe mode stops
/// the server first, which costs a short outage but always produces a restorable archive.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackupService
{
    /// <summary>Runs a backup according to the server's configured mode.</summary>
    public async Task<BackupResult> RunAsync(ServerInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var definition = instance.Definition;
        var source = definition.ResolveBackupSource();

        if (!Directory.Exists(source))
        {
            return new BackupResult(false, null, 0, 0, $"Backup source folder not found: {source}");
        }

        if (string.IsNullOrWhiteSpace(definition.BackupDestinationFolder))
        {
            return new BackupResult(false, null, 0, 0, "No backup destination folder configured.");
        }

        var wasRunning = instance.State == ServerState.Running;
        var mustRestart = false;

        if (definition.BackupMode == BackupMode.SafeStopAndRestart && wasRunning)
        {
            await instance.StopAsync(cancellationToken).ConfigureAwait(false);
            mustRestart = true;
        }

        try
        {
            Directory.CreateDirectory(definition.BackupDestinationFolder);

            var name = $"{Sanitise(definition.Name)}_{DateTime.Now:yyyy-MM-dd_HHmmss}.zip";
            var archivePath = Path.Combine(definition.BackupDestinationFolder, name);

            var (archived, skipped) = await Task.Run(
                () => CreateArchive(source, archivePath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            Prune(definition);

            var message = skipped == 0
                ? $"Backed up {archived} files to {name}."
                : $"Backed up {archived} files to {name}; skipped {skipped} locked files.";

            return new BackupResult(true, archivePath, archived, skipped, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackupResult(false, null, 0, 0, $"Backup failed: {ex.Message}");
        }
        finally
        {
            if (mustRestart)
            {
                await instance.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static (int Archived, int Skipped) CreateArchive(
        string sourceFolder, string archivePath, CancellationToken cancellationToken)
    {
        var archived = 0;
        var skipped = 0;

        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var root = Path.GetFullPath(sourceFolder);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');

            try
            {
                // ReadWrite sharing lets us read most files the server still has open.
                using var input = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                input.CopyTo(entryStream);
                archived++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Exclusively locked by the server; recorded rather than failing the run.
                skipped++;
            }
        }

        return (archived, skipped);
    }

    /// <summary>Deletes the oldest archives beyond the configured retention count.</summary>
    private static void Prune(ServerDefinition definition)
    {
        if (definition.BackupRetentionCount <= 0)
        {
            return;
        }

        try
        {
            var prefix = Sanitise(definition.Name) + "_";
            var archives = new DirectoryInfo(definition.BackupDestinationFolder)
                .EnumerateFiles(prefix + "*.zip")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(definition.BackupRetentionCount)
                .ToList();

            foreach (var archive in archives)
            {
                archive.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Retention is best-effort and must never fail an otherwise good backup.
        }
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "server" : cleaned;
    }
}
