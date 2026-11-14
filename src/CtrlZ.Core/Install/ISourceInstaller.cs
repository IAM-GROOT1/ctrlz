using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

public interface ISourceInstaller
{
    bool CanHandle(AppSource source);
    Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken);
}
