using CtrlZ.App.Services;

namespace CtrlZ.Core.Tests;

public class FreeCoreStartupTests
{
    [Fact]
    public void App_services_start_with_no_pro_module()
    {
        using var temp = new TestDirectory();
        var old = Environment.GetEnvironmentVariable("CTRLZ_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CTRLZ_DATA_DIR", temp.PathOf("data"));
            using var services = AppServices.Create(temp.CreateDirectory("app"));

            Assert.False(services.ProModule.IsLoaded);
            Assert.False(services.ProModule.IsInstalled);
            Assert.NotNull(services.License);
            Assert.Equal(CtrlZ.Core.Licensing.LicenseStatus.Missing, services.License.Snapshot.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CTRLZ_DATA_DIR", old);
        }
    }
}
