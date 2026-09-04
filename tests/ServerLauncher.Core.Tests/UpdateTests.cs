using System.Text;
using System.Text.Json;
using FluentAssertions;
using ServerLauncher.Core.Updates;

namespace ServerLauncher.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  v2.0.0  ", "2.0.0")]
    public void TryParseTag_AcceptsTheTagShapesWePublish(string tag, string expected)
    {
        UpdateService.TryParseTag(tag, out var version).Should().BeTrue();
        version.Should().Be(Version.Parse(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("release-2024")]
    public void TryParseTag_RejectsTagsItCannotCompare(string? tag)
    {
        // Guessing at an unparseable tag risks offering a downgrade as an update.
        UpdateService.TryParseTag(tag, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    public void IsNewer_ComparesReleasesCorrectly(string candidate, string current, bool expected)
    {
        UpdateService.IsNewer(Version.Parse(candidate), Version.Parse(current)).Should().Be(expected);
    }

    [Fact]
    public void IsNewer_IgnoresTheRevisionField()
    {
        // MSBuild stamps AssemblyVersion as 1.2.0.0 while the tag parses as 1.2.0.
        // Comparing the revision would report a phantom update on every startup.
        var assemblyVersion = new Version(1, 2, 0, 0);
        var tagVersion = new Version(1, 2, 0);

        UpdateService.IsNewer(tagVersion, assemblyVersion).Should().BeFalse();
        UpdateService.IsNewer(assemblyVersion, tagVersion).Should().BeFalse();
    }
}

public class ReleaseParsingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string ValidRelease = """
    {
      "tag_name": "v1.4.0",
      "html_url": "https://github.com/owner/repo/releases/tag/v1.4.0",
      "body": "Fixed the console scrollback.",
      "draft": false,
      "prerelease": false,
      "assets": [
        {
          "name": "ServerLauncher.exe",
          "size": 1048576,
          "browser_download_url": "https://github.com/owner/repo/releases/download/v1.4.0/ServerLauncher.exe"
        },
        {
          "name": "ServerLauncher.exe.sha256",
          "size": 64,
          "browser_download_url": "https://github.com/owner/repo/releases/download/v1.4.0/ServerLauncher.exe.sha256"
        }
      ]
    }
    """;

    [Fact]
    public void ParsesAReleaseWithBothAssets()
    {
        var release = UpdateService.ParseRelease(Json(ValidRelease));

        release.Should().NotBeNull();
        release!.Version.Should().Be(new Version(1, 4, 0));
        release.TagName.Should().Be("v1.4.0");
        release.Notes.Should().Contain("console scrollback");
        release.DownloadUrl.Should().EndWith("ServerLauncher.exe");
        release.ChecksumUrl.Should().EndWith(".sha256");
        release.SizeBytes.Should().Be(1048576);
        // Formatted for the current locale, so compare the same way rather than
        // assuming a dot decimal separator.
        release.SizeDisplay.Should().Be($"{1.0:0.0} MB");
    }

    [Fact]
    public void IgnoresDrafts()
    {
        // A draft's assets are not publicly downloadable.
        var draft = ValidRelease.Replace("\"draft\": false", "\"draft\": true");

        UpdateService.ParseRelease(Json(draft)).Should().BeNull();
    }

    [Fact]
    public void RejectsAReleaseWithNoExecutableAttached()
    {
        // A tag pushed without the workflow completing would otherwise be offered as an
        // update with nothing to download.
        var noAssets = """
        { "tag_name": "v1.4.0", "draft": false, "assets": [] }
        """;

        UpdateService.ParseRelease(Json(noAssets)).Should().BeNull();
    }

    [Fact]
    public void RejectsAnUnparseableTag()
    {
        var badTag = ValidRelease.Replace("\"tag_name\": \"v1.4.0\"", "\"tag_name\": \"nightly\"");

        UpdateService.ParseRelease(Json(badTag)).Should().BeNull();
    }

    [Fact]
    public void ToleratesAMissingChecksumAsset()
    {
        var noChecksum = """
        {
          "tag_name": "v1.4.0",
          "draft": false,
          "assets": [
            {
              "name": "ServerLauncher.exe",
              "size": 500,
              "browser_download_url": "https://example.invalid/ServerLauncher.exe"
            }
          ]
        }
        """;

        var release = UpdateService.ParseRelease(Json(noChecksum));

        release.Should().NotBeNull();
        release!.ChecksumUrl.Should().BeNull("verification is skipped rather than the update being blocked");
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ServerLauncherUpdateTests", Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Apply_SwapsTheExecutableAndKeepsTheOldOneAside()
    {
        var target = WriteFile("app.exe", "version 1");
        var downloaded = WriteFile("app.exe.new", "version 2");

        UpdateInstaller.Apply(downloaded, target, restart: false);

        File.ReadAllText(target).Should().Be("version 2", "the new build must be in place");
        File.Exists(target + UpdateInstaller.BackupSuffix).Should().BeTrue(
            "the previous build is kept until the new one has started successfully");
        File.ReadAllText(target + UpdateInstaller.BackupSuffix).Should().Be("version 1");
        File.Exists(downloaded).Should().BeFalse("the download was moved, not copied");
    }

    [Fact]
    public void Apply_ReplacesABackupLeftByAnEarlierUpdate()
    {
        var target = WriteFile("app.exe", "version 2");
        WriteFile("app.exe" + UpdateInstaller.BackupSuffix, "version 1");
        var downloaded = WriteFile("app.exe.new", "version 3");

        UpdateInstaller.Apply(downloaded, target, restart: false);

        File.ReadAllText(target).Should().Be("version 3");
        File.ReadAllText(target + UpdateInstaller.BackupSuffix).Should().Be("version 2");
    }

    [Fact]
    public void Apply_RollsBackWhenTheNewBuildCannotBeMovedIntoPlace()
    {
        var target = WriteFile("app.exe", "version 1");
        var downloaded = WriteFile("app.exe.new", "version 2");

        // Hold the download open exclusively so the second move fails, simulating a
        // scanner or backup tool grabbing the file mid-update.
        using (var _ = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => UpdateInstaller.Apply(downloaded, target, restart: false);
            act.Should().Throw<IOException>();
        }

        // The critical guarantee: a failed update must never leave the app missing.
        File.Exists(target).Should().BeTrue("the original executable must be restored");
        File.ReadAllText(target).Should().Be("version 1");
    }

    [Fact]
    public void Apply_FailsClearlyWhenTheDownloadIsMissing()
    {
        var target = WriteFile("app.exe", "version 1");

        var act = () => UpdateInstaller.Apply(Path.Combine(_root, "absent.exe"), target, restart: false);

        act.Should().Throw<FileNotFoundException>();
        File.ReadAllText(target).Should().Be("version 1", "nothing should have been touched");
    }

    [Theory]
    [InlineData(new[] { "--after-update", "1234" }, 1234)]
    [InlineData(new[] { "--minimised", "--after-update", "99" }, 99)]
    [InlineData(new[] { "--AFTER-UPDATE", "7" }, 7)]
    public void GetProcessIdToWaitFor_ReadsTheRelaunchArgument(string[] args, int expected)
    {
        UpdateInstaller.GetProcessIdToWaitFor(args).Should().Be(expected);
    }

    [Theory]
    [InlineData((object)new string[0])]
    [InlineData((object)new[] { "--minimised" })]
    [InlineData((object)new[] { "--after-update" })]
    [InlineData((object)new[] { "--after-update", "notanumber" })]
    public void GetProcessIdToWaitFor_ReturnsNullWhenAbsentOrMalformed(string[] args)
    {
        UpdateInstaller.GetProcessIdToWaitFor(args).Should().BeNull();
    }

    [Fact]
    public void WaitForPreviousInstance_ReturnsImmediatelyForAProcessThatIsGone()
    {
        // A pid that no longer exists is the normal case when the old process exits fast.
        var act = () => UpdateInstaller.WaitForPreviousInstance(999999, TimeSpan.FromSeconds(1));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ComputeSha256_MatchesTheKnownHashOfItsContent()
    {
        // Guards the verification path: a wrong hash here would mean either rejecting
        // good downloads or, worse, accepting tampered ones.
        var path = WriteFile("payload.bin", "abc");

        var hash = await UpdateService.ComputeSha256Async(path);

        hash.Should().Be("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
    }

    [Fact]
    public async Task ComputeSha256_DiffersWhenContentChangesByOneByte()
    {
        var original = WriteFile("a.bin", "version 1");
        var tampered = WriteFile("b.bin", "version 2");

        var first = await UpdateService.ComputeSha256Async(original);
        var second = await UpdateService.ComputeSha256Async(tampered);

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task CheckAsync_ReportsNotConfiguredWithoutARepository()
    {
        var result = await new UpdateService().CheckAsync("", new Version(1, 0, 0));

        result.Status.Should().Be(UpdateCheckStatus.NotConfigured);
        result.Release.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_ReportsNotConfiguredForAMalformedRepository()
    {
        // "ServerLauncher" without an owner would otherwise produce a confusing 404.
        var result = await new UpdateService().CheckAsync("ServerLauncher", new Version(1, 0, 0));

        result.Status.Should().Be(UpdateCheckStatus.NotConfigured);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Parses the JSON GitHub actually returned for the v1.0.0 release built by the release
/// workflow. The hand-written samples above prove the parser handles the shapes we expect;
/// this proves the workflow really produces one of them — a renamed asset or a retagged
/// release would break updates silently, and this fails instead.
/// </summary>
public class RealReleasePayloadTests
{
    private static JsonElement RealPayload()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "real-release.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void TheReleaseWorkflowProducesAPayloadTheAppCanParse()
    {
        var release = UpdateService.ParseRelease(RealPayload());

        release.Should().NotBeNull("the published release must be installable by the app");
        release!.Version.Should().Be(new Version(1, 1, 0));
        release.DownloadUrl.Should().EndWith(UpdateService.AssetName);
        release.ChecksumUrl.Should().EndWith(UpdateService.ChecksumAssetName,
            "without the checksum the download cannot be verified");
        release.SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnIdenticalVersionIsNotOfferedAsAnUpdate()
    {
        // A copy installed from this very release must report "up to date" rather than
        // offering to reinstall itself in a loop.
        var release = UpdateService.ParseRelease(RealPayload())!;

        UpdateService.IsNewer(release.Version, new Version(1, 1, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void AnOlderInstalledBuildIsOfferedTheRelease()
    {
        var release = UpdateService.ParseRelease(RealPayload())!;

        UpdateService.IsNewer(release.Version, new Version(1, 0, 0)).Should().BeTrue();
    }
}

/// <summary>
/// Releases carry two builds: a small one that needs the .NET runtimes installed and a
/// standalone one that carries them. Getting this selection wrong is not a cosmetic bug —
/// replacing a standalone install with the small build on a machine without the runtimes
/// produces an app that cannot start and cannot say why, because a missing framework
/// stops the process before its code runs.
/// </summary>
public class BuildVariantTests
{
    private const string ReleaseWithBothBuilds = """
    {
      "tag_name": "v1.6.0",
      "html_url": "https://github.com/AnthemVH/ServerManager/releases/tag/v1.6.0",
      "body": "notes",
      "assets": [
        { "name": "ServerLauncher.exe", "size": 1400000,
          "browser_download_url": "https://example.invalid/ServerLauncher.exe" },
        { "name": "ServerLauncher.exe.sha256", "size": 64,
          "browser_download_url": "https://example.invalid/ServerLauncher.exe.sha256" },
        { "name": "ServerLauncher-standalone.exe", "size": 83518606,
          "browser_download_url": "https://example.invalid/ServerLauncher-standalone.exe" },
        { "name": "ServerLauncher-standalone.exe.sha256", "size": 64,
          "browser_download_url": "https://example.invalid/ServerLauncher-standalone.exe.sha256" }
      ]
    }
    """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheTwoBuildsAreDistinctAssets()
    {
        BuildInfo.StandaloneAsset.Should().NotBe(BuildInfo.FrameworkDependentAsset);
    }

    [Fact]
    public void EachBuildAsksForItsOwnKind()
    {
        var standalone = UpdateService.ParseRelease(Parse(ReleaseWithBothBuilds), BuildInfo.StandaloneAsset)!;
        var small = UpdateService.ParseRelease(Parse(ReleaseWithBothBuilds), BuildInfo.FrameworkDependentAsset)!;

        standalone.DownloadUrl.Should().EndWith("/ServerLauncher-standalone.exe");
        small.DownloadUrl.Should().EndWith("/ServerLauncher.exe");
    }

    [Fact]
    public void ChecksumsAreMatchedToTheirOwnAsset()
    {
        // The obvious way to get this wrong is to verify the 80 MB standalone download
        // against the 1.4 MB build's hash, which would reject every update.
        var standalone = UpdateService.ParseRelease(Parse(ReleaseWithBothBuilds), BuildInfo.StandaloneAsset)!;

        standalone.ChecksumUrl.Should().EndWith("/ServerLauncher-standalone.exe.sha256");
    }

    [Fact]
    public void TheSmallBuildsNameIsNotAPrefixMatchForTheStandaloneOne()
    {
        // "ServerLauncher.exe" must not be satisfied by "ServerLauncher-standalone.exe"
        // or vice versa; the lookup is an exact name match, and this pins that.
        var small = UpdateService.ParseRelease(Parse(ReleaseWithBothBuilds), BuildInfo.FrameworkDependentAsset)!;

        small.SizeBytes.Should().Be(1400000, "the small build's own size, not the standalone one's");
    }

    [Fact]
    public void AReleaseMissingThisBuildsKindOffersNothing()
    {
        // Releases before v1.6.0 have no standalone asset. Offering the wrong build would
        // be far worse than reporting there is nothing to install.
        const string oldRelease = """
        {
          "tag_name": "v1.5.1",
          "assets": [
            { "name": "ServerLauncher.exe",
              "browser_download_url": "https://example.invalid/ServerLauncher.exe" }
          ]
        }
        """;

        UpdateService.ParseRelease(Parse(oldRelease), BuildInfo.StandaloneAsset).Should().BeNull();
    }

    [Fact]
    public void ABuildThatWasNotPublishedStandaloneSaysSo()
    {
        // The test host is built without SelfContained, so this also proves the stamp
        // defaults to "not standalone" rather than to whatever was last published.
        BuildInfo.IsStandalone.Should().BeFalse();
        BuildInfo.UpdateAssetName.Should().Be(BuildInfo.FrameworkDependentAsset);
    }

    [Fact]
    public void TheChecksumAssetIsAlwaysNamedAfterItsExecutable()
    {
        UpdateService.ChecksumNameFor("Anything.exe").Should().Be("Anything.exe.sha256");
    }
}
