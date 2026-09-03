using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Processes;

namespace ServerLauncher.Core.Tests;

public class ScriptLauncherTests
{
    private static readonly AppSettings Settings = new();

    private static ServerDefinition Definition(string scriptPath, string args = "") => new()
    {
        Name = "Test",
        ScriptPath = scriptPath,
        Arguments = args
    };

    [Theory]
    [InlineData("start.bat")]
    [InlineData("start.cmd")]
    [InlineData("start.BAT")]
    public void BatchScripts_LaunchThroughCmd(string fileName)
    {
        var info = ScriptLauncher.BuildStartInfo(Definition(fileName), Settings);

        info.FileName.Should().Be("cmd.exe");
        info.Arguments.Should().StartWith("/c ");
    }

    [Fact]
    public void BatchScript_WithSpacesInPath_IsDoubleQuoted()
    {
        // cmd.exe strips the outer quote pair, so the inner pair must survive for a
        // path containing spaces to resolve correctly.
        var info = ScriptLauncher.BuildStartInfo(
            Definition(@"C:\Program Files\My Server\start.bat"), Settings);

        // Expected: /c ""C:\Program Files\My Server\start.bat""
        var expected = @"/c """"C:\Program Files\My Server\start.bat""""";
        info.Arguments.Should().Be(expected);
    }

    [Fact]
    public void PowerShellScript_UsesNoProfileAndBypass()
    {
        var info = ScriptLauncher.BuildStartInfo(Definition(@"C:\servers\start.ps1"), Settings);

        info.FileName.Should().Be("powershell.exe");
        info.Arguments.Should().Contain("-NoProfile");
        info.Arguments.Should().Contain("-ExecutionPolicy Bypass");
        info.Arguments.Should().Contain(@"-File C:\servers\start.ps1");
    }

    [Fact]
    public void PowerShellScript_HonoursConfiguredHost()
    {
        var settings = new AppSettings { PowerShellPath = @"C:\Program Files\PowerShell\7\pwsh.exe" };

        var info = ScriptLauncher.BuildStartInfo(Definition(@"C:\servers\start.ps1"), settings);

        info.FileName.Should().Be(@"C:\Program Files\PowerShell\7\pwsh.exe");
    }

    [Fact]
    public void Executable_IsInvokedDirectly()
    {
        var info = ScriptLauncher.BuildStartInfo(Definition(@"C:\servers\server.exe", "-port 25565"), Settings);

        info.FileName.Should().Be(@"C:\servers\server.exe");
        info.Arguments.Should().Be("-port 25565");
    }

    [Fact]
    public void AllStreamsAreRedirected_SoConsoleCaptureWorks()
    {
        var info = ScriptLauncher.BuildStartInfo(Definition(@"C:\servers\start.bat"), Settings);

        info.RedirectStandardOutput.Should().BeTrue();
        info.RedirectStandardError.Should().BeTrue();
        info.RedirectStandardInput.Should().BeTrue();
        info.UseShellExecute.Should().BeFalse();
        info.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void WorkingDirectory_DefaultsToScriptFolder()
    {
        // Most server scripts assume they run from their own directory.
        var info = ScriptLauncher.BuildStartInfo(Definition(@"C:\servers\minecraft\start.bat"), Settings);

        info.WorkingDirectory.Should().Be(@"C:\servers\minecraft");
    }

    [Fact]
    public void WorkingDirectory_ExplicitValueWins()
    {
        var definition = Definition(@"C:\servers\minecraft\start.bat");
        definition.WorkingDirectory = @"D:\data";

        var info = ScriptLauncher.BuildStartInfo(definition, Settings);

        info.WorkingDirectory.Should().Be(@"D:\data");
    }

    [Fact]
    public void EnvironmentVariables_AreApplied()
    {
        var definition = Definition(@"C:\servers\start.bat");
        definition.EnvironmentVariables["JAVA_OPTS"] = "-Xmx4G";

        var info = ScriptLauncher.BuildStartInfo(definition, Settings);

        info.Environment["JAVA_OPTS"].Should().Be("-Xmx4G");
    }

    [Fact]
    public void MissingScriptPath_FailsLoudly()
    {
        var act = () => ScriptLauncher.BuildStartInfo(Definition(""), Settings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no script path*");
    }

    [Theory]
    [InlineData("a.bat", true)]
    [InlineData("a.cmd", true)]
    [InlineData("a.ps1", true)]
    [InlineData("a.exe", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.jar", false)]
    public void IsSupportedScript_RecognisesLaunchableTypes(string path, bool expected)
    {
        ScriptLauncher.IsSupportedScript(path).Should().Be(expected);
    }
}
