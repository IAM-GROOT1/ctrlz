// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void Parses_core_versions(string text, int major, int minor, int patch)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.False(version.IsPreRelease);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "alpha")]
    [InlineData("1.0.0-alpha.1", "alpha.1")]
    [InlineData("1.0.0-0.3.7", "0.3.7")]
    [InlineData("1.0.0-x.7.z.92", "x.7.z.92")]
    public void Parses_pre_release_labels(string text, string expected)
    {
        var version = SemanticVersion.Parse(text);

        Assert.True(version.IsPreRelease);
        Assert.Equal(expected, string.Join('.', version.PreRelease));
    }

    [Fact]
    public void Parses_build_metadata_without_treating_it_as_precedence()
    {
        var withMetadata = SemanticVersion.Parse("1.2.3+build.99");
        var without = SemanticVersion.Parse("1.2.3");

        Assert.Equal("build.99", withMetadata.BuildMetadata);
        Assert.Equal(0, withMetadata.CompareTo(without));
        Assert.Equal(withMetadata, without);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3-al pha")]
    [InlineData("v")]
    public void Rejects_invalid_versions(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out var version));
        Assert.Null(version);
    }

    [Theory]
    // Semantic Versioning 2.0 section 11 precedence chain.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.1", "1.1.0")]
    [InlineData("1.1.0", "2.0.0")]
    [InlineData("1.1.3", "1.2.0")]
    [InlineData("1.2.0-rc.1", "1.2.0")]
    public void Orders_versions_by_specification_precedence(string lower, string higher)
    {
        var left = SemanticVersion.Parse(lower);
        var right = SemanticVersion.Parse(higher);

        Assert.True(left < right, $"expected {lower} < {higher}");
        Assert.True(right > left, $"expected {higher} > {lower}");
        Assert.True(right.IsNewerThan(left));
        Assert.False(left.IsNewerThan(right));
    }

    [Fact]
    public void Release_outranks_its_own_pre_release()
    {
        var beta = SemanticVersion.Parse("1.3.0-beta.1");
        var stable = SemanticVersion.Parse("1.3.0");

        Assert.True(stable.IsNewerThan(beta));
        Assert.False(beta.IsNewerThan(stable));
    }

    [Fact]
    public void Pre_release_does_not_outrank_an_older_release()
    {
        var upcoming = SemanticVersion.Parse("1.3.0-beta.1");
        var current = SemanticVersion.Parse("1.2.5");

        Assert.True(upcoming.IsNewerThan(current));
    }

    [Fact]
    public void Numeric_identifiers_sort_before_alphanumeric_ones()
    {
        var numeric = SemanticVersion.Parse("1.0.0-1");
        var alpha = SemanticVersion.Parse("1.0.0-alpha");

        Assert.True(numeric < alpha);
    }

    [Fact]
    public void Large_numeric_identifiers_do_not_overflow()
    {
        var smaller = SemanticVersion.Parse("1.0.0-99999999999999999999");
        var larger = SemanticVersion.Parse("1.0.0-100000000000000000000");

        Assert.True(smaller < larger);
    }

    [Fact]
    public void Equality_ignores_build_metadata()
    {
        Assert.Equal(SemanticVersion.Parse("1.2.3+a"), SemanticVersion.Parse("1.2.3+b"));
        Assert.NotEqual(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("1.2.4"));
        Assert.NotEqual(SemanticVersion.Parse("1.2.3-alpha"), SemanticVersion.Parse("1.2.3-beta"));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.3.0-beta.1", "1.3.0-beta.1")]
    [InlineData("v2.0.0-rc.2", "2.0.0-rc.2")]
    [InlineData("1.0.0+build.5", "1.0.0+build.5")]
    public void Round_trips_to_string(string input, string expected)
    {
        Assert.Equal(expected, SemanticVersion.Parse(input).ToString());
    }

    [Fact]
    public void Provides_numeric_forms_for_assembly_attributes()
    {
        var version = SemanticVersion.Parse("1.3.0-beta.1");

        Assert.Equal("1.3.0", version.ToNumericString());
        Assert.Equal(new Version(1, 3, 0, 0), version.ToAssemblyVersion());
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.3.0-beta.1", "1.3.0-beta.1")]
    [InlineData("1.3.0-beta.1+abc1234", "1.3.0-beta.1")]
    [InlineData("2.0.0+sha.9f2c1a", "2.0.0")]
    public void Reads_version_from_informational_version(string informational, string expected)
    {
        var parsed = AppVersionInfo.Parse(informational);

        Assert.Equal(expected, parsed.ToString());
    }

    [Fact]
    public void Falls_back_to_numeric_assembly_version()
    {
        var parsed = AppVersionInfo.Parse(null, new Version(1, 4, 2, 0));

        Assert.Equal("1.4.2", parsed.ToString());
    }

    [Fact]
    public void Falls_back_when_informational_version_is_garbage()
    {
        var parsed = AppVersionInfo.Parse("not-a-version", new Version(2, 1, 0, 0));

        Assert.Equal("2.1.0", parsed.ToString());
    }
}
