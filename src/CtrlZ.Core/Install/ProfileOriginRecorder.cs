// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using System.Text;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>
/// The single API through which profile content may be loaded for use. Every loader — file open,
/// import, drag and drop, recent list, drift, preview, dry run, command line — must go through
/// <see cref="LoadForUse"/>, which records the origin <b>before</b> the content is read.
/// <para>
/// A profile stays <see cref="ProfileOrigin.Local"/> only while it is still exactly what this machine
/// last saved: the stored hash of that save is compared with the content being loaded, so editing the
/// file outside the app makes it untrusted.
/// </para>
/// </summary>
public static class ProfileOriginRecorder
{
    /// <summary>A stable hash of the profile's content, used to detect external edits.</summary>
    public static string ContentHash(AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var json = System.Text.Json.JsonSerializer.Serialize(profile, CtrlZ.Core.Infrastructure.JsonDefaults.Profile);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>A profile the user just created here: Local, with the hash of what was saved.</summary>
    public static AppProfile RecordCreated(ProfileOriginStore origins, AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(profile);

        origins.Set(profile.Id, ProfileOrigin.Local, ContentHash(profile));
        return profile;
    }

    /// <summary>
    /// The user saved a profile in this app. A profile that was already imported stays imported;
    /// an existing local profile is re-stamped with the new hash.
    /// </summary>
    public static AppProfile RecordSaved(ProfileOriginStore origins, AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(profile);

        var current = origins.Get(profile.Id);

        // Unknown means this profile has no record yet: it is being created here. Imported stays
        // imported, because saving someone else's profile does not make it yours.
        if (current is ProfileOrigin.Local or ProfileOrigin.Unknown)
        {
            origins.Set(profile.Id, ProfileOrigin.Local, ContentHash(profile));
        }
        else
        {
            origins.Set(profile.Id, ProfileOrigin.Imported, null);
        }

        return profile;
    }

    /// <summary>
    /// Any load that is not a save on this machine. Local only if a Local entry exists <b>and</b> the
    /// content still matches the hash recorded at that save; otherwise Imported.
    /// </summary>
    public static AppProfile LoadForUse(ProfileOriginStore origins, AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(profile);

        var origin = origins.Get(profile.Id);
        var savedHash = origins.GetLastSavedHash(profile.Id);
        var matches = origin == ProfileOrigin.Local
                      && !string.IsNullOrEmpty(savedHash)
                      && string.Equals(savedHash, ContentHash(profile), StringComparison.Ordinal);

        origins.Set(profile.Id, matches ? ProfileOrigin.Local : ProfileOrigin.Imported, matches ? savedHash : null);
        return profile;
    }

    /// <summary>The user explicitly confirmed this profile is theirs. Requires the confirmation flag.</summary>
    public static AppProfile MarkAsLocal(ProfileOriginStore origins, AppProfile profile, bool userConfirmed)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(profile);

        if (!userConfirmed)
        {
            return profile;
        }

        origins.Set(profile.Id, ProfileOrigin.Local, ContentHash(profile));
        return profile;
    }

    /// <summary>Trusted only when the entry is explicitly Local.</summary>
    public static bool IsTrusted(ProfileOriginStore origins, AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(profile);

        return origins.Get(profile.Id) == ProfileOrigin.Local;
    }
}
