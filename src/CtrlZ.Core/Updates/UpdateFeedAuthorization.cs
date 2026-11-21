// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>
/// Debug-only Authorization for the release feed, so automated tests can escape GitHub's anonymous
/// quota. Release builds never read the environment variable: the whole path is compiled out, and
/// <see cref="For"/> returns null unconditionally.
/// </summary>
public static class UpdateFeedAuthorization
{
    /// <summary>Only the GitHub API over HTTPS may ever receive a token.</summary>
    public static bool IsTrustedHost(Uri uri) =>
        uri is not null &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

#if DEBUG
    /// <summary>Name of the test-only environment variable. Absent from Release builds.</summary>
    public const string TestTokenVariable = "CTRLZ_TEST_GITHUB_TOKEN";

    /// <summary>Returns the test token for the GitHub API only, or null.</summary>
    public static string? For(Uri uri)
    {
        if (!IsTrustedHost(uri))
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable(TestTokenVariable);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
#else
    /// <summary>Release builds never send a token to the release feed.</summary>
    public static string? For(Uri uri) => null;
#endif
}
