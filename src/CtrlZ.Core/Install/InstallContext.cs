// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

public sealed class NullInstallLogSink : IInstallLogSink
{
    public static readonly NullInstallLogSink Instance = new();
    public void Line(string message) { }
    public void Progress(InstallProgress progress) { }
}

public sealed class InstallContext
{
    public InstallContext(AppPaths paths, IInstallLogSink sink, IAppLog log, bool isElevated)
    {
        Paths = paths;
        Sink = sink;
        Log = log;
        IsElevated = isElevated;
    }

    public AppPaths Paths { get; }
    public IInstallLogSink Sink { get; }
    public IAppLog Log { get; }
    public bool IsElevated { get; }

    public void Line(string message)
    {
        Sink.Line(message);
    }

    public void Report(AppEntry app, InstallStatus status, string? detail = null, double? fraction = null)
    {
        Sink.Progress(new InstallProgress(app.Name, status, detail, fraction));
    }

    public void Warn(string message) => Log.Write(LogLevel.Warning, message);
}
