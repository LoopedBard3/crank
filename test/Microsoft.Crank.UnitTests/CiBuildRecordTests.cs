// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.Agent;
using Microsoft.Crank.Models;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Crank.UnitTests
{
    [Collection("Agent startup")]
    public class CiBuildRecordTests : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("crank-ci-reuse-").FullName;
        private const string Sha = "1234567890abcdef1234567890abcdef12345678";
        private string Output => Path.Combine(_root, "published");

        private static CiBuildRecord.Inputs Inputs(bool selfContained = false) => new(
            new(Sha, "12.0.0-dev", $"https://example.test/runtime/{Sha}/install"),
            new(Sha, "12.0.0-dev", $"https://example.test/aspnetcore/{Sha}/install"),
            "12.0.0-dev", "12.0.0-dev", "10.0.401", "net10.0", "win-x64", selfContained,
            "10.0.12", "10.0.12", "10.0.12", "App");

        private static Job BuiltJob()
        {
            var job = new Job { DesktopVersion = "10.0.12", PublishedSize = 1234, BuildTime = TimeSpan.FromSeconds(12) };
            foreach (var (name, value) in new[]
            {
                (Measurements.BenchmarksNetSdkVersion, "10.0.401"),
                (Measurements.BenchmarksNetCoreAppVersion, $"12.0.0-dev+{Sha}"),
                (Measurements.BenchmarksAspNetCoreVersion, $"12.0.0-dev+{Sha}")
            })
            {
                job.Metadata.Enqueue(new MeasurementMetadata { Name = name, Source = "Host Process", Aggregate = Operation.First });
                job.Measurements.Enqueue(new Measurement { Name = name, Value = value, Timestamp = DateTime.UtcNow.AddDays(-1) });
            }
            job.Measurements.Enqueue(new Measurement { Name = Measurements.BenchmarksBuildTime, Value = 12000 });
            job.Dependencies.Add(new Dependency { Id = "test", Names = ["Microsoft.NETCore.App"], CommitHash = Sha, Version = "12.0.0-dev", RepositoryUrl = "https://github.com/dotnet/runtime" });
            return job;
        }

        private void CreateOutput(bool scd = false)
        {
            Directory.CreateDirectory(Output);
            foreach (var file in new[] { "App.dll", "App.deps.json", "App.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(Output, file), "published fixture");
            if (scd)
                File.WriteAllText(Path.Combine(Output, "App.exe"), "apphost fixture");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExactResolvedSelectionRestoresResultsWithoutOldBuildDuration(bool scd)
        {
            CreateOutput(scd);
            CiBuildRecord.Write(_root, Output, Inputs(scd), BuiltJob());
            var record = CiBuildRecord.Read(_root, Output, Inputs(scd));
            Assert.NotNull(record);
            var job = new Job { NoClean = true, RequestedRuntimeVersion = "ci", RequestedAspNetCoreVersion = Sha };
            record.Restore(job);
            Assert.Equal("12.0.0-dev", job.RuntimeVersion);
            Assert.Equal(Sha, job.RuntimeCommitSha);
            Assert.Equal("10.0.401", job.SdkVersion);
            Assert.Equal("net10.0", job.BuildFramework);
            Assert.Equal("ci", job.RequestedRuntimeVersion);
            Assert.Equal(Sha, job.RequestedAspNetCoreVersion);
            Assert.Equal(Sha, Assert.Single(job.Dependencies).CommitHash);
            Assert.Equal("test", job.Dependencies[0].Id);
            Assert.Equal(1234, job.PublishedSize);
            Assert.Equal(TimeSpan.Zero, job.BuildTime);
            Assert.DoesNotContain(job.Measurements, m => m.Name == Measurements.BenchmarksBuildTime);
            Assert.Contains(job.Measurements, m => m.Name == Measurements.BenchmarksNetSdkVersion && (string)m.Value == "10.0.401");
            Assert.All(job.Measurements, m => Assert.True(m.Timestamp > DateTime.UtcNow.AddMinutes(-1)));
            Assert.Equal(3, job.Metadata.Count);
            Assert.Single(Directory.GetFiles(_root, ".ci-build*"));
        }

        [Theory]
        [InlineData("runtime-commit")]
        [InlineData("aspnet-commit")]
        [InlineData("runtime-version")]
        [InlineData("aspnet-version")]
        [InlineData("runtime-source")]
        [InlineData("aspnet-source")]
        [InlineData("sdk")]
        [InlineData("compile-runtime")]
        [InlineData("compile-aspnet")]
        [InlineData("desktop")]
        [InlineData("tfm")]
        [InlineData("rid")]
        [InlineData("scd")]
        [InlineData("assembly")]
        public void ResolvedInputsInvalidateSameRequestedKey(string change)
        {
            CreateOutput();
            var original = Inputs();
            CiBuildRecord.Write(_root, Output, original, BuiltJob());
            var changed = change switch
            {
                "runtime-commit" => original with { RuntimeBuild = original.RuntimeBuild with { CommitSha = new string('b', 40) } },
                "aspnet-commit" => original with { AspNetCoreBuild = original.AspNetCoreBuild with { CommitSha = new string('b', 40) } },
                "runtime-version" => original with { RuntimeVersion = "12.0.1" },
                "aspnet-version" => original with { AspNetCoreVersion = "12.0.1" },
                "runtime-source" => original with { RuntimeBuild = original.RuntimeBuild with { AzureFeed = "https://another.test/install" } },
                "aspnet-source" => original with { AspNetCoreBuild = original.AspNetCoreBuild with { AzureFeed = "https://another.test/install" } },
                "sdk" => original with { SdkVersion = "10.0.402" },
                "compile-runtime" => original with { BuildRuntimeVersion = "10.0.13" },
                "compile-aspnet" => original with { BuildAspNetCoreVersion = "10.0.13" },
                "desktop" => original with { DesktopVersion = "10.0.13" },
                "tfm" => original with { Framework = "net11.0" },
                "rid" => original with { Rid = "linux-arm64" },
                "scd" => original with { SelfContained = true },
                _ => original with { AssemblyName = "Renamed" }
            };
            Assert.Null(CiBuildRecord.Read(_root, Output, changed));
            Assert.NotNull(CiBuildRecord.Read(_root, Output, original));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"SchemaVersion\":99}")]
        [InlineData("{\"Results\":\"wrong-shape\"}")]
        public void AbsentMalformedOrUnsupportedRecordIsAMiss(string content)
        {
            CreateOutput();
            if (content != null)
                File.WriteAllText(Path.Combine(_root, CiBuildRecord.FileName), content);
            Assert.Null(CiBuildRecord.Read(_root, Output, Inputs()));
        }

        [Theory]
        [InlineData("App.dll")]
        [InlineData("App.deps.json")]
        [InlineData("App.runtimeconfig.json")]
        [InlineData("App.exe")]
        public void IncompletePublishedOutputCannotHitOrBecomeReady(string file)
        {
            CreateOutput(true);
            CiBuildRecord.Write(_root, Output, Inputs(true), BuiltJob());
            File.Delete(Path.Combine(Output, file));
            Assert.Null(CiBuildRecord.Read(_root, Output, Inputs(true)));
            CiBuildRecord.Invalidate(_root);
            CiBuildRecord.Write(_root, Output, Inputs(true), BuiltJob());
            Assert.False(File.Exists(Path.Combine(_root, CiBuildRecord.FileName)));
        }

        [Theory]
        [InlineData("Results")]
        [InlineData("Metadata")]
        [InlineData("Dependencies")]
        public void MalformedResultPayloadIsAMiss(string property)
        {
            CreateOutput();
            CiBuildRecord.Write(_root, Output, Inputs(), BuiltJob());
            var path = Path.Combine(_root, CiBuildRecord.FileName);
            var record = JObject.Parse(File.ReadAllText(path));
            record[property] = new JArray(JValue.CreateNull());
            File.WriteAllText(path, record.ToString());
            Assert.Null(CiBuildRecord.Read(_root, Output, Inputs()));
        }

        [Fact]
        public async Task SourceRequestMismatchInvalidatesBeforeSourceRetrievalCanFail()
        {
            var field = typeof(Startup).GetField("_rootTempDir", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = field.GetValue(null);
            try
            {
                field.SetValue(null, _root);
                Directory.CreateDirectory(Path.Combine(_root, "_options"));
                File.WriteAllText(Path.Combine(_root, "_options", "key.json"), "different request");
                File.WriteAllText(Path.Combine(_root, CiBuildRecord.FileName), "old completed record");
                var job = new Job { BuildKey = "key" };
                job.Sources.Add("broken-source", new Source { SourceCode = new Attachment { TempFilename = Path.Combine(_root, "missing.zip") } });
                var retrieve = typeof(Startup).GetMethod("RetrieveSourcesAsync", BindingFlags.Static | BindingFlags.NonPublic);
                await Assert.ThrowsAsync<FileNotFoundException>(async () =>
                    await (Task<bool>)retrieve.Invoke(null, new object[] { job, _root }));
                Assert.False(File.Exists(Path.Combine(_root, CiBuildRecord.FileName)));
            }
            finally { field.SetValue(null, previous); }
        }

        [Fact]
        public void SharedKeyDefersWithoutBlockingOwnerReleaseEvenWithNoClean()
        {
            var first = new JobContext { Job = new Job { NoClean = true }, BuildAndRunTask = Task.CompletedTask };
            var next = new JobContext { Job = new Job() };
            Assert.True(Startup.TryAcquireBuildPath(first, _root));
            Assert.False(Startup.TryAcquireBuildPath(next, _root + Path.DirectorySeparatorChar));
            try
            {
                Assert.True(Startup.TryAcquireBuildPath(first, _root));
                var attempt = Task.Run(() => Startup.TryAcquireBuildPath(next, Path.Combine(_root, ".")));
                Assert.True(attempt.Wait(TimeSpan.FromSeconds(2)));
                Assert.False(attempt.Result);
                using var current = Process.GetCurrentProcess();
                first.Job.ChildProcessId = current.Id;
                Assert.False(Startup.ReleaseBuildPath(first, first.Job, stopped: true));
                first.Job.ChildProcessId = 0;
                first.BuildAndRunTask = new TaskCompletionSource().Task;
                Assert.False(Startup.ReleaseBuildPath(first, first.Job, stopped: true));
                first.BuildAndRunTask = Task.CompletedTask;
                Assert.False(Startup.ReleaseBuildPath(first, first.Job, stopped: false));
                Assert.True(Startup.ReleaseBuildPath(first, first.Job, stopped: true));
                Assert.True(Startup.TryAcquireBuildPath(next, _root));
            }
            finally
            {
                first.Job.ChildProcessId = 0;
                first.BuildAndRunTask = Task.CompletedTask;
                Startup.ReleaseBuildPath(first, first.Job, true);
                Startup.ReleaseBuildPath(next, next.Job, true);
            }
        }

        [Fact]
        public void FailedContainerCleanupCannotReleaseSharedBuildPath()
        {
            var job = new Job { DockerFile = "Dockerfile", State = JobState.Failed };
            var context = new JobContext { Job = job, BuildAndRunTask = Task.CompletedTask };
            Assert.True(Startup.TryAcquireBuildPath(context, _root));
            try { Assert.False(Startup.ReleaseBuildPath(context, job, stopped: true)); }
            finally
            {
                job.State = JobState.Stopped;
                Assert.True(Startup.ReleaseBuildPath(context, job, stopped: true));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CompletedFailedBuildReleasesKeyForNextJob(bool canceled)
        {
            var first = new JobContext
            {
                Job = new Job { State = JobState.Failed },
                BuildAndRunTask = canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new HttpRequestException("resolver failed"))
            };
            var next = new JobContext { Job = new Job() };
            Assert.True(Startup.TryAcquireBuildPath(first, _root));
            try
            {
                Assert.False(Startup.ReleaseBuildPath(first, first.Job, stopped: false));
                using var process = Process.GetCurrentProcess();
                first.Job.ChildProcessId = process.Id;
                Assert.False(Startup.ReleaseBuildPath(first, first.Job, stopped: true));
                Assert.False(Startup.TryAcquireBuildPath(next, _root));
                first.Job.ChildProcessId = 0;
                Assert.True(Startup.ReleaseBuildPath(first, first.Job, stopped: true));
                Assert.True(Startup.TryAcquireBuildPath(next, _root));
            }
            finally
            {
                first.Job.ChildProcessId = 0;
                Startup.ReleaseBuildPath(first, first.Job, true);
                Startup.ReleaseBuildPath(next, next.Job, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SelfContainedHitOnlyResolvesAndNeverInstallsOrBuilds(bool resolutionFails)
        {
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)portReservation.LocalEndpoint).Port}";
            portReservation.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add(url + "/");
            listener.Start();
            var requests = 0;
            var unavailable = resolutionFails;
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var request = await listener.GetContextAsync();
                        Interlocked.Increment(ref requests);
                        if (Volatile.Read(ref unavailable) || !request.Request.Url.AbsolutePath.EndsWith("/main/latest.version"))
                            request.Response.StatusCode = 404;
                        else
                        {
                            var content = Encoding.UTF8.GetBytes($"{Sha}\n12.0.0-dev\n");
                            request.Response.ContentLength64 = content.Length;
                            await request.Response.OutputStream.WriteAsync(content);
                        }
                        request.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            });

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var fields = new[] { "_rootTempDir", "_buildCacheBaseUrl", "_pwsh", "_fileContentCache" }
                .Select(name => typeof(Startup).GetField(name, flags)).ToDictionary(field => field, field => field.GetValue(null));
            var logger = typeof(Startup).GetProperty("Logger", flags);
            var previousLogger = logger.GetValue(null);
            var sdks = (System.Collections.Generic.HashSet<string>)typeof(Startup).GetField("_installedSdks", flags).GetValue(null);
            var addedSdk = sdks.Add("10.0.401");
            using var cache = new MemoryCache(new MemoryCacheOptions());
            using var testLogger = new Serilog.LoggerConfiguration().CreateLogger();
            JobContext owner = null;
            try
            {
                typeof(Startup).GetField("_rootTempDir", flags).SetValue(null, _root);
                typeof(Startup).GetField("_buildCacheBaseUrl", flags).SetValue(null, url);
                typeof(Startup).GetField("_pwsh", flags).SetValue(null, "installer-must-not-run");
                typeof(Startup).GetField("_fileContentCache", flags).SetValue(null, cache);
                logger.SetValue(null, testLogger);
                cache.Set(("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", (TimeSpan?)TimeSpan.FromDays(1)),
                    """{"releases":[{"runtime":{"version":"10.0.12"},"aspnetcore-runtime":{"version":"10.0.12"},"windowsdesktop":{"version":"10.0.12"},"sdk":{"version":"10.0.401"}}]}""");
                var rid = (string)typeof(Startup).GetMethod("GetPlatformMoniker", flags).Invoke(null, null);
                var selection = Inputs(true) with
                {
                    Rid = rid,
                    RuntimeBuild = new(Sha, "12.0.0-dev", $"{url}/builds/runtime/buildArtifacts/{Sha}/{BuildCacheClient.GetConfiguration("runtime", rid)}/install"),
                    AspNetCoreBuild = new(Sha, "12.0.0-dev", $"{url}/builds/aspnetcore/buildArtifacts/{Sha}/{BuildCacheClient.GetConfiguration("aspnetcore", rid)}/install")
                };
                CreateOutput(true);
                File.WriteAllText(Path.Combine(Output, "App"), "Unix apphost fixture");
                CiBuildRecord.Write(_root, Output, selection, BuiltJob());
                var marker = File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName));
                Job NewJob() => new Job { BuildKey = "reuse", Project = "App.csproj", Framework = "net10.0", SdkVersion = "10.0.401",
                    DesktopVersion = "10.0.12", RuntimeVersion = Sha, AspNetCoreVersion = Sha, SelfContained = true, NoBuild = true, NoClean = true };
                var job = NewJob();
                Directory.CreateDirectory(Path.Combine(_root, "_options"));
                File.WriteAllText(Path.Combine(_root, "_options", "reuse.json"), JsonConvert.SerializeObject(job.GetBuildKeyData()));
                var context = owner = new JobContext { Job = job };
                Assert.True(Startup.TryAcquireBuildPath(context, _root));
                var build = (Task<string>)typeof(Startup).GetMethod("CloneRestoreAndBuild", flags).Invoke(null,
                    new object[] { _root, job, Path.Combine(_root, "SDK-does-not-exist"), context, CancellationToken.None });
                context.BuildAndRunTask = build;
                if (resolutionFails)
                {
                    await Assert.ThrowsAsync<HttpRequestException>(async () => await build);
                    Assert.True(Startup.ReleaseBuildPath(context, job, stopped: true));
                    Volatile.Write(ref unavailable, false);
                    var retry = NewJob();
                    owner = new JobContext { Job = retry };
                    Assert.True(Startup.TryAcquireBuildPath(owner, _root));
                    var retryBuild = (Task<string>)typeof(Startup).GetMethod("CloneRestoreAndBuild", flags).Invoke(null,
                        new object[] { _root, retry, Path.Combine(_root, "SDK-does-not-exist"), owner, CancellationToken.None });
                    owner.BuildAndRunTask = retryBuild;
                    Assert.Equal(_root, await retryBuild);
                    Assert.Equal(Sha, retry.RuntimeCommitSha);
                    Assert.Equal(3, requests);
                    Assert.Null(owner.BuildCacheDotnetHome);
                    Assert.True(Startup.ReleaseBuildPath(owner, retry, stopped: true));
                }
                else
                {
                    Assert.Equal(_root, await build);
                    Assert.Equal(2, requests);
                    Assert.Null(context.BuildCacheDotnetHome);
                    Assert.Equal("", job.BuildLog.ToString());
                    Assert.Equal(Sha, job.RuntimeCommitSha);
                    Assert.Equal(Sha, Assert.Single(job.Dependencies).CommitHash);
                    Assert.Equal("10.0.401", job.SdkVersion);
                    Assert.Equal(TimeSpan.Zero, job.BuildTime);
                    Assert.False(Directory.Exists(Path.Combine(_root, "SDK-does-not-exist")));
                }
                Assert.Equal(marker, File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName)));
            }
            finally
            {
                if (owner != null)
                    Startup.ReleaseBuildPath(owner, owner.Job, true);
                foreach (var (field, value) in fields)
                    field.SetValue(null, value);
                logger.SetValue(null, previousLogger);
                if (addedSdk)
                    sdks.Remove("10.0.401");
                listener.Stop();
                await serving;
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
