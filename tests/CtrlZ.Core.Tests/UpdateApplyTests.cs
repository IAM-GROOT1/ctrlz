using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class UpdateApplyTests
{
    private const string InstalledFolder = @"C:\Users\me\AppData\Local\Programs\CTRL+Z";

    // ---- Installed vs portable detection ------------------------------------------

    [Fact]
    public void Exe_inside_the_installed_folder_is_installed()
    {
        var kind = InstallKindDetector.Detect(
            InstalledFolder + @"\CTRL+Z.exe",
            InstalledFolder,
            portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Installed, kind);
    }

    [Fact]
    public void Detection_ignores_case_and_trailing_separators()
    {
        var kind = InstallKindDetector.Detect(
            @"c:\users\ME\appdata\local\programs\ctrl+z\CTRL+Z.exe",
            InstalledFolder + @"\",
            portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Installed, kind);
    }

    [Fact]
    public void Exe_outside_the_installed_folder_is_portable()
    {
        var kind = InstallKindDetector.Detect(
            @"D:\Tools\CTRL+Z\CTRL+Z.exe",
            InstalledFolder,
            portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Portable, kind);
    }

    [Fact]
    public void No_uninstall_entry_means_portable()
    {
        var kind = InstallKindDetector.Detect(@"D:\Tools\CTRL+Z\CTRL+Z.exe", null, portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Portable, kind);
    }

    [Fact]
    public void Portable_flag_wins_over_an_installed_folder()
    {
        var kind = InstallKindDetector.Detect(
            InstalledFolder + @"\CTRL+Z.exe",
            InstalledFolder,
            portableFlagPresent: true);

        Assert.Equal(UpdateInstallKind.Portable, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_exe_path_is_portable(string? exePath)
    {
        var kind = InstallKindDetector.Detect(exePath, InstalledFolder, portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Portable, kind);
    }

    [Fact]
    public void A_subfolder_of_the_install_location_is_not_treated_as_installed()
    {
        var kind = InstallKindDetector.Detect(
            InstalledFolder + @"\sub\CTRL+Z.exe",
            InstalledFolder,
            portableFlagPresent: false);

        Assert.Equal(UpdateInstallKind.Portable, kind);
    }

    // ---- Apply handoff files -------------------------------------------------------

    [Fact]
    public void Apply_request_round_trips()
    {
        var directory = new TestDirectory();
        try
        {
            var path = directory.PathOf("apply.json");
            UpdateApplyFiles.WriteRequest(path, new UpdateApplyRequest
            {
                Kind = nameof(UpdateInstallKind.Installed),
                DownloadedFile = @"C:\Temp\setup.exe",
                TargetExe = InstalledFolder + @"\CTRL+Z.exe",
                ExpectedSha256 = new string('a', 64),
                Version = "1.2.0",
                WaitForProcessId = 4242
            });

            var read = UpdateApplyFiles.ReadRequest(path);

            Assert.NotNull(read);
            Assert.Equal("Installed", read!.Kind);
            Assert.Equal(@"C:\Temp\setup.exe", read.DownloadedFile);
            Assert.Equal("1.2.0", read.Version);
            Assert.Equal(4242, read.WaitForProcessId);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Apply_result_round_trips()
    {
        var directory = new TestDirectory();
        try
        {
            var path = directory.PathOf("result.json");
            UpdateApplyFiles.WriteResult(path, UpdateApplyResult.Failed("it broke"));

            var read = UpdateApplyFiles.ReadResult(path);

            Assert.NotNull(read);
            Assert.False(read!.Success);
            Assert.Equal("it broke", read.Message);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Missing_or_malformed_handoff_files_read_as_null()
    {
        var directory = new TestDirectory();
        try
        {
            Assert.Null(UpdateApplyFiles.ReadRequest(directory.PathOf("absent.json")));

            var broken = directory.Write("broken.json", "{ not json");
            Assert.Null(UpdateApplyFiles.ReadRequest(broken));
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Deleting_a_result_marker_is_idempotent()
    {
        var directory = new TestDirectory();
        try
        {
            var path = directory.PathOf("result.json");
            UpdateApplyFiles.DeleteIfExists(path);

            UpdateApplyFiles.WriteResult(path, UpdateApplyResult.Ok("1.2.0"));
            UpdateApplyFiles.DeleteIfExists(path);
            UpdateApplyFiles.DeleteIfExists(path);

            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Dispose();
        }
    }
}
