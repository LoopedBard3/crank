// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.Agent;
using Microsoft.Crank.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Crank.UnitTests
{
    [Collection("Agent startup")]
    public class BuildCacheClientTests
    {
        private const string Sha = "1234567890abcdef1234567890abcdef12345678";

        [Theory]
        [InlineData("", "ci", "ci", true)]
        [InlineData("", "current", "current", false)]
        [InlineData("latest", "ci", "latest", false)]
        [InlineData("current", "ci", "current", false)]
        [InlineData("edge", "ci", "edge", false)]
        [InlineData("10.0.12", "ci", "10.0.12", false)]
        [InlineData("ci", "current", "ci", true)]
        [InlineData(Sha, "current", Sha, true)]
        public void SelectorsAreIndependent(string version, string channel, string expected, bool ci)
        {
            var selector = BuildCacheClient.GetSelector(version, channel);
            Assert.Equal(expected, selector);
            Assert.Equal(ci, BuildCacheClient.IsCiSelector(selector));
            BuildCacheClient.ValidateSelector(selector);
        }

        [Theory]
        [InlineData("12345678")]
        [InlineData("abcdef123")]
        [InlineData("../runtime")]
        [InlineData("ci;exit")]
        public void InvalidSelectorsAreRejected(string selector) =>
            Assert.Throws<ArgumentException>(() => BuildCacheClient.ValidateSelector(selector));

        [Theory]
        [InlineData("runtime", "coreclr_x64_windows", "Runtime", false)]
        [InlineData("aspnetcore", "aspnetcore_x64_windows", "aspnetcore/Runtime", true)]
        public async Task PinUsesCommitFeedAndActualVersion(string repo, string config, string product, bool oneLine)
        {
            var version = "12.0.0-dev";
            var feed = $"https://example.test/builds/{repo}/buildArtifacts/{Sha}/{config}/install";
            using var handler = new Handler(new Dictionary<string, string>
            {
                [$"{feed}/{product}/main/latest.version"] = oneLine ? version : $"{Sha}\r\n{version}\r\n"
            });
            using var client = new HttpClient(handler);
            var build = await BuildCacheClient.ResolveAsync(client, "https://example.test/", repo, Sha.ToUpperInvariant(), "win-x64", default);
            Assert.Equal(Sha, build.CommitSha);
            Assert.Equal(version, build.Version);
            Assert.Equal(feed, build.AzureFeed);
            Assert.Single(handler.Requests);
        }

        [Theory]
        [InlineData("CommitSha", "coreclr_x64_linux")]
        [InlineData("commit_sha", "coreclr_x64_linux")]
        [InlineData("CommitSha", "all")]
        public async Task LatestReadsExistingIndexThenVersionFile(string property, string entry)
        {
            using var handler = new Handler(new Dictionary<string, string>
            {
                ["https://example.test/builds/runtime/latest/main/latestBuilds.json"] = new JObject { [entry] = new JObject { [property] = Sha } }.ToString(),
                [$"https://example.test/builds/runtime/buildArtifacts/{Sha}/coreclr_x64_linux/install/Runtime/main/latest.version"] = "11.0.0-preview.1.123"
            });
            using var client = new HttpClient(handler);
            var build = await BuildCacheClient.ResolveAsync(client, "https://example.test", "runtime", "ci", "linux-x64", default);
            Assert.Equal(Sha, build.CommitSha);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Theory]
        [InlineData("runtime", "coreclr_x64_windows", "Runtime")]
        [InlineData("aspnetcore", "aspnetcore_x64_windows", "aspnetcore/Runtime")]
        public async Task FloatingCiResolvesEachRunAndDetectsSameVersionCommitAdvance(string repo, string config, string product)
        {
            var latest = $"https://example.test/builds/{repo}/latest/main/latestBuilds.json";
            var responses = new Dictionary<string, string>();
            using var handler = new Handler(responses);
            using var client = new HttpClient(handler);
            var next = new string('b', 40);
            foreach (var commit in new[] { Sha, next })
                responses[$"https://example.test/builds/{repo}/buildArtifacts/{commit}/{config}/install/{product}/main/latest.version"] = $"{commit}\n12.0.0-dev";
            responses[latest] = new JObject { [config] = new JObject { ["CommitSha"] = Sha } }.ToString();
            var first = await BuildCacheClient.ResolveAsync(client, "https://example.test", repo, "ci", "win-x64", default);
            var unchanged = await BuildCacheClient.ResolveAsync(client, "https://example.test", repo, "ci", "win-x64", default);
            Assert.Equal(first, unchanged);
            responses[latest] = new JObject { [config] = new JObject { ["CommitSha"] = next } }.ToString();
            var advanced = await BuildCacheClient.ResolveAsync(client, "https://example.test", repo, "ci", "win-x64", default);
            Assert.Equal(first.Version, advanced.Version);
            Assert.NotEqual(first, advanced);
            Assert.Equal(6, handler.Requests.Count);
            Assert.Equal(latest, handler.Requests[0]);
            Assert.Equal(latest, handler.Requests[2]);
            Assert.Equal(latest, handler.Requests[4]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("12.0.0-dev\nanything")]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n12.0.0-dev")]
        [InlineData("../../runtime")]
        [InlineData("12.0.0-dev\n12.0.0-dev\n12.0.0-dev")]
        public async Task InvalidMarkerFailsClosed(string marker)
        {
            using var handler = new Handler(new Dictionary<string, string>
            {
                [$"https://example.test/builds/runtime/buildArtifacts/{Sha}/coreclr_x64_windows/install/Runtime/main/latest.version"] = marker
            });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BuildCacheClient.ResolveAsync(client, "https://example.test", "runtime", Sha, "win-x64", default));
        }

        [Fact]
        public async Task RawArtifactOnlyBuildDoesNotFallBack()
        {
            using var handler = new Handler(new Dictionary<string, string>());
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                BuildCacheClient.ResolveAsync(client, "https://example.test", "runtime", Sha, "win-x64", default));
            Assert.Single(handler.Requests);
            Assert.EndsWith("/install/Runtime/main/latest.version", handler.Requests[0]);
        }

        [Fact]
        public void NumericCommitDoesNotChooseTargetFramework()
        {
            var method = typeof(Startup).GetMethod("IsVersionPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.False((bool)method.Invoke(null, new object[] { Sha }));
            Assert.True((bool)method.Invoke(null, new object[] { "10.0.12" }));
        }

        [Fact]
        public void InstalledCommitAndCompleteFrameworkAreRequired()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                var build = new BuildCacheClient.ResolvedBuild(Sha, "12.0.0-dev", "https://example.test");
                var folder = Path.Combine(root, "shared", "Microsoft.NETCore.App", build.Version);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, ".version"), Sha);
                Assert.Throws<InvalidOperationException>(() => BuildCacheClient.ValidateInstallation(root, "Microsoft.NETCore.App", build));
                File.WriteAllText(Path.Combine(folder, "Microsoft.NETCore.App.deps.json"), "{}");
                File.WriteAllText(Path.Combine(folder, "Microsoft.NETCore.App.runtimeconfig.json"), "{}");
                BuildCacheClient.ValidateInstallation(root, "Microsoft.NETCore.App", build);
                File.WriteAllText(Path.Combine(folder, ".version"), new string('a', 40));
                Assert.Throws<InvalidOperationException>(() => BuildCacheClient.ValidateInstallation(root, "Microsoft.NETCore.App", build));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeConfigPatchesExistingReferencesWithoutDuplicates(bool multiple)
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                var path = Path.Combine(root, "test.runtimeconfig.json");
                var reference = new JObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.0" };
                var original = new JObject { ["tfm"] = "net10.0" };
                original[multiple ? "frameworks" : "framework"] = multiple
                    ? new JArray(reference, new JObject { ["name"] = "Microsoft.AspNetCore.App", ["version"] = "10.0.0" })
                    : reference;
                File.WriteAllText(path, new JObject { ["runtimeOptions"] = original }.ToString());
                Startup.PatchRuntimeConfig(new Job(), root, "12.0.0-dev", "12.0.0-dev", ci: true);
                Startup.PatchRuntimeConfig(new Job(), root, "12.0.0-dev", "12.0.0-dev", ci: true);
                var options = JObject.Parse(File.ReadAllText(path))["runtimeOptions"];
                Assert.Equal("net10.0", (string)options["tfm"]);
                Assert.Equal("Disable", (string)options["rollForward"]);
                Assert.Null(options["framework"]);
                var frameworks = (JArray)options["frameworks"];
                Assert.Equal(multiple ? 2 : 1, frameworks.Count);
                Assert.All(frameworks, f => Assert.Equal("12.0.0-dev", (string)f["version"]));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InstallerUsesExactVersionFeedArchitectureAndQuotedPrivateHome(bool windows)
        {
            var command = Startup.GetFrameworkInstallArguments(windows, "12.0.0-dev", "dotnet", "home with spaces", "https://example.test/install", "arm64");
            Assert.Contains("\"12.0.0-dev\"", command);
            Assert.Contains("\"home with spaces\"", command);
            Assert.Contains("\"https://example.test/install\"", command);
            Assert.Contains("arm64", command);
            Assert.DoesNotContain("latest", command);
        }

        [Fact]
        public void AspNetPromotionCannotOverwriteSameVersionRuntimeOrHost()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                var home = Path.Combine(root, "home");
                var staging = Path.Combine(root, "aspnet");
                foreach (var directory in new[] { home, staging })
                {
                    Directory.CreateDirectory(Path.Combine(directory, "shared", "Microsoft.NETCore.App", "12.0.0-dev"));
                    File.WriteAllText(Path.Combine(directory, "dotnet.exe"), directory);
                    File.WriteAllText(Path.Combine(directory, "shared", "Microsoft.NETCore.App", "12.0.0-dev", "coreclr.dll"), directory);
                }
                var aspnet = Path.Combine(staging, "shared", "Microsoft.AspNetCore.App", "12.0.0-dev");
                Directory.CreateDirectory(aspnet);
                File.WriteAllText(Path.Combine(aspnet, "Microsoft.AspNetCore.App.deps.json"), "original-metadata");
                BuildCacheClient.PromoteAspNetCoreFramework(home, staging);
                Assert.Equal(home, File.ReadAllText(Path.Combine(home, "dotnet.exe")));
                Assert.Equal(home, File.ReadAllText(Path.Combine(home, "shared", "Microsoft.NETCore.App", "12.0.0-dev", "coreclr.dll")));
                Assert.Equal("original-metadata", File.ReadAllText(Path.Combine(home, "shared", "Microsoft.AspNetCore.App", "12.0.0-dev", "Microsoft.AspNetCore.App.deps.json")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task NonCiBuildReuseKeepsExistingSkipBehavior()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            var rootField = typeof(Startup).GetField("_rootTempDir", BindingFlags.Static | BindingFlags.NonPublic);
            var originalRoot = rootField.GetValue(null);
            try
            {
                rootField.SetValue(null, root);
                var job = new Job { RuntimeVersion = "latest", AspNetCoreVersion = "latest", Project = "test.csproj", NoBuild = true, BuildKey = "reuse-test" };
                var options = Path.Combine(root, "_options");
                Directory.CreateDirectory(options);
                File.WriteAllText(Path.Combine(options, "reuse-test.json"), JsonConvert.SerializeObject(job.GetBuildKeyData()));
                File.WriteAllText(Path.Combine(root, CiBuildRecord.FileName), "old CI completion for an explicitly reused key");
                var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", BindingFlags.Static | BindingFlags.NonPublic);
                var result = await (Task<string>)clone.Invoke(null, new object[] { root, job, root, new JobContext(), CancellationToken.None });
                Assert.Equal(root, result);
                Assert.True(string.IsNullOrEmpty(job.Error));
                Assert.False(File.Exists(Path.Combine(root, CiBuildRecord.FileName)));
            }
            finally
            {
                rootField.SetValue(null, originalRoot);
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task MissingSelfContainedPackHasNoFeedFallback()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                using var handler = new Handler(new Dictionary<string, string>());
                using var client = new HttpClient(handler);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildCacheClient.PrepareRuntimePacksAsync(
                    client, "unused-dotnet", root, "10.0.401", "net10.0", "win-x64",
                    new BuildCacheClient.ResolvedBuild(Sha, "12.0.0-dev", "https://example.test/install"), null, default));
                Assert.Contains("self-contained runtime pack is unavailable", error.Message);
                Assert.Single(handler.Requests);
                Assert.EndsWith("/Runtime/12.0.0-dev/Microsoft.NETCore.App.Runtime.win-x64.12.0.0-dev.nupkg", handler.Requests[0]);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void SelfContainedMetadataIsNotOverlaidOrRewritten()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                var path = Path.Combine(root, "app.runtimeconfig.json");
                const string content = """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}""";
                File.WriteAllText(path, content);
                Startup.PatchRuntimeConfig(new Job { SelfContained = true }, root, "10.0.12", "10.0.12", ci: true);
                Assert.Equal(content, File.ReadAllText(path));
                Assert.Throws<InvalidOperationException>(() => Startup.PatchRuntimeConfig(new Job(), root, "10.0.12", "10.0.12", ci: true));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public async Task SameVersionRuntimePacksFromDifferentCommitsUseSeparateCaches()
        {
            var root = Directory.CreateTempSubdirectory("crank-ci-test-").FullName;
            try
            {
                var sdk = await ProcessUtil.RunAsync("dotnet", "--version", captureOutput: true, throwOnError: true);
                using var handler = new Handler(new Dictionary<string, string>());
                using var client = new HttpClient(handler);
                foreach (var marker in new[] { 'a', 'b' })
                {
                    var commit = new string(marker, 40);
                    var feed = $"https://example.test/{commit}/install";
                    using var archive = new MemoryStream();
                    using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        using (var writer = new StreamWriter(zip.CreateEntry("runtime.nuspec").Open()))
                            writer.Write("<package><metadata><id>Microsoft.NETCore.App.Runtime.win-x64</id><version>12.0.0-dev</version><authors>test</authors><description>Cache isolation fixture</description></metadata></package>");
                        using (var writer = new StreamWriter(zip.CreateEntry("marker.txt").Open()))
                            writer.Write(commit);
                    }
                    handler.Packages[$"{feed}/Runtime/12.0.0-dev/Microsoft.NETCore.App.Runtime.win-x64.12.0.0-dev.nupkg"] = archive.ToArray();
                    var packages = await BuildCacheClient.PrepareRuntimePacksAsync(client, "dotnet", Path.Combine(root, commit),
                        sdk.StandardOutput.Trim(), "net8.0", "win-x64", new BuildCacheClient.ResolvedBuild(commit, "12.0.0-dev", feed), null, default);
                    Assert.Equal(Path.Combine(root, commit, "nuget"), packages);
                    Assert.Equal(commit, File.ReadAllText(Path.Combine(packages, "microsoft.netcore.app.runtime.win-x64", "12.0.0-dev", "marker.txt")));
                }
                Assert.Equal(2, handler.Requests.Count);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void VersionMeasurementPreservesRegressionBotCommitSchema()
        {
            var measurement = BuildCacheClient.FormatVersion("12.0.0-dev", Sha);
            var segments = measurement.Split('+', 2, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("12.0.0-dev", segments[0]);
            Assert.Equal(Sha, segments[1]);
            Assert.True(BuildCacheClient.IsCommitSha(segments[1]));
        }

        [Theory]
        [InlineData("jit")]
        [InlineData("llvm-jit")]
        [InlineData("llvm-aot")]
        public async Task CiRejectsPostPublishMonoReplacementBeforeRetrievingSources(string mode)
        {
            var job = new Job { RuntimeVersion = "ci", AspNetCoreVersion = "latest", SelfContained = true, UseMonoRuntime = mode };
            var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", BindingFlags.Static | BindingFlags.NonPublic);
            var result = await (Task<string>)clone.Invoke(null, new object[] { "unused", job, "unused", new JobContext(), CancellationToken.None });
            Assert.Null(result);
            Assert.Contains("useMonoRuntime would replace the selected bits", job.Error);
        }

        [Theory]
        [InlineData("runtime")]
        [InlineData("aspnetcore")]
        public void UnsupportedMuslInstallerConfigurationIsRejected(string repo) =>
            Assert.Throws<InvalidOperationException>(() => BuildCacheClient.GetConfiguration(repo, "linux-musl-x64"));

        [Theory]
        [InlineData(JobState.Starting, false, true)]
        [InlineData(JobState.Starting, true, false)]
        [InlineData(JobState.Failed, false, false)]
        [InlineData(JobState.Deleting, false, false)]
        [InlineData(JobState.Deleted, false, false)]
        [InlineData(JobState.Stopping, false, false)]
        [InlineData(JobState.Stopped, false, false)]
        public void LateBuildCannotStartCanceledOrStoppedJob(JobState state, bool canceled, bool expected) =>
            Assert.Equal(expected, Startup.CanStartJob(new Job { State = state }, new CancellationToken(canceled)));

        [Fact]
        public void CleanupRetainsHomeUntilBuildAndStopAreConfirmed()
        {
            var job = new Job();
            var context = new JobContext();
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: false));
            var build = new TaskCompletionSource();
            context.BuildAndRunTask = build.Task;
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            build.SetResult();
            Assert.True(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            context.BuildAndRunTask = Task.FromCanceled(new CancellationToken(true));
            Assert.True(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            context.BuildAndRunTask = Task.FromException(new InvalidOperationException("completed build failure"));
            Assert.True(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
        }

        [Fact]
        public void CleanupRetainsHomeForLiveParentChildOrUnverifiableProcess()
        {
            var job = new Job();
            var context = new JobContext { BuildAndRunTask = Task.CompletedTask };
            using var current = Process.GetCurrentProcess();
            context.Process = current;
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            context.Process = null;
            job.ProcessId = current.Id;
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            job.ProcessId = 0;
            job.ChildProcessId = current.Id;
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
            job.ChildProcessId = 0;
            using var unknown = new Process();
            context.Process = unknown;
            Assert.False(Startup.CanDeleteBuildCacheHome(context, job, stopped: true));
        }

        [Fact]
        public void CleanupAllowsConfirmedExitedOwner()
        {
            using var owner = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            owner.StandardOutput.ReadToEnd();
            owner.WaitForExit();
            var context = new JobContext { BuildAndRunTask = Task.CompletedTask, Process = owner };
            Assert.True(Startup.CanDeleteBuildCacheHome(context, new Job { ProcessId = owner.Id }, stopped: true));
        }

        private sealed class Handler(Dictionary<string, string> responses) : HttpMessageHandler
        {
            internal List<string> Requests { get; } = new();
            internal Dictionary<string, byte[]> Packages { get; } = new();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri.AbsoluteUri;
                Requests.Add(url);
                if (Packages.TryGetValue(url, out var package))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
                return Task.FromResult(responses.TryGetValue(url, out var content)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
