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
}

/// <summary>
/// Puts dependencies before the apps that need them and honours each app's "install after" list.
/// Missing references are reported and ignored; a cycle stops only the apps inside it.
/// </summary>
public sealed class InstallOrderPlanner
{
    private readonly DependencyCatalog _catalog;

    public InstallOrderPlanner(DependencyCatalog? catalog = null)
    {
        _catalog = catalog ?? DependencyCatalog.Empty;
    }

    public InstallOrderResult Plan(AppProfile profile, IReadOnlyCollection<string>? failedAppIds = null)
    {
        var failed = new HashSet<string>(failedAppIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var enabled = profile.Apps.Where(app => app.Enabled).ToList();
        var autoAdded = new List<AppEntry>();
        var nodes = new Dictionary<string, AppEntry>(StringComparer.Ordinal);

        foreach (var app in enabled)
        {
            nodes[app.Id] = app;
        }

        // Add a dependency entry for every known dependency a saved app needs, once, and mark it so
        // the user can remove it again.
        foreach (var app in enabled)
        {
            foreach (var dependency in _catalog.For(app))
            {
                if (nodes.Values.Any(existing =>
                        existing.IsAutoDependency &&
                        string.Equals(existing.PackageId, dependency.PackageId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var entry = new AppEntry
                {
                    Id = "dep:" + dependency.Id,
                    Name = dependency.Label,
                    Source = AppSource.Winget,
                    PackageId = dependency.PackageId,
                    IsAutoDependency = true,
                    Enabled = true,
                    Category = AppCategory.Other
                };

                nodes[entry.Id] = entry;
                autoAdded.Add(entry);
            }
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var id in nodes.Keys)
        {
            edges[id] = new List<string>();
        }

        // An app depends on the auto-added dependency entries whose catalog rule it matched.
        foreach (var app in enabled)
        {
            foreach (var dependency in _catalog.For(app))
            {
                var dependencyNode = "dep:" + dependency.Id;
                if (nodes.ContainsKey(dependencyNode))
                {
                    edges[app.Id].Add(dependencyNode);
                }
            }

            foreach (var after in app.AfterAppIds)
            {
                if (nodes.ContainsKey(after))
                {
                    edges[app.Id].Add(after);
                }
                else
                {
                    missing.Add($"{app.Name} is set to install after '{after}', which is not in this profile.");
                }
            }
        }

        var ordered = new List<AppEntry>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new List<string>();
        var skipped = new List<SkippedApp>();

        foreach (var id in nodes.Keys)
        {
            Visit(id, new List<string>());
        }

        void Visit(string id, List<string> path)
        {
            if (state.TryGetValue(id, out var current))
            {
                if (current == 2)
                {
                    return;
                }

                if (current == 1)
                {
                    var start = path.IndexOf(id);
                    var cycle = start >= 0 ? path.Skip(start).ToList() : path.ToList();
                    cycle.Add(id);
                    cycles.Add(string.Join(" -> ", cycle.Select(node => nodes[node].Name)));
                    return;
                }
            }

            state[id] = 1;
            path.Add(id);

            foreach (var dependency in edges[id])
            {
                if (failed.Contains(dependency))
                {
                    continue;
                }

                Visit(dependency, path);
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;

            var failedDependency = edges[id].FirstOrDefault(failed.Contains);
            if (failedDependency is not null && !nodes[id].IsAutoDependency)
            {
                skipped.Add(new SkippedApp(
                    nodes[id].Id,
                    nodes[id].Name,
                    $"Needs {nodes[failedDependency].Name}, which failed to install."));
                return;
            }

            ordered.Add(nodes[id]);
        }

        return new InstallOrderResult(ordered, autoAdded, cycles, missing, skipped);
    }
}
