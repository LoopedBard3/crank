// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Crank.Agent;
using Xunit;

namespace Microsoft.Crank.UnitTests;

public class BuildCacheClientTests
{
    [Theory]
    [InlineData(null, "ci", "ci", true)]
    [InlineData("", "CI", "ci", true)]
    [InlineData("ci", "current", "ci", true)]
    [InlineData("latest", "ci", "latest", false)]
    [InlineData("current", "ci", "current", false)]
    [InlineData("edge", "ci", "edge", false)]
    [InlineData("11.0.0-rc.1", "ci", "11.0.0-rc.1", false)]
    [InlineData(BuildCacheTestFixture.RuntimeSha, "latest", BuildCacheTestFixture.RuntimeSha, true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", "current", BuildCacheTestFixture.AspNetCoreSha, true)]
    public void SelectorsAreIndependentAndExplicitLatestIsAlwaysFeed(string requested, string channel, string value, bool bcs)
    {
        var selector = FrameworkSelector.Parse(requested, channel);
        Assert.Equal(value, selector.Value);
        Assert.Equal(bcs, selector.IsBuildCache);
        Assert.Equal(requested ?? "", selector.Requested);
    }

    [Theory]
    [InlineData("abcdef01", false)]
    [InlineData("123456789012345678901234567890123456789", false)]
    [InlineData("1234567890123456789012345678901234567890", true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", true)]
    [InlineData("11.0.0", false)]
    [InlineData(null, false)]
    public void CommitPinsRequireFullSha(string pin, bool expected) => Assert.Equal(expected, BuildCacheClient.IsCommitSha(pin));

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task CompleteDistributionsKeepRealHostFrameworkMetadataAndVersions(string rid)
    {
        using var fixture = new BuildCacheTestFixture(rid);
        fixture.PrepareArtifacts();
        fixture.PrepareFeedHome();
        var runtime = await Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        var aspNet = await Prepare(fixture, "aspnetcore", BuildCacheTestFixture.AspNetCoreSha);
        var home = fixture.Client.CreateDotnetHome(fixture.FeedHome, runtime.Version, aspNet.Version, runtime, aspNet);
        Assert.StartsWith("BCS muxer", File.ReadAllText(Path.Combine(home, rid.StartsWith("win-") ? "dotnet.exe" : "dotnet")));
        Assert.Empty(Directory.GetFiles(home, "feed-only.dll", SearchOption.AllDirectories));
        var original = Path.Combine(runtime.RoleDirectory("runtime-distribution"), "shared", "Microsoft.NETCore.App", runtime.Version);
        var actual = Path.Combine(home, "shared", "Microsoft.NETCore.App", runtime.Version);
        foreach (var name in new[] { "Microsoft.NETCore.App.deps.json", "Microsoft.NETCore.App.runtimeconfig.json", ".version" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(original, name)), File.ReadAllBytes(Path.Combine(actual, name)));
        }
        Assert.Equal("net11.0", runtime.Tfm);
        Assert.Equal("12.0.0-ci", runtime.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherFrameworkCanIndependentlyUseFeed(bool bcsRuntime)
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        fixture.PrepareFeedHome();
        var prepared = await Prepare(fixture, bcsRuntime ? "runtime" : "aspnetcore", bcsRuntime ? BuildCacheTestFixture.RuntimeSha : BuildCacheTestFixture.AspNetCoreSha);
        var home = fixture.Client.CreateDotnetHome(fixture.FeedHome, BuildCacheTestFixture.Version, BuildCacheTestFixture.Version,
            bcsRuntime ? prepared : null, bcsRuntime ? null : prepared);
        Assert.Single(Directory.GetFiles(home, "feed-only.dll", SearchOption.AllDirectories));
        Assert.All(fixture.Requests, request => Assert.Contains(bcsRuntime ? "/runtime/" : "/aspnetcore/", request));
    }

    [Theory]
    [InlineData("runtime", BuildCacheTestFixture.RuntimeSha)]
    [InlineData("aspnetcore", BuildCacheTestFixture.AspNetCoreSha)]
    public async Task FrameworksCannotBeRenamedToFeedVersions(string repo, string sha)
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var build = await Prepare(fixture, repo, sha);
        Assert.Throws<BuildCacheClient.BuildCacheIncompleteException>(() => fixture.Client.CreateDotnetHome(
            fixture.FeedHome, "11.0.0", "11.0.0", repo == "runtime" ? build : null, repo == "aspnetcore" ? build : null));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("wrong-rid")]
    public async Task RejectsMissingOrAmbiguousOriginalArchives(string fault)
    {
        using var fixture = new BuildCacheTestFixture();
        var archives = fixture.PrepareBuild("runtime", BuildCacheTestFixture.RuntimeSha);
        var reference = archives.Keys.Single(k => k.Contains(".Ref."));
        switch (fault)
        {
            case "missing": archives.Remove(reference); break;
            case "duplicate": archives[reference.Replace(".12.0.0-ci.", ".13.0.0-ci.")] = archives[reference]; break;
            case "nested": archives["folder/" + reference] = archives[reference]; archives.Remove(reference); break;
            case "wrong-rid":
                var runtime = archives.Keys.Single(k => k.Contains(".Runtime."));
                archives[runtime.Replace(fixture.Rid, "wrong-rid")] = archives[runtime]; archives.Remove(runtime);
                break;
        }
        fixture.PublishBundle("runtime", BuildCacheTestFixture.RuntimeSha, archives);
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() => Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha));
    }

    [Theory]
    [InlineData("12.0.0-ci", "11.0.0-rc.1.123", "LatestPatch", false)]
    [InlineData("11.0.0-rc.1.124", "11.0.0-rc.1.123", "LatestPatch", true)]
    [InlineData("11.0.1", "11.0.0", "LatestPatch", true)]
    [InlineData("11.1.0", "11.0.0", "LatestPatch", false)]
    [InlineData("12.0.0", "11.0.0", "Minor", false)]
    [InlineData("12.0.0", "11.0.0", "Major", true)]
    [InlineData("11.0.0-ci", "11.0.0", "Major", false)]
    [InlineData("11.0.1", "11.0.0", "Disable", false)]
    public void HonorsFrameworkRequirementPolicy(string selected, string required, string roll, bool compatible) =>
        Assert.Equal(compatible, BuildCacheClient.SatisfiesRequirement(new() { Name = "Microsoft.NETCore.App", Version = required, RollForward = roll }, selected));

    [Fact]
    public void ApplyPatchesFalseDoesNotAdvancePatchByDefault() =>
        Assert.False(BuildCacheClient.SatisfiesRequirement(new() { Name = "Microsoft.NETCore.App", Version = "11.0.0", ApplyPatches = false }, "11.0.1"));

    [Fact]
    public async Task ProducerBundleRoundTrip()
    {
        var path = Environment.GetEnvironmentVariable("CRANK_BCS_PRODUCER_BUNDLE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        using var fixture = new BuildCacheTestFixture(Environment.GetEnvironmentVariable("CRANK_BCS_PRODUCER_RID"));
        var repo = Path.GetFileName(path).StartsWith("RuntimeDistribution_") ? "runtime" : "aspnetcore";
        var sha = fixture.ImportBundle(path, repo);
        var prepared = await Prepare(fixture, repo, sha);
        Assert.True(prepared.Packages.Count >= 2);
        Assert.Equal("12.0.0-ci", prepared.Version);
        Assert.Equal("net11.0", prepared.Tfm);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task EqualFrameworkVersionsDoNotOverrideDeclaredRequirements()
    {
        using var fixture = new BuildCacheTestFixture("win-x64");
        fixture.PrepareBuild("runtime", BuildCacheTestFixture.RuntimeSha);
        var archives = fixture.PrepareBuild("aspnetcore", BuildCacheTestFixture.AspNetCoreSha);
        BuildCacheTestFixture.RewriteZip(archives, archives.Keys.Single(k => k.Contains(".Runtime.")), files =>
        {
            var config = files.Keys.Single(k => k.EndsWith(".runtimeconfig.json"));
            files[config] = JsonSerializer.Serialize(new { runtimeOptions = new { tfm = BuildCacheTestFixture.Tfm,
                framework = new { name = "Microsoft.NETCore.App", version = "11.0.0-rc.1.123", rollForward = "LatestPatch", applyPatches = false } } });
        });
        fixture.PublishBundle("aspnetcore", BuildCacheTestFixture.AspNetCoreSha, archives);
        var runtime = await Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        var aspNet = await Prepare(fixture, "aspnetcore", BuildCacheTestFixture.AspNetCoreSha);
        var error = Assert.Throws<BuildCacheClient.BuildCacheIncompleteException>(() => fixture.Client.CreateDotnetHome(
            fixture.FeedHome, runtime.Version, aspNet.Version, runtime, aspNet));
        Assert.Contains("requires Microsoft.NETCore.App 11.0.0-rc.1.123", error.Message);
        Assert.Contains("never lowered", error.Message);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.CacheRoot, "jobs"), "home-*"));
    }

    internal static async Task<BuildCacheClient.PreparedBuild> Prepare(BuildCacheTestFixture fixture, string repo, string sha) =>
        await fixture.Client.PrepareAsync(await fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, repo, sha, fixture.Rid));
}
