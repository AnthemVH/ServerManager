using FluentAssertions;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Tests;

public class ServerDefinitionTests
{
    [Fact]
    public void Clone_DeepCopiesEnvironmentVariables()
    {
        // A shallow copy would share the dictionary, so editing a clone and then
        // cancelling would still have mutated the live definition.
        var original = new ServerDefinition();
        original.EnvironmentVariables["JAVA_OPTS"] = "-Xmx2G";

        var copy = original.Clone();
        copy.EnvironmentVariables["JAVA_OPTS"] = "-Xmx8G";
        copy.EnvironmentVariables["EXTRA"] = "value";

        original.EnvironmentVariables["JAVA_OPTS"].Should().Be("-Xmx2G");
        original.EnvironmentVariables.Should().NotContainKey("EXTRA");
    }

    [Fact]
    public void Clone_CopiesEveryScalarField()
    {
        var original = new ServerDefinition
        {
            Name = "Original",
            ScriptPath = @"C:\a\start.bat",
            GracefulStopTimeoutSeconds = 42,
            RestartPolicy = RestartPolicy.Always,
            BackupMode = BackupMode.Live
        };

        var copy = original.Clone();

        copy.Id.Should().Be(original.Id);
        copy.Name.Should().Be("Original");
        copy.ScriptPath.Should().Be(@"C:\a\start.bat");
        copy.GracefulStopTimeoutSeconds.Should().Be(42);
        copy.RestartPolicy.Should().Be(RestartPolicy.Always);
        copy.BackupMode.Should().Be(BackupMode.Live);
    }

    [Fact]
    public void ParseEnvironment_ReadsKeyValueLines()
    {
        var parsed = ServerDefinition.ParseEnvironment("JAVA_OPTS=-Xmx4G\nPORT=25565");

        parsed.Should().HaveCount(2);
        parsed["JAVA_OPTS"].Should().Be("-Xmx4G");
        parsed["PORT"].Should().Be("25565");
    }

    [Fact]
    public void ParseEnvironment_KeepsEqualsSignsInsideValues()
    {
        // Only the first "=" separates, so JVM flags survive intact.
        var parsed = ServerDefinition.ParseEnvironment("OPTS=-Dfoo=bar -Dbaz=qux");

        parsed["OPTS"].Should().Be("-Dfoo=bar -Dbaz=qux");
    }

    [Fact]
    public void ParseEnvironment_SkipsBlanksCommentsAndMalformedLines()
    {
        var parsed = ServerDefinition.ParseEnvironment(
            "\n# a comment\nGOOD=yes\n\nnoequalshere\n=novalue\n   \n");

        parsed.Should().HaveCount(1);
        parsed.Should().ContainKey("GOOD");
    }

    [Fact]
    public void ParseEnvironment_HandlesWindowsLineEndings()
    {
        // The editor produces CRLF, so a stray \r must not end up inside the value.
        var parsed = ServerDefinition.ParseEnvironment("A=1\r\nB=2\r\n");

        parsed["A"].Should().Be("1");
        parsed["B"].Should().Be("2");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ParseEnvironment_ReturnsEmptyForNothing(string? text)
    {
        ServerDefinition.ParseEnvironment(text).Should().BeEmpty();
    }

    [Fact]
    public void FormatEnvironment_RoundTripsThroughParse()
    {
        var original = new Dictionary<string, string>
        {
            ["JAVA_OPTS"] = "-Xmx4G -Xms1G",
            ["WORLD"] = "overworld"
        };

        var reparsed = ServerDefinition.ParseEnvironment(ServerDefinition.FormatEnvironment(original));

        reparsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void FormatEnvironment_ReturnsEmptyForNoVariables()
    {
        ServerDefinition.FormatEnvironment(new Dictionary<string, string>()).Should().BeEmpty();
        ServerDefinition.FormatEnvironment(null).Should().BeEmpty();
    }

    [Fact]
    public void ResolveWorkingDirectory_FallsBackToTheScriptFolder()
    {
        var definition = new ServerDefinition { ScriptPath = @"C:\servers\minecraft\start.bat" };

        definition.ResolveWorkingDirectory().Should().Be(@"C:\servers\minecraft");
    }

    [Fact]
    public void ResolveBackupSource_FallsBackToTheWorkingDirectory()
    {
        var definition = new ServerDefinition
        {
            ScriptPath = @"C:\servers\minecraft\start.bat",
            WorkingDirectory = @"D:\data"
        };

        definition.ResolveBackupSource().Should().Be(@"D:\data");
    }
}
