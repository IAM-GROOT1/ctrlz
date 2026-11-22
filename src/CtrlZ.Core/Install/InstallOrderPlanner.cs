// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>Why an app will not be installed even though the profile says it should be.</summary>
public sealed record SkippedApp(string AppId, string Name, string Reason);

/// <summary>
/// The order apps will install in, plus everything that could not be ordered. A cycle is reported
/// with the apps involved instead of being broken silently.
/// </summary>
public sealed record InstallOrderResult(
    IReadOnlyList<AppEntry> Ordered,
    IReadOnlyList<AppEntry> AutoAdded,
    IReadOnlyList<string> Cycles,
    IReadOnlyList<string> MissingReferences,
    IReadOnlyList<SkippedApp> Skipped)
{
    public bool IsOrderable => Cycles.Count == 0;

    /// <summary>For each app id in the plan, the app ids that must succeed before it runs.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>
/// Puts dependencies before the apps that need them and honours each app's "install after" list.
/// Missing references are reported and ignored. A cycle stops the apps inside it and the apps that
/// need them; everything else still installs. An app already in the profile with a dependency's
/// package id satisfies it; a disabled one means the user removed that dependency, so it is not
/// added back.
/// </summary>
public sealed class InstallOrderPlanner
{
    public const string AutoDependencyIdPrefix = "dep:";

    private readonly DependencyCatalog _catalog;

    public InstallOrderPlanner(DependencyCatalog? catalog = null)
    {
        _catalog = catalog ?? DependencyCatalog.Empty;
    }

    public InstallOrderResult Plan(AppProfile profile, IReadOnlyCollection<string>? failedAppIds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var failed = new HashSet<string>(failedAppIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var enabled = profile.Apps.Where(app => app.Enabled).ToList();
        var autoAdded = new List<AppEntry>();
        var nodes = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var app in enabled.Where(app => !nodes.ContainsKey(app.Id)))
        {
            nodes[app.Id] = app;
            order.Add(app.Id);
        }

        var edges = nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var missing = new List<string>();

        // Known dependencies: reuse a matching app already in the profile, respect a disabled one as
        // removed by the user, otherwise add one marked entry per dependency.
        foreach (var app in enabled)
        {
            foreach (var dependency in _catalog.For(app))
            {
                // An entry that already uses this dependency's id, enabled or not, is the dependency:
                // a disabled one was turned off by the user and is never shadowed by a new entry.
                // Otherwise an app with the same package satisfies it, preferring an enabled one.
                var autoId = AutoDependencyIdPrefix + dependency.Id;
                var existing =
                    profile.Apps.FirstOrDefault(candidate => candidate.Id == autoId) ??
                    profile.Apps
                        .Where(candidate =>
                            !ReferenceEquals(candidate, app) &&
                            string.Equals(candidate.PackageId, dependency.PackageId, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(candidate => candidate.Enabled)
                        .FirstOrDefault();

                string? target;
                if (existing is not null)
                {
                    target = existing.Enabled && nodes.ContainsKey(existing.Id) ? existing.Id : null;
                }
                else
                {
                    target = autoId;
                    if (!nodes.ContainsKey(target))
                    {
                        var entry = new AppEntry
                        {
                            Id = target,
                            Name = dependency.Label,
                            Source = AppSource.Winget,
                            PackageId = dependency.PackageId,
                            IsAutoDependency = true,
                            Enabled = true,
                            Category = AppCategory.Other
                        };

                        nodes[target] = entry;
                        edges[target] = new List<string>();
                        autoAdded.Add(entry);
                        order.Insert(0, target);
                    }
                }

                if (target is not null && !edges[app.Id].Contains(target))
                {
                    edges[app.Id].Add(target);
                }
            }

            foreach (var after in app.AfterAppIds ?? new List<string>())
            {
                if (nodes.ContainsKey(after))
                {
                    if (!edges[app.Id].Contains(after))
                    {
                        edges[app.Id].Add(after);
                    }
                }
                else
                {
                    missing.Add($"{app.Name} is set to install after '{after}', which is not in this run.");
                }
            }
        }

        var cycleMembers = FindCycles(order, edges, out var cycles, nodes);

        // Depth-first in profile order: dependencies first, then the app. Blocked apps are skipped
        // with the reason, and anything that needs a blocked app is skipped too.
        var ordered = new List<AppEntry>();
        var skipped = new List<SkippedApp>();
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in order)
        {
            Place(id);
        }

        void Place(string id)
        {
            if (!done.Add(id))
            {
                return;
            }

            var app = nodes[id];
            if (cycleMembers.Contains(id))
            {
                Block(id, "It is part of an install-order loop.");
                return;
            }

            foreach (var dependency in edges[id])
            {
                Place(dependency);
            }

            var reason = edges[id]
                .Select(dependency =>
                    failed.Contains(dependency) ? $"Needs {nodes[dependency].Name}, which failed to install." :
                    blocked.ContainsKey(dependency) ? $"Needs {nodes[dependency].Name}, which will not install." :
                    null)
                .FirstOrDefault(text => text is not null);

            if (reason is not null)
            {
                Block(id, reason);
                return;
            }

            ordered.Add(app);
        }

        void Block(string id, string reason)
        {
            blocked[id] = reason;
            skipped.Add(new SkippedApp(id, nodes[id].Name, reason));
        }

        var dependencies = edges.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToList(),
            StringComparer.Ordinal);

        return new InstallOrderResult(ordered, autoAdded, cycles, missing, skipped) { Dependencies = dependencies };
    }

    /// <summary>
    /// True only for an entry the planner itself would create: the flag alone comes from the profile
    /// file, so an imported profile cannot borrow the "added automatically" label for its own apps.
    /// </summary>
    public static bool IsKnownAutoDependency(AppEntry app, DependencyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(catalog);

        return app.IsAutoDependency && catalog.Dependencies.Any(dependency =>
            app.Id == AutoDependencyIdPrefix + dependency.Id &&
            string.Equals(app.PackageId, dependency.PackageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every node that sits on a cycle (strongly connected, Tarjan), with a readable description of each cycle.</summary>
    private static HashSet<string> FindCycles(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, List<string>> edges,
        out List<string> descriptions,
        IReadOnlyDictionary<string, AppEntry> nodes)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in order)
        {
            if (!indices.ContainsKey(id))
            {
                Connect(id);
            }
        }

        void Connect(string id)
        {
            indices[id] = low[id] = index++;
            stack.Push(id);
            onStack.Add(id);

            foreach (var next in edges[id])
            {
                if (!indices.ContainsKey(next))
                {
                    Connect(next);
                    low[id] = Math.Min(low[id], low[next]);
                }
                else if (onStack.Contains(next))
                {
                    low[id] = Math.Min(low[id], indices[next]);
                }
            }

            if (low[id] != indices[id])
            {
                return;
            }

            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            }
            while (popped != id);

            if (component.Count > 1 || edges[id].Contains(id))
            {
                component.Reverse();
                members.UnionWith(component);
                found.Add(string.Join(" -> ", component.Append(component[0]).Select(node => nodes[node].Name)));
            }
        }

        descriptions = found;
        return members;
    }
}
