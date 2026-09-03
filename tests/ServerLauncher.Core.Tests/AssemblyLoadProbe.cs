using FluentAssertions;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Confirms the WPF assembly can be loaded by the test host. Windows Smart App Control
/// has been observed blocking freshly built unsigned binaries, which would make every
/// UI test fail for a reason unrelated to the code.
/// </summary>
public class AssemblyLoadProbe
{
    [Fact]
    public void AppAssemblyLoads()
    {
        var type = typeof(ServerLauncher.App.ViewModels.MainViewModel);

        type.Should().NotBeNull();
        type.Assembly.GetName().Name.Should().Be("ServerLauncher.App");
    }
}
