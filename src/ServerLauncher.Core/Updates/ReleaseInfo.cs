namespace ServerLauncher.Core.Updates;

/// <summary>A published release available to install.</summary>
/// <param name="Version">Version parsed from the release tag.</param>
/// <param name="TagName">The raw tag, e.g. "v1.2.0".</param>
/// <param name="Notes">Release body, shown to the user before they approve.</param>
/// <param name="DownloadUrl">Direct URL of the executable asset.</param>
/// <param name="ChecksumUrl">URL of the SHA-256 file, when the release publishes one.</param>
/// <param name="SizeBytes">Asset size, for the download progress display.</param>
/// <param name="HtmlUrl">Release page, for the "view details" link.</param>
public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    string Notes,
    string DownloadUrl,
    string? ChecksumUrl,
    long SizeBytes,
    string HtmlUrl)
{
    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
        : $"{SizeBytes / 1024d:0} KB";
}

/// <summary>Why an update check did not produce a release.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer release is available.</summary>
    UpdateAvailable,

    /// <summary>Already running the latest release.</summary>
    UpToDate,

    /// <summary>No repository configured, so no check was attempted.</summary>
    NotConfigured,

    /// <summary>The check failed — offline, rate limited, or the repository is private.</summary>
    Failed
}

/// <param name="Status">Outcome of the check.</param>
/// <param name="Release">The newer release, when one exists.</param>
/// <param name="Message">Human-readable detail for the status bar.</param>
public readonly record struct UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseInfo? Release,
    string Message);
