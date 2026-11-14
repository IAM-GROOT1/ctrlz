using System.Text;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class SilentFlagsTests
{
    [Fact]
    public void Msi_uses_msiexec_quiet_flags()
    {
        using var temp = new TestDirectory();
        var path = temp.PathOf("setup.msi");
        File.WriteAllBytes(path, [0]);
        Assert.Equal(InstallerType.Msi, SilentInstallerCatalog.Detect(path));
        Assert.Contains("/qn", SilentInstallerCatalog.RecommendedArguments(path));
    }

    [Fact]
    public void Inno_signature_is_detected()
    {
        using var temp = new TestDirectory();
        var path = temp.PathOf("installer.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ fake payload Inno Setup Setup Data (5.6.1)"));
        Assert.Equal(InstallerType.InnoSetup, SilentInstallerCatalog.Detect(path));
        Assert.Contains("VERYSILENT", SilentInstallerCatalog.RecommendedArguments(path));
    }

    [Fact]
    public void Nsis_signature_is_detected()
    {
        using var temp = new TestDirectory();
        var path = temp.PathOf("setup.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ NullsoftInstaller"));
        Assert.Equal(InstallerType.Nsis, SilentInstallerCatalog.Detect(path));
        Assert.Equal("/S", SilentInstallerCatalog.RecommendedArguments(path));
    }

    [Fact]
    public void Unknown_exe_is_reported_as_unknown()
    {
        using var temp = new TestDirectory();
        var path = temp.PathOf("app.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ something else"));
        Assert.Equal(InstallerType.Unknown, SilentInstallerCatalog.Detect(path));
        Assert.Equal("/S", SilentInstallerCatalog.RecommendedArguments(path));
    }

    [Theory]
    [InlineData("/S", null)]
    [InlineData("", null)]
    [InlineData("/S\n/other", "single line")]
    [InlineData("/S\0hidden", "single line")]
    public void Custom_switches_are_validated(string arguments, string? expectedFragment)
    {
        var error = SilentInstallerCatalog.ValidateCustomArguments(arguments);
        if (expectedFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Contains(expectedFragment, error);
        }
    }

    [Fact]
    public void Display_command_uses_msiexec_for_msi()
    {
        var command = SilentInstallerCatalog.BuildDisplayCommand(@"C:\Temp\setup.msi", "/qn");
        Assert.StartsWith("msiexec.exe /i", command);
        Assert.EndsWith("/qn", command);
    }
}
