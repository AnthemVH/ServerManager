using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ServerLauncher.Core.Updates;

/// <summary>
/// Checks GitHub Releases for a newer build and downloads it.
///
/// Pull rather than push: the server only needs outbound HTTPS, so nothing has to be
/// exposed inbound on a rented box. Downloads are verified against the SHA-256 the
/// release publishes before they are ever put on disk as an executable.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Name of the executable asset attached to each release.</summary>
    public const string AssetName = "ServerLauncher.App.exe";

    /// <summary>Name of the checksum asset attached alongside it.</summary>
    public const string ChecksumAssetName = "ServerLauncher.App.exe.sha256";

    /// <summary>
    /// Optional token for private repositories, read from the environment rather than
    /// stored in settings.json — a token in a plaintext config file is a credential leak
    /// waiting to happen. Public repositories need no token at all.
    /// </summary>
    public const string TokenEnvironmentVariable = "SERVERLAUNCHER_GITHUB_TOKEN";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ServerLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The version this build reports, taken from the entry assembly.</summary>
    public static Version DetectCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Parses a release tag into a version. Accepts "v1.2.3" and "1.2.3"; anything else
    /// is ignored rather than guessed at.
    /// </summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>
    /// Compares versions ignoring the Revision field, since .NET fills an unset revision
    /// with -1 and MSBuild fills it with 0 — comparing it produces phantom updates.
    /// </summary>
    public static bool IsNewer(Version candidate, Version current)
    {
        static Version Normalise(Version v) =>
            new(v.Major, v.Minor, Math.Max(0, v.Build));

        return Normalise(candidate) > Normalise(current);
    }

    /// <param name="repository">"owner/name", e.g. "chris/ServerLauncher".</param>
    /// <param name="currentVersion">Version to compare the latest release against.</param>
    public async Task<UpdateCheckResult> CheckAsync(
        string repository,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null,
                "No update repository configured.");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{repository.Trim()}/releases/latest");

            ApplyToken(request);

            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    DescribeFailure(response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var release = ParseRelease(document.RootElement);

            if (release is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                    $"The latest release has no '{AssetName}' attached.");
            }

            return IsNewer(release.Version, currentVersion)
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release,
                    $"Version {release.Version} is available.")
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null,
                    $"Up to date (version {currentVersion}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null,
                $"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Parses the release JSON. Public so the shape can be unit tested offline.</summary>
    public static ReleaseInfo? ParseRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagElement)
            || !TryParseTag(tagElement.GetString(), out var version))
        {
            return null;
        }

        // A draft has no usable download, and a prerelease should not be offered as
        // the latest stable build.
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? downloadUrl = null;
        string? checksumUrl = null;
        long size = 0;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;

            if (name is null || url is null)
            {
                continue;
            }

            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = url;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsed) ? parsed : 0;
            }
            else if (name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }

        if (downloadUrl is null)
        {
            return null;
        }

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
        var htmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
        var tag = tagElement.GetString() ?? version.ToString();

        return new ReleaseInfo(version, tag, notes, downloadUrl, checksumUrl, size, htmlUrl);
    }

    /// <summary>
    /// Downloads the release executable to a temporary file and verifies its SHA-256.
    /// </summary>
    /// <returns>Path to the verified file.</returns>
    /// <exception cref="InvalidOperationException">The download did not match its published hash.</exception>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        Directory.CreateDirectory(destinationDirectory);
        var target = Path.Combine(destinationDirectory, AssetName + ".new");

        using (var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl))
        {
            ApplyToken(request);

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.SizeBytes;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;

                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)copied / total, 0d, 1d));
                }
            }
        }

        if (release.ChecksumUrl is not null)
        {
            var expected = await FetchChecksumAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            var actual = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                // Never leave an unverified executable lying around.
                TryDelete(target);
                throw new InvalidOperationException(
                    "The downloaded update did not match its published checksum and has been discarded.");
            }
        }

        return target;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task<string> FetchChecksumAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyToken(request);

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Accept both a bare hash and the "<hash>  <filename>" shape sha256sum emits.
        return text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
               ?? string.Empty;
    }

    private static void ApplyToken(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    private static string DescribeFailure(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.NotFound =>
            "Repository or release not found. If the repository is private, set the "
            + TokenEnvironmentVariable + " environment variable.",
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "GitHub refused the request — the token may be missing, expired, or rate limited.",
        _ => $"GitHub returned {(int)status} {status}."
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
