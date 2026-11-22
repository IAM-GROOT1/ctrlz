// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using System.Text;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>
/// Remembers, per profile, which command texts a human actually reviewed and enabled. The record
/// lives in the app's state folder and holds hashes only: editing a command in the profile changes
/// its hash, so the approval stops matching and the command is disabled again until it is reviewed.
/// </summary>
public sealed class PostInstallApprovalStore
{
    private readonly string _path;
    private Dictionary<string, List<string>> _approved = new(StringComparer.Ordinal);

    public PostInstallApprovalStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>A stable hash of everything that makes a command what it is.</summary>
    public static string HashOf(PostInstallCommand command)
    {
        var text = string.Join(
            "\u001f",
            command.Executable ?? string.Empty,
            string.Join("\u001e", command.Arguments ?? new List<string>()),
            command.WorkingDirectory ?? string.Empty,
            command.RequiresElevation ? "elevated" : "normal");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public bool IsApproved(string profileId, PostInstallCommand command)
    {
        if (!_approved.TryGetValue(profileId, out var hashes))
        {
            return false;
        }

        return hashes.Contains(HashOf(command), StringComparer.Ordinal);
    }

    public void Approve(string profileId, PostInstallCommand command)
    {
        if (!_approved.TryGetValue(profileId, out var hashes))
        {
            hashes = new List<string>();
            _approved[profileId] = hashes;
        }

        var hash = HashOf(command);
        if (!hashes.Contains(hash, StringComparer.Ordinal))
        {
            hashes.Add(hash);
            Save();
        }
    }

    public void Forget(string profileId)
    {
        if (_approved.Remove(profileId))
        {
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(_path),
                CtrlZ.Core.Infrastructure.JsonDefaults.Profile);

            if (loaded is not null)
            {
                _approved = new Dictionary<string, List<string>>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            // Unreadable approvals mean nothing is approved, which fails safe.
            _approved = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _path,
                System.Text.Json.JsonSerializer.Serialize(_approved, CtrlZ.Core.Infrastructure.JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Saving is best effort; a lost approval only means the user reviews again.
        }
    }
}
