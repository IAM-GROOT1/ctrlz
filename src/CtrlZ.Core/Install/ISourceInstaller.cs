// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

public interface ISourceInstaller
{
    bool CanHandle(AppSource source);
    Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken);
}
