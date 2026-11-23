// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public sealed class UpdateProbationTests : IDisposable
{
    private static readonly DateTime Started = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestDirectory _temp = new();
    private readonly HashSet<int> _running = [];

    public void Dispose() => _temp.Dispose();

    private string Target => _temp.PathOf("app", "CTRL+Z.exe");

    private string Backup => Target + ".old";

    private UpdateProbation Probation() =>
        new(_temp.PathOf("state", UpdateProbation.FileName), TestFactory.Log, (pid, _) => _running.Contains(pid));

    /// <summary>The state right after the helper swapped builds and before the new one starts.</summary>
    private UpdateProbation Begin(bool withCompanion = false)
    {
        _temp.Write("app/CTRL+Z.exe", "new");
        _temp.Write("app/CTRL+Z.exe.old", "old");
        if (withCompanion)
        {
            _temp.Write("app/ctrlz.exe", "new cli");
            _temp.Write("app/ctrlz.exe.old", "old cli");
        }

        var probation = Probation();
        probation.Begin(new UpdateProbationRecord
        {
            Version = "1.5.0",
            TargetExe = Target,
            BackupExe = Backup,
            CompanionTarget = withCompanion ? _temp.PathOf("app", "ctrlz.exe") : null,
            CompanionBackup = withCompanion ? _temp.PathOf("app", "ctrlz.exe.old") : null
        });
        return probation;
    }

    [Fact]
    public void Without_a_record_a_launch_is_not_watched()
    {
        Assert.Equal(ProbationStartup.None, Probation().OnLaunch(Target, 10, Started));
    }

    [Fact]
    public void One_loaded_launch_keeps_the_previous_build()
    {
        var probation = Begin();

        Assert.Equal(ProbationStartup.Watching, probation.OnLaunch(Target, 10, Started));
        Assert.False(probation.MarkLoaded(Target, 10));

        Assert.True(File.Exists(Backup));
        Assert.Equal(1, probation.Read()!.LoadedLaunches);
    }

    [Fact]
    public void Two_loaded_launches_remove_the_previous_build_and_the_record()
    {
        var probation = Begin(withCompanion: true);

        probation.OnLaunch(Target, 10, Started);
        probation.MarkLoaded(Target, 10);
        probation.OnLaunch(Target, 11, Started);
        Assert.True(probation.MarkLoaded(Target, 11));

        Assert.False(File.Exists(Backup));
        Assert.False(File.Exists(_temp.PathOf("app", "ctrlz.exe.old")));
        Assert.Equal("new", File.ReadAllText(Target));
        Assert.Null(probation.Read());
    }

    [Fact]
    public void A_launch_that_never_showed_its_window_is_rolled_back_on_the_next_start()
    {
        var probation = Begin(withCompanion: true);

        // First launch loads; the second starts and dies before its window.
        probation.OnLaunch(Target, 10, Started);
        probation.MarkLoaded(Target, 10);
        probation.OnLaunch(Target, 11, Started);

        Assert.Equal(ProbationStartup.RolledBack, probation.OnLaunch(Target, 12, Started));

        Assert.Equal("old", File.ReadAllText(Target));
        Assert.Equal("new", File.ReadAllText(UpdateProbation.FailedPath(Target)));
        Assert.Equal("old cli", File.ReadAllText(_temp.PathOf("app", "ctrlz.exe")));
        Assert.False(File.Exists(Backup));
        Assert.Null(probation.Read());
    }

    [Fact]
    public void A_copy_that_is_still_starting_is_not_rolled_back()
    {
        var probation = Begin();
        probation.OnLaunch(Target, 10, Started);
        _running.Add(10);

        Assert.Equal(ProbationStartup.None, probation.OnLaunch(Target, 11, Started));

        Assert.Equal("new", File.ReadAllText(Target));
        Assert.True(File.Exists(Backup));
    }

    [Fact]
    public void Only_the_watched_launch_counts_as_loaded()
    {
        var probation = Begin();
        probation.OnLaunch(Target, 10, Started);

        Assert.False(probation.MarkLoaded(Target, 99));
        Assert.Equal(0, probation.Read()!.LoadedLaunches);
    }

    [Fact]
    public void A_record_for_another_copy_is_ignored()
    {
        var probation = Begin();
        var elsewhere = _temp.PathOf("other", "CTRL+Z.exe");

        Assert.Equal(ProbationStartup.None, probation.OnLaunch(elsewhere, 10, Started));
        Assert.NotNull(probation.Read());
    }

    [Fact]
    public void A_missing_backup_ends_the_probation()
    {
        var probation = Begin();
        File.Delete(Backup);

        Assert.Equal(ProbationStartup.None, probation.OnLaunch(Target, 10, Started));
        Assert.Null(probation.Read());
    }

    [Fact]
    public void The_helper_uses_the_data_folder_of_the_portable_app_it_updates()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CTRLZ_DATA_DIR")))
        {
            return; // An override wins for every exe; nothing to compare.
        }

        _temp.Write("app/portable.flag", string.Empty);

        var paths = CtrlZ.Core.Infrastructure.AppPaths.CreateFor(Target);

        Assert.Equal(_temp.PathOf("app", "Data"), paths.Root);
    }

    [Fact]
    public void The_set_aside_build_is_removed_on_a_later_start()
    {
        _temp.Write("app/CTRL+Z.exe.failed", "new");

        UpdateProbation.RemoveFailedBuild(Target);

        Assert.False(File.Exists(UpdateProbation.FailedPath(Target)));
    }
}
