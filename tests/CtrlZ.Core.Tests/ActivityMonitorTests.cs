using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class ActivityMonitorTests
{
    [Fact]
    public void Starts_idle()
    {
        var monitor = new AppActivityMonitor();

        Assert.False(monitor.IsBusy);
        Assert.Null(monitor.CurrentActivity);
    }

    [Fact]
    public void Beginning_an_activity_marks_it_busy_until_disposed()
    {
        var monitor = new AppActivityMonitor();

        var scope = monitor.Begin("Installing apps");
        Assert.True(monitor.IsBusy);
        Assert.Equal("Installing apps", monitor.CurrentActivity);

        scope.Dispose();
        Assert.False(monitor.IsBusy);
        Assert.Null(monitor.CurrentActivity);
    }

    [Fact]
    public void Nested_activities_keep_it_busy_until_the_last_one_ends()
    {
        var monitor = new AppActivityMonitor();

        var outer = monitor.Begin("Installing apps");
        var inner = monitor.Begin("Backing up browsers");

        inner.Dispose();
        Assert.True(monitor.IsBusy);

        outer.Dispose();
        Assert.False(monitor.IsBusy);
    }

    [Fact]
    public void Disposing_twice_does_not_release_someone_elses_scope()
    {
        var monitor = new AppActivityMonitor();

        var first = monitor.Begin("first");
        var second = monitor.Begin("second");
        first.Dispose();
        first.Dispose();

        Assert.True(monitor.IsBusy);

        second.Dispose();
        Assert.False(monitor.IsBusy);
    }

    [Fact]
    public void Busy_changed_fires_on_transitions_only()
    {
        var monitor = new AppActivityMonitor();
        var events = 0;
        monitor.BusyChanged += (_, _) => events++;

        var first = monitor.Begin("a");
        Assert.Equal(1, events);

        var second = monitor.Begin("b");
        Assert.Equal(2, events);

        first.Dispose();
        Assert.Equal(3, events);

        second.Dispose();
        Assert.Equal(4, events);
    }

    [Fact]
    public void Blank_activity_gets_a_readable_label()
    {
        var monitor = new AppActivityMonitor();

        using var scope = monitor.Begin("   ");

        Assert.Equal("Working", monitor.CurrentActivity);
    }

    [Fact]
    public void Using_scope_pattern_releases_on_exception()
    {
        var monitor = new AppActivityMonitor();

        try
        {
            using (monitor.Begin("Installing apps"))
            {
                throw new InvalidOperationException("boom");
            }
        }
        catch (InvalidOperationException)
        {
            // Expected.
        }

        Assert.False(monitor.IsBusy);
    }
}
