// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.Agent;
using Microsoft.Crank.Models;
using Xunit;

namespace Microsoft.Crank.UnitTests;

[CollectionDefinition("Startup build tests", DisableParallelization = true)]
public class StartupBuildTestCollection;

[Collection("Startup build tests")]
public class StartupBuildTests : IDisposable
{
    private readonly BuildCacheTestFixture _fixture = new();
    private readonly string _project;
    private static FieldInfo Field(string name) => typeof(Startup).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
    private readonly object _oldClient = Field("_buildCacheClient").GetValue(null);
    private readonly object _oldUrl = Field("_buildCacheBaseUrl").GetValue(null);
    private readonly object _oldEnabled = Field("_buildCacheEnabled").GetValue(null);

    public StartupBuildTests()
    {
        _project = Path.Combine(_fixture.Root, "app.csproj");
        File.WriteAllText(_project, "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        _fixture.PrepareArtifacts();
        _fixture.PrepareFeedHome();
        Field("_buildCacheClient").SetValue(null, _fixture.Client);
        Field("_buildCacheBaseUrl").SetValue(null, BuildCacheTestFixture.BaseUrl);
        Field("_buildCacheEnabled").SetValue(null, true);
    }

    public void Dispose()
    {
        Field("_buildCacheClient").SetValue(null, _oldClient);
        Field("_buildCacheBaseUrl").SetValue(null, _oldUrl);
        Field("_buildCacheEnabled").SetValue(null, _oldEnabled);
        _fixture.Dispose();
    }

    [Theory]
    [InlineData("ci", BuildCacheTestFixture.RuntimeSha, "", "net11.0")]
    [InlineData("current", BuildCacheTestFixture.RuntimeSha, "", "net11.0")]
    [InlineData("ci", BuildCacheTestFixture.AspNetCoreSha, "", "net11.0")]
    [InlineData("ci", "ci", "", "net11.0")]
    [InlineData("ci", "", "", "net11.0")]
    [InlineData("ci", "latest", "latest", "net11.0")]
    [InlineData("ci", "10.0", "current", "net10.0")]
    [InlineData("ci", "10.0.*", "edge", "net10.0")]
    [InlineData("current", "10.0", "current", "net10.0")]
    [InlineData("latest", "10.0.1", "10.0.1", "net8.0")]
    public void SelectorParsingPrecedesLegacyFrameworkInference(string channel, string selector, string version, string tfm)
    {
        var job = new Job { Channel = channel, RuntimeVersion = selector, AspNetCoreVersion = "latest" };
        var resolved = Startup.ResolveFrameworkAndVersions(job, _project);
        Assert.Equal(tfm, resolved.targetFramework);
        Assert.Equal(version, resolved.runtimeVersion);
        Assert.Equal("latest", resolved.aspNetCoreVersion);
        Assert.Equal(selector, job.RuntimeVersion);
    }

    [Theory]
    [InlineData(BuildCacheTestFixture.RuntimeSha)]
    [InlineData(BuildCacheTestFixture.AspNetCoreSha)]
    [InlineData("10.0")]
    public void ExplicitFrameworkWins(string runtime)
    {
        var job = new Job { Channel = "ci", RuntimeVersion = runtime, Framework = "net9.0" };
        Assert.Equal("net9.0", Startup.ResolveFrameworkAndVersions(job, _project).targetFramework);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameImmutableSelectionsPreserveReuseAndResults(bool selfContained)
    {
        var job = Job(selfContained);
        var metadata = await WriteMarker(job);
        var context = new JobContext { Job = job };
        Assert.True(await Reuse(job, context));
        Assert.Equal(BuildCacheTestFixture.Version, job.RuntimeVersion);
        Assert.Equal(BuildCacheTestFixture.RuntimeSha, job.RequestedRuntimeVersion);
        Assert.Equal(metadata.Dependencies.Count, job.Dependencies.Count);
        Assert.Contains(job.Measurements, m => m.Name == Measurements.BenchmarksNetCoreAppVersion && (string)m.Value == BuildCacheTestFixture.Version + "+" + BuildCacheTestFixture.RuntimeSha);
        Assert.Equal(selfContained, context.BuildCacheDotnetHome == null);
        Startup.CleanupBuildCacheResources(context, true);
        Assert.Null(context.BuildCacheDotnetHome);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("mode")]
    [InlineData("selector")]
    [InlineData("identity")]
    public async Task InvalidReuseFailsClosed(string fault)
    {
        var job = Job(false);
        var metadata = await WriteMarker(job);
        var marker = Path.Combine(_fixture.Root, ".bcs-build-meta.json");
        switch (fault)
        {
            case "disabled": Field("_buildCacheEnabled").SetValue(null, false); break;
            case "missing": File.Delete(marker); break;
            case "malformed": File.WriteAllText(marker, "{}"); break;
            case "mode": job.SelfContained = true; break;
            case "selector": job.RuntimeVersion = "latest"; break;
            case "identity": metadata.RuntimeIdentity = "wrong"; BuildCachePublish.WriteMetadata(marker, metadata); break;
        }
        Assert.False(await Reuse(job, new JobContext { Job = job }));
        Assert.Contains("Build Cache", job.Error);
    }

    [Fact]
    public async Task LatestDriftRequiresRepublishEvenForFrameworkDependentBuilds()
    {
        var job = Job(false);
        job.RuntimeVersion = "ci";
        await WriteMarker(job);
        _fixture.PrepareBuild("runtime", new string('a', 40));
        Assert.False(await Reuse(job, new JobContext { Job = job }));
        Assert.Contains("Rebuild without build reuse", job.Error);
    }

    [Fact]
    public async Task ExplicitFeedJobsDoNotQueryBcsEvenUnderCiChannel()
    {
        var job = Job(false);
        job.RuntimeVersion = "latest";
        job.AspNetCoreVersion = "11.0.0";
        Field("_buildCacheEnabled").SetValue(null, false);
        Assert.True(await Reuse(job, new JobContext { Job = job }));
        Assert.Empty(_fixture.Requests);
    }

    [Fact]
    public async Task ReuseCancellationPropagates()
    {
        var job = Job(false);
        await WriteMarker(job);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reuse(job, new JobContext { Job = job }, cts.Token));
        Assert.Null(job.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupHonorsNoCleanAndWaitsForBuild(bool noClean)
    {
        var directory = Path.Combine(_fixture.Root, "owned");
        Directory.CreateDirectory(directory);
        var completion = new TaskCompletionSource();
        var job = Job(false);
        job.NoClean = noClean;
        var context = new JobContext { Job = job, BuildCacheDotnetHome = directory, BuildTask = completion.Task };
        Startup.CleanupBuildCacheResources(context, true);
        Assert.True(Directory.Exists(directory));
        completion.SetResult();
        Startup.CleanupBuildCacheResources(context, true);
        Assert.Equal(noClean, Directory.Exists(directory));
    }

    [Fact]
    public async Task WaitForBuildCompletionCancelsAndAwaitsWorker()
    {
        var cts = new CancellationTokenSource();
        var context = new JobContext { Job = Job(false), BuildCancellationTokenSource = cts };
        context.BuildTask = Task.Delay(Timeout.Infinite, cts.Token);
        await Startup.WaitForBuildCompletionAsync(context);
        Assert.True(context.BuildTask.IsCompleted);
        Assert.Null(context.BuildCancellationTokenSource);
    }

    [Fact]
    public void RuntimeEnvironmentUsesPrivateHomeWithoutFeedFallback()
    {
        var environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = _fixture.FeedHome, ["DOTNET_MULTILEVEL_LOOKUP"] = "1" };
        Startup.SetBuildCacheRuntimeEnvironment(environment, _fixture.Root, new Dictionary<string, string>());
        Assert.Equal(_fixture.Root, environment["DOTNET_ROOT"]);
        Assert.Equal(_fixture.Root, environment["DOTNET_ROOT_X64"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Throws<InvalidOperationException>(() => Startup.SetBuildCacheRuntimeEnvironment(environment, _fixture.Root,
            new Dictionary<string, string> { ["DOTNET_ROOT"] = _fixture.FeedHome }));
    }

    private static Job Job(bool selfContained) => new()
    {
        Project = "app.csproj", Channel = "ci", RuntimeVersion = BuildCacheTestFixture.RuntimeSha,
        AspNetCoreVersion = BuildCacheTestFixture.AspNetCoreSha, SelfContained = selfContained
    };

    private async Task<BuildCachePublish.BuildMetadata> WriteMarker(Job job)
    {
        var runtime = await _fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, _fixture.Rid);
        var aspNet = await _fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "aspnetcore", BuildCacheTestFixture.AspNetCoreSha, _fixture.Rid);
        var metadata = new BuildCachePublish.BuildMetadata
        {
            RuntimeSelector = FrameworkSelector.Parse(job.RuntimeVersion, job.Channel).Value,
            AspNetCoreSelector = FrameworkSelector.Parse(job.AspNetCoreVersion, job.Channel).Value,
            RuntimeIdentity = runtime.Identity, AspNetCoreIdentity = aspNet.Identity,
            RuntimeVersion = BuildCacheTestFixture.Version, AspNetCoreVersion = BuildCacheTestFixture.Version,
            RuntimeCommitSha = BuildCacheTestFixture.RuntimeSha, AspNetCoreCommitSha = BuildCacheTestFixture.AspNetCoreSha,
            Framework = BuildCacheTestFixture.Tfm, Rid = _fixture.Rid, SelfContained = job.SelfContained,
            Dependencies =
            [
                new() { Id = "runtime", Names = ["Microsoft.NETCore.App"], Version = BuildCacheTestFixture.Version, CommitHash = BuildCacheTestFixture.RuntimeSha },
                new() { Id = "aspnet", Names = ["Microsoft.AspNetCore.App"], Version = BuildCacheTestFixture.Version, CommitHash = BuildCacheTestFixture.AspNetCoreSha }
            ]
        };
        BuildCachePublish.WriteMetadata(Path.Combine(_fixture.Root, ".bcs-build-meta.json"), metadata);
        return metadata;
    }

    private Task<bool> Reuse(Job job, JobContext context, CancellationToken token = default) =>
        (Task<bool>)typeof(Startup).GetMethod("RefreshBuildCacheForReuseAsync", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, [_fixture.Root, _fixture.Root, job, _fixture.FeedHome, context, token]);
}
