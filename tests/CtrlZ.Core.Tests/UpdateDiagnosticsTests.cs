// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class UpdateDiagnosticsTests
{
    private const string TokenVariable = "CTRLZ_TEST_GITHUB_TOKEN";

    // ---- a: a timeout must never be reported as a user cancellation -----------------

    [Fact]
    public void A_timeout_is_not_reported_as_a_user_cancellation()
    {
        var message = UpdateFailureMessages.ForCancellation(userRequested: false);

        Assert.Equal(UpdateFailureMessages.TimedOut, message);
        Assert.Contains("could not be downloaded in time", message);
        Assert.Contains("Nothing was changed", message);
        Assert.DoesNotContain("cancelled", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_real_cancellation_is_reported_as_a_cancellation()
    {
        var message = UpdateFailureMessages.ForCancellation(userRequested: true);

        Assert.Contains("cancelled", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("in time", message);
    }

    // ---- b: the feed's rate-limit text never mentions tokens -----------------------

    [Fact]
    public void The_rate_limit_message_is_plain_and_token_free()
    {
        Assert.Equal(UpdateFailureMessages.RateLimited, UpdateFailureMessages.RateLimitedAt(null));
        Assert.Contains("limiting update checks", UpdateFailureMessages.RateLimited);

        foreach (var banned in new[] { "GITHUB_TOKEN", "token", "Authorization", "set " })
        {
            Assert.DoesNotContain(banned, UpdateFailureMessages.RateLimited, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_rate_limit_message_includes_the_reset_time_when_known()
    {
        var reset = new DateTimeOffset(2026, 11, 22, 14, 30, 0, TimeSpan.Zero);

        var message = UpdateFailureMessages.RateLimitedAt(reset);

        Assert.Contains("14:30", message);
        Assert.Contains("UTC", message);
        Assert.DoesNotContain("token", message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- c: only the GitHub API over HTTPS may receive a token ---------------------

    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases", true)]
    [InlineData("http://api.github.com/repos/x/y/releases", false)]
    [InlineData("https://evil.example/api.github.com", false)]
    [InlineData("https://github.com/x/y", false)]
    public void Only_the_github_api_over_https_is_trusted(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateFeedAuthorization.IsTrustedHost(new Uri(url)));
    }

#if DEBUG
    [Fact]
    public void A_debug_build_reads_the_test_token_for_the_github_api_only()
    {
        var original = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenVariable, "test-token-value");

            Assert.Equal("test-token-value", UpdateFeedAuthorization.For(new Uri("https://api.github.com/repos/x")));
            Assert.Null(UpdateFeedAuthorization.For(new Uri("https://evil.example/repos")));
            Assert.Null(UpdateFeedAuthorization.For(new Uri("http://api.github.com/repos/x")));
            Assert.Null(UpdateFeedAuthorization.For(new Uri("https://github.com/x")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }
#else
    [Fact]
    public void A_release_build_never_reads_the_test_token()
    {
        var original = Environment.GetEnvironmentVariable(TokenVariable);
        try
        {
            // Even with the variable set, a Release build has no path that returns it.
            Environment.SetEnvironmentVariable(TokenVariable, "test-token-value");

            Assert.Null(UpdateFeedAuthorization.For(new Uri("https://api.github.com/repos/x")));
            Assert.Null(UpdateFeedAuthorization.For(new Uri("https://evil.example/repos")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }
#endif

    [Fact]
    public void A_redacted_log_line_never_contains_the_token()
    {
        var exception = new InvalidOperationException("boom with a path C:\\Users\\someone\\x and a token ghp_example123");

        var line = CtrlZ.Core.Logging.LogPrivacy.Describe(exception);

        Assert.DoesNotContain("C:\\Users", line);
        Assert.Contains("<path>", line);
    }
}
