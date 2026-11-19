// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>
/// Source of the raw GitHub Releases JSON. Abstracted so the updater can be tested against
/// fixed payloads without touching the network.
/// </summary>
public interface IReleaseFeed
{
    Task<string> GetReleasesJsonAsync(CancellationToken cancellationToken = default);
}
