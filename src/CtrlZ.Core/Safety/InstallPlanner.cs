// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography.X509Certificates;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Describes what an install run would do, without touching the network or the disk beyond
/// checking that a local installer exists and whether it is signed.
/// </summary>
public sealed class InstallPlanner
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool?> _isSigned;
    private readonly Install.DependencyCatalog _catalog;

    public InstallPlanner(
        Func<string, bool>? fileExists = null,
        Func<string, bool?>? isSigned = null,
        Install.DependencyCatalog? catalog = null)
    {
        _fileExists = fileExists ?? SafeFileExists;
        _isSigned = isSigned ?? ProbeSignature;
        _catalog = catalog ?? Install.DependencyCatalog.Empty;
    }

    /// <summary>Describes the run in exactly the order the install engine uses.</summary>
    public InstallPlan Build(AppProfile profile, string profileName)
    {
        var items = new List<InstallPlanItem>();
        var plan = new Install.InstallOrderPlanner(_catalog).Plan(profile);
        var order = 0;

        foreach (var app in plan.Ordered)
        {
            order++;
            var item = BuildItem(app, order);
            if (Install.InstallOrderPlanner.IsKnownAutoDependency(app, _catalog))
            {
                var neededBy = plan.Dependencies
                    .Where(pair => pair.Value.Contains(app.Id))
                    .Select(pair => plan.Ordered.FirstOrDefault(other => other.Id == pair.Key)?.Name)
                    .Where(name => name is not null)
                    .ToList();
                item = item with
                {
                    Notes = [.. item.Notes, "Added automatically" + (neededBy.Count > 0 ? " because " + string.Join(", ", neededBy) + " need" + (neededBy.Count == 1 ? "s" : string.Empty) + " it." : ".")]
                };
            }

            items.Add(item);
        }

        // Listed at the end, exactly like the engine's queue, with the reason they will not install.
        foreach (var skipped in plan.Skipped)
        {
            var app = profile.Apps.FirstOrDefault(candidate => candidate.Id == skipped.AppId);
            if (app is null)
            {
                continue;
            }

            order++;
            var item = BuildItem(app, order);
            items.Add(item with
            {
                NotInstalledReason = skipped.Reason,
                Warnings = [.. item.Warnings, "Will not install: " + skipped.Reason]
            });
        }

        var planWarnings = new List<string>
        {
            "Apps install one after another in the order below. Known dependencies install first, and an app whose dependency fails is not attempted."
        };

        planWarnings.AddRange(plan.Cycles.Select(cycle => $"Install-order loop: {cycle}."));
        planWarnings.AddRange(plan.MissingReferences);

        if (items.All(item => item.NotInstalledReason is not null))
        {
            planWarnings.Add("There is nothing enabled to install.");
        }
        else if (items.Any(item => item.Warnings.Count > 0))
        {
            var affected = items.Count(item => item.Warnings.Count > 0);
            planWarnings.Add($"{affected} app{(affected == 1 ? " has" : "s have")} a warning — review before running.");
        }

        return new InstallPlan
        {
            ProfileName = profileName,
            Items = items,
            PlanWarnings = planWarnings
        };
    }

    public InstallPlanItem BuildItem(AppEntry app, int order)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var description = string.Empty;
        var silentArgs = app.SilentArgs ?? string.Empty;
        var requiresAdmin = false;

        switch (app.Source)
        {
            case AppSource.Winget:
            case AppSource.MicrosoftStore:
                description = string.IsNullOrWhiteSpace(app.PackageId)
                    ? "winget (no package id)"
                    : $"winget {app.PackageId}";
                silentArgs = app.SilentArgs ?? "--silent --accept-package-agreements --accept-source-agreements";
                notes.Add("winget decides whether the package needs elevation.");

                if (string.IsNullOrWhiteSpace(app.PackageId))
                {
                    warnings.Add("No winget package id is recorded.");
                }

                break;

            case AppSource.GitHub:
                description = string.IsNullOrWhiteSpace(app.GitHubRepo)
                    ? "GitHub (no repository)"
                    : $"GitHub {app.GitHubRepo}" +
                      (string.IsNullOrWhiteSpace(app.GitHubAsset) ? string.Empty : $" · {app.GitHubAsset}");
                requiresAdmin = true;
                silentArgs = app.SilentArgs ?? SilentArgumentsFor(app.GitHubAsset ?? app.FileName);

                if (string.IsNullOrWhiteSpace(app.GitHubRepo))
                {
                    warnings.Add("No GitHub repository is recorded.");
                }

                if (string.IsNullOrWhiteSpace(app.ExpectedSha256))
                {
                    warnings.Add("No SHA-256 recorded, so the download cannot be verified.");
                }

                break;

            case AppSource.DirectUrl:
                description = Uri.TryCreate(app.DownloadUrl, UriKind.Absolute, out var url)
                    ? $"Download from {url.Host}"
                    : "Download (no valid URL)";
                requiresAdmin = true;
                silentArgs = app.SilentArgs ?? SilentArgumentsFor(app.FileName ?? app.DownloadUrl);

                if (!Uri.TryCreate(app.DownloadUrl, UriKind.Absolute, out _))
                {
                    warnings.Add("The download URL is missing or invalid.");
                }

                if (string.IsNullOrWhiteSpace(app.ExpectedSha256))
                {
                    warnings.Add("No SHA-256 recorded, so the download cannot be verified.");
                }

                break;

            case AppSource.LocalFile:
                requiresAdmin = true;
                var exists = !string.IsNullOrWhiteSpace(app.LocalPath) && _fileExists(app.LocalPath);
                description = exists
                    ? $"Local file {Path.GetFileName(app.LocalPath)}"
                    : "Local file (missing)";

                if (!exists)
                {
                    warnings.Add("The installer file is not on this PC.");
                }
                else
                {
                    silentArgs = app.SilentArgs ?? SilentArgumentsFor(app.LocalPath!);
                    if (_isSigned(app.LocalPath!) == false)
                    {
                        warnings.Add("The installer is not code-signed.");
                    }
                }

                break;

            default:
                description = "Unknown source";
                warnings.Add("This app has an unsupported source.");
                break;
        }

        if (string.IsNullOrWhiteSpace(silentArgs))
        {
            silentArgs = "installer defaults";
        }

        return new InstallPlanItem
        {
            AppId = app.Id,
            Name = app.Name,
            Source = app.Source,
            SourceDescription = description,
            PackageId = app.PackageId,
            Version = app.Version,
            SilentArgs = silentArgs,
            RequiresAdmin = requiresAdmin,
            Order = order,
            Notes = notes,
            Warnings = warnings
        };
    }

    private static string SilentArgumentsFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "installer defaults";
        }

        try
        {
            var recommended = SilentInstallerCatalog.RecommendedArguments(path);
            return string.IsNullOrWhiteSpace(recommended) ? "installer defaults" : recommended;
        }
        catch
        {
            return "installer defaults";
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when signed, false when present but unsigned, null when it cannot be read.</summary>
    private static bool? ProbeSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        catch
        {
            return null;
        }
    }
}
