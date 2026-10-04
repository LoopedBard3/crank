// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    // A matching cache hit is a frozen completion proof: it is restored as-is (never compared against
    // anything newly resolved) and never mutates the published project/build output. These tests exercise
    // both the small CiBuildRecord helper and the real CloneRestoreAndBuild hit/miss routing.
    [Collection("Agent startup")]
    public class CiBuildRecordTests : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("crank-ci-reuse-").FullName;
        private const string Sha = "1234567890abcdef1234567890abcdef12345678";
        private string Output => Path.Combine(_root, "published");

        private static CiBuildRecord.Inputs Inputs(bool selfContained = false, string sha = Sha) => new(
            new(sha, "12.0.0-dev", $"https://example.test/runtime/{sha}/install"),
            new(sha, "12.0.0-dev", $"https://example.test/aspnetcore/{sha}/install"),
            "12.0.0-dev", "12.0.0-dev", "10.0.401", "net10.0", "win-x64", selfContained,
            "10.0.12", "10.0.12", "10.0.12", "App");

        private static Job BuiltJob(string sha = Sha)
        {
            var job = new Job { DesktopVersion = "10.0.12", PublishedSize = 1234, BuildTime = TimeSpan.FromSeconds(12) };
            foreach (var (name, value) in new[]
            {
                (Measurements.BenchmarksNetSdkVersion, "10.0.401"),
                (Measurements.BenchmarksNetCoreAppVersion, $"12.0.0-dev+{sha}"),
                (Measurements.BenchmarksAspNetCoreVersion, $"12.0.0-dev+{sha}")
            })
            {
                job.Metadata.Enqueue(new MeasurementMetadata { Name = name, Source = "Host Process", Aggregate = Operation.First });
                job.Measurements.Enqueue(new Measurement { Name = name, Value = value, Timestamp = DateTime.UtcNow.AddDays(-1) });
            }
            job.Measurements.Enqueue(new Measurement { Name = Measurements.BenchmarksBuildTime, Value = 12000 });
            job.Dependencies.Add(new Dependency { Id = "test", Names = ["Microsoft.NETCore.App"], CommitHash = sha, Version = "12.0.0-dev", RepositoryUrl = "https://github.com/dotnet/runtime" });
            return job;
        }

        private void CreateOutput(bool scd = false, string assemblyName = "App")
        {
            Directory.CreateDirectory(Output);
            foreach (var file in new[] { $"{assemblyName}.dll", $"{assemblyName}.deps.json", $"{assemblyName}.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(Output, file), "published fixture");
            if (scd)
                File.WriteAllText(Path.Combine(Output, $"{assemblyName}.exe"), "apphost fixture");
        }

        private static CiBuildRecord Read(string root, string output, CiBuildRecord.Inputs current) =>
            CiBuildRecord.Read(root, output, current.AssemblyName, current.Rid, current.SelfContained);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExactResolvedSelectionRestoresResultsWithoutOldBuildDuration(bool scd)
        {
            CreateOutput(scd);
            CiBuildRecord.Write(_root, Output, Inputs(scd), BuiltJob());
            var record = Read(_root, Output, Inputs(scd));
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
        [InlineData("assembly")]
        [InlineData("rid")]
        [InlineData("scd")]
        public void BasicFieldMismatchInvalidatesTheHit(string change)
        {
            CreateOutput();
            CiBuildRecord.Write(_root, Output, Inputs(), BuiltJob());
            var current = Inputs() with
            {
                AssemblyName = change == "assembly" ? "Renamed" : "App",
                Rid = change == "rid" ? "linux-arm64" : "win-x64",
                SelfContained = change == "scd",
            };
            Assert.Null(Read(_root, Output, current));
            Assert.NotNull(Read(_root, Output, Inputs()));
        }

        [Theory]
        [InlineData("no-resolved-build")]
        [InlineData("non-sha-commit")]
        [InlineData("non-http-feed")]
        [InlineData("version-mismatch")]
        [InlineData("malformed-sdk")]
        public void InconsistentSnapshotIsAMiss_NeverTrustsTheRecordBlindly(string change)
        {
            // Internal snapshot-shape validation only — never compares against a user-supplied or newly
            // resolved value. A record that is internally inconsistent (not what CloneRestoreAndBuild
            // could ever have actually written) must be a normal cache miss up front, rather than passing
            // Read and only failing later during reinstall/validation.
            CreateOutput();
            var original = Inputs();
            var stored = change switch
            {
                "no-resolved-build" => original with { RuntimeBuild = null, AspNetCoreBuild = null },
                "non-sha-commit" => original with { RuntimeBuild = original.RuntimeBuild with { CommitSha = "not-a-sha" } },
                "non-http-feed" => original with { RuntimeBuild = original.RuntimeBuild with { AzureFeed = "file:///not-http" } },
                "version-mismatch" => original with { RuntimeBuild = original.RuntimeBuild with { Version = "99.0.0" } },
                _ => original with { SdkVersion = "not-a-version" }
            };
            CiBuildRecord.Write(_root, Output, stored, BuiltJob());
            Assert.Null(Read(_root, Output, original));
        }

        [Theory]
        [InlineData("Selection.RuntimeVersion")]
        [InlineData("Selection.AspNetCoreVersion")]
        [InlineData("Selection.SdkVersion")]
        [InlineData("Selection.RuntimeBuild.Version")]
        [InlineData("Selection.RuntimeBuild.CommitSha")]
        [InlineData("Selection.RuntimeBuild.AzureFeed")]
        public void NullOrOmittedVersionIdentityFieldIsAMissNotAnException(string jsonPath)
        {
            // A null (explicit JSON null, or simply omitted) version/SDK/commit/feed field anywhere in
            // the snapshot must be treated as a normal cache miss — never let a null escape IsConsistentSnapshot
            // (or any other check) as an unhandled exception out of Read, which only catches IO/Json errors.
            CreateOutput();
            CiBuildRecord.Write(_root, Output, Inputs(), BuiltJob());
            var path = Path.Combine(_root, CiBuildRecord.FileName);
            var record = JObject.Parse(File.ReadAllText(path));
            var segments = jsonPath.Split('.');
            var target = record;
            for (var i = 0; i < segments.Length - 1; i++)
                target = (JObject)target[segments[i]];
            target[segments[^1]] = JValue.CreateNull();
            File.WriteAllText(path, record.ToString());

            var exception = Record.Exception(() => Read(_root, Output, Inputs()));
            Assert.Null(exception);
            Assert.Null(Read(_root, Output, Inputs()));
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
        public void ResolvedValueChangesDoNotInvalidateTheHit_RecordedMetadataIsFrozenNotCurrentLatest(string change)
        {
            // A matching request/build-key guard is enough: the record is a completion proof, not a
            // current-resolved-input equality mechanism, so none of these would ever be re-resolved on a
            // hit in the first place. This demonstrates the frozen record is used as-is regardless.
            CreateOutput();
            var original = Inputs();
            var stored = change switch
            {
                "runtime-commit" => original with { RuntimeBuild = original.RuntimeBuild with { CommitSha = new string('b', 40) } },
                "aspnet-commit" => original with { AspNetCoreBuild = original.AspNetCoreBuild with { CommitSha = new string('b', 40) } },
                "runtime-version" => original with { RuntimeVersion = "12.0.1", RuntimeBuild = original.RuntimeBuild with { Version = "12.0.1" } },
                "aspnet-version" => original with { AspNetCoreVersion = "12.0.1", AspNetCoreBuild = original.AspNetCoreBuild with { Version = "12.0.1" } },
                "runtime-source" => original with { RuntimeBuild = original.RuntimeBuild with { AzureFeed = "https://another.test/install" } },
                "aspnet-source" => original with { AspNetCoreBuild = original.AspNetCoreBuild with { AzureFeed = "https://another.test/install" } },
                "sdk" => original with { SdkVersion = "10.0.402" },
                "compile-runtime" => original with { BuildRuntimeVersion = "10.0.13" },
                "compile-aspnet" => original with { BuildAspNetCoreVersion = "10.0.13" },
                "desktop" => original with { DesktopVersion = "10.0.13" },
                _ => original with { Framework = "net11.0" }
            };
            CiBuildRecord.Write(_root, Output, stored, BuiltJob());

            var record = Read(_root, Output, original);
            Assert.NotNull(record);
            var job = new Job();
            record.Restore(job);
            // The restored job reflects whatever was recorded, not a freshly resolved/"current latest" value.
            Assert.Equal(stored.RuntimeVersion, job.RuntimeVersion);
            Assert.Equal(stored.RuntimeBuild.CommitSha, job.RuntimeCommitSha);
            Assert.Equal(stored.SdkVersion, job.SdkVersion);
            Assert.Equal(stored.Framework, job.BuildFramework);
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
            Assert.Null(Read(_root, Output, Inputs()));
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
            Assert.Null(Read(_root, Output, Inputs(true)));
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
            Assert.Null(Read(_root, Output, Inputs()));
        }

        [Fact]
        public async Task FddHitCancellationPropagatesWithoutInstallingOrMutatingTheRecord()
        {
            // Cancellation must propagate like any other installer-alternative code path (never be
            // swallowed into job.Error/null), and a canceled reinstall must leave no installed private
            // home and no record mutation behind.
            var rootField = typeof(Startup).GetField("_rootTempDir", BindingFlags.Static | BindingFlags.NonPublic);
            var previousRoot = rootField.GetValue(null);
            try
            {
                rootField.SetValue(null, _root);
                CreateOutput();
                CiBuildRecord.Write(_root, Output, Inputs(), BuiltJob());
                var marker = File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName));
                Directory.CreateDirectory(Path.Combine(_root, "_options"));

                var job = new Job
                {
                    BuildKey = "cancel-key", Project = "App.csproj", RuntimeVersion = Sha, AspNetCoreVersion = Sha,
                    SdkVersion = "10.0.401", DesktopVersion = "10.0.12", SelfContained = false, NoBuild = true, NoClean = true
                };
                File.WriteAllText(Path.Combine(_root, "_options", "cancel-key.json"), JsonConvert.SerializeObject(job.GetBuildKeyData()));
                var context = new JobContext();
                var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", BindingFlags.Static | BindingFlags.NonPublic);

                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var build = (Task<string>)clone.Invoke(null, new object[] { _root, job, @"C:\unused-sdk", context, cts.Token });
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await build);

                Assert.Null(context.BuildCacheDotnetHome);
                Assert.Empty(Directory.GetDirectories(_root, "ci-dotnet-*"));
                Assert.Equal(marker, File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName)));
            }
            finally { rootField.SetValue(null, previousRoot); }
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
        public void NoSharedBuildOwnershipMechanismRemains()
        {
            // The whole-job-lifetime reservation (_buildOwners/TryAcquireBuildPath/ReleaseBuildPath/
            // OwnedBuildPath) caused a same-run two-service deadlock, a Docker Stop->Delete permanent
            // key poison, and a late-canceled-task permanent key poison. It has been removed entirely;
            // matching requests share cached output read-only exactly like the pre-existing non-CI
            // BuildKey/_options guard always has, with no new coordination primitive.
            Assert.Null(typeof(Startup).GetMethod("TryAcquireBuildPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.Null(typeof(Startup).GetMethod("ReleaseBuildPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            Assert.Null(typeof(Startup).GetField("_buildOwners", BindingFlags.Static | BindingFlags.NonPublic));
            Assert.Null(typeof(JobContext).GetProperty("OwnedBuildPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            // CanDeleteBuildCacheHome remains: it is still used independently for the job's own private
            // CI runtime-home cleanup in DeleteJobAsync, unrelated to any cross-job coordination.
            Assert.NotNull(typeof(Startup).GetMethod("CanDeleteBuildCacheHome", BindingFlags.Static | BindingFlags.NonPublic));
        }

        [Fact]
        public async Task SharedBuildKeyTwoJobsReuseImmediatelyWithoutDeferring()
        {
            // Two services intentionally sharing a BuildKey (identical requested options, different
            // Service/Arguments, which are not part of the key) must both be able to proceed immediately:
            // the second never defers waiting on the first, matching the pre-existing non-CI behavior.
            var field = typeof(Startup).GetField("_rootTempDir", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = field.GetValue(null);
            try
            {
                field.SetValue(null, _root);
                var retrieve = typeof(Startup).GetMethod("RetrieveSourcesAsync", BindingFlags.Static | BindingFlags.NonPublic);

                var first = new Job { BuildKey = "shared-key", Project = "App.csproj" };
                var second = new Job { BuildKey = "shared-key", Project = "App.csproj" };

                var firstReuse = await (Task<bool>)retrieve.Invoke(null, new object[] { first, _root });
                Assert.False(firstReuse); // first use: nothing cached yet

                var secondTask = (Task<bool>)retrieve.Invoke(null, new object[] { second, _root });
                Assert.True(secondTask.Wait(TimeSpan.FromSeconds(2)), "second job must not block waiting on the first");
                Assert.True(await secondTask); // options already matched: read-only reuse, no write race
            }
            finally { field.SetValue(null, previous); }
        }

        [Fact]
        public async Task ConcurrentMatchingReadersBothHitWithoutBlockingOrMutatingEachOther()
        {
            var rootField = typeof(Startup).GetField("_rootTempDir", BindingFlags.Static | BindingFlags.NonPublic);
            var previousRoot = rootField.GetValue(null);
            try
            {
                rootField.SetValue(null, _root);
                CreateOutput(true);
                CiBuildRecord.Write(_root, Output, Inputs(true), BuiltJob());
                var marker = File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName));
                Directory.CreateDirectory(Path.Combine(_root, "_options"));

                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", flags);

                Job NewReader(string key)
                {
                    var job = new Job
                    {
                        BuildKey = key, Project = "App.csproj", RuntimeVersion = Sha, AspNetCoreVersion = Sha,
                        SdkVersion = "10.0.401", DesktopVersion = "10.0.12", SelfContained = true, NoBuild = true, NoClean = true
                    };
                    File.WriteAllText(Path.Combine(_root, "_options", $"{key}.json"), JsonConvert.SerializeObject(job.GetBuildKeyData()));
                    return job;
                }

                var firstJob = NewReader("concurrent-key");
                var secondJob = NewReader("concurrent-key");
                var firstContext = new JobContext();
                var secondContext = new JobContext();

                var firstTask = (Task<string>)clone.Invoke(null, new object[] { _root, firstJob, @"C:\unused-sdk", firstContext, CancellationToken.None });
                var secondTask = (Task<string>)clone.Invoke(null, new object[] { _root, secondJob, @"C:\unused-sdk", secondContext, CancellationToken.None });

                await Task.WhenAll(firstTask, secondTask);

                Assert.Equal(_root, await firstTask);
                Assert.Equal(_root, await secondTask);
                Assert.Equal(Sha, firstJob.RuntimeCommitSha);
                Assert.Equal(Sha, secondJob.RuntimeCommitSha);
                Assert.Null(firstJob.Error);
                Assert.Null(secondJob.Error);
                // Neither reader installed or mutated anything: the record is untouched.
                Assert.Equal(marker, File.ReadAllText(Path.Combine(_root, CiBuildRecord.FileName)));
            }
            finally { rootField.SetValue(null, previousRoot); }
        }

        /// <summary>
        /// Hermetic, real-CloneRestoreAndBuild hit tests. A stub `dotnet-install.ps1` is used in place of
        /// the real script so no network archive download happens, while still exercising the actual
        /// production install call (exact recorded version/feed/home), and the actual BCS base URL is
        /// pointed at a listener that fails everything except latestBuilds.json (serving an "advanced"
        /// SHA) to prove a hit never requests it.
        /// </summary>
        [Fact]
        public async Task FddHitInstallsFrozenRecordedVersionsWithoutAnyBcsLatestLookup()
        {
            var advancedSha = new string('9', 40);
            using var fixture = new HitFixture(_root, advancedSha);
            try
            {
                await fixture.ArrangeAsync();
                var job = fixture.NewHitJob(selfContained: false);
                var context = new JobContext();
                var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", BindingFlags.Static | BindingFlags.NonPublic);
                var result = await (Task<string>)clone.Invoke(null, new object[] { _root, job, @"C:\unused-sdk", context, CancellationToken.None });

                Assert.Equal(_root, result);
                Assert.True(string.IsNullOrEmpty(job.Error));
                Assert.Equal(Sha, job.RuntimeCommitSha);
                Assert.Equal(Sha, job.AspNetCoreCommitSha);
                Assert.NotNull(context.BuildCacheDotnetHome);
                Assert.True(File.Exists(Path.Combine(context.BuildCacheDotnetHome, "shared", "Microsoft.NETCore.App", "12.0.0-dev", ".version")));
                Assert.True(File.Exists(Path.Combine(context.BuildCacheDotnetHome, "shared", "Microsoft.AspNetCore.App", "12.0.0-dev", ".version")));
                fixture.AssertInstalledExactRecordedVersionsOnly();
                fixture.AssertNoBcsLatestOrVersionMarkerRequestsMade();
            }
            finally { fixture.Dispose(); }
        }

        [Fact]
        public async Task ScdHitNeverInstallsOrTouchesNetworkEvenWhenBcsIsUnavailable()
        {
            using var fixture = new HitFixture(_root, new string('9', 40), bcsAlwaysFails: true);
            try
            {
                await fixture.ArrangeAsync(selfContained: true);
                var job = fixture.NewHitJob(selfContained: true);
                var context = new JobContext();
                var clone = typeof(Startup).GetMethod("CloneRestoreAndBuild", BindingFlags.Static | BindingFlags.NonPublic);
                var result = await (Task<string>)clone.Invoke(null, new object[] { _root, job, @"C:\unused-sdk", context, CancellationToken.None });

                Assert.Equal(_root, result);
                Assert.True(string.IsNullOrEmpty(job.Error));
                Assert.Equal(Sha, job.RuntimeCommitSha);
                Assert.Null(context.BuildCacheDotnetHome);
                Assert.Equal(0, fixture.InstallInvocationCount);
                fixture.AssertNoBcsLatestOrVersionMarkerRequestsMade();
            }
            finally { fixture.Dispose(); }
        }

        /// <summary>
        /// Arranges a fully hermetic environment for a real CloneRestoreAndBuild hit: a stub
        /// dotnet-install.ps1 standing in for the real installer (recording its exact invocation and
        /// materializing the expected .version/deps.json/runtimeconfig.json so ValidateInstallation
        /// passes), and a local HTTP listener standing in for BCS that fails any latestBuilds.json /
        /// latest.version request (or always 500s, for the SCD "unavailable" case).
        /// </summary>
        private sealed class HitFixture : IDisposable
        {
            private readonly string _root;
            private readonly string _installPath;
            private readonly HttpListener _listener;
            private readonly Dictionary<FieldInfo, object> _savedFields = new();
            private readonly string _advancedSha;
            private readonly bool _bcsAlwaysFails;
            private Task _serving;
            private int _requests;

            public int InstallInvocationCount =>
                File.Exists(Path.Combine(_installPath, "install-log.txt"))
                    ? File.ReadAllLines(Path.Combine(_installPath, "install-log.txt")).Length
                    : 0;

            public HitFixture(string root, string advancedSha, bool bcsAlwaysFails = false)
            {
                _root = root;
                _advancedSha = advancedSha;
                _bcsAlwaysFails = bcsAlwaysFails;
                _installPath = Directory.CreateTempSubdirectory("crank-ci-install-").FullName;

                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                BaseUrl = $"http://127.0.0.1:{port}";
                _listener = new HttpListener();
                _listener.Prefixes.Add(BaseUrl + "/");
            }

            public string BaseUrl { get; }

            public async Task ArrangeAsync(bool selfContained = false)
            {
                var flags = BindingFlags.Static | BindingFlags.NonPublic;
                Save(typeof(Startup).GetField("_rootTempDir", flags), _root);
                Save(typeof(Startup).GetField("_buildCacheBaseUrl", flags), BaseUrl);
                Save(typeof(Startup).GetField("_dotnetInstallPath", flags), _installPath);
                Save(typeof(Startup).GetField("_pwsh", flags), "pwsh");
                Save(typeof(Startup).GetProperty("Logger", flags), new Serilog.LoggerConfiguration().CreateLogger());

                Directory.CreateDirectory(Path.Combine(_root, "published"));
                foreach (var file in new[] { "App.dll", "App.deps.json", "App.runtimeconfig.json" })
                    File.WriteAllText(Path.Combine(_root, "published", file), "published fixture");
                if (selfContained)
                    File.WriteAllText(Path.Combine(_root, "published", "App.exe"), "apphost fixture");

                // Matches the existing request guard: the _options/<BuildKey>.json file must already
                // reflect the exact same requested build-key data as the job about to request a hit.
                Directory.CreateDirectory(Path.Combine(_root, "_options"));
                File.WriteAllText(Path.Combine(_root, "_options", "ci-hit-fixture.json"),
                    JsonConvert.SerializeObject(NewHitJob(selfContained).GetBuildKeyData()));

                WriteInstallerStub();

                var selection = new CiBuildRecord.Inputs(
                    new(Sha, "12.0.0-dev", $"{BaseUrl}/builds/runtime/buildArtifacts/{Sha}/{BuildCacheClient.GetConfiguration("runtime", "win-x64")}/install"),
                    new(Sha, "12.0.0-dev", $"{BaseUrl}/builds/aspnetcore/buildArtifacts/{Sha}/{BuildCacheClient.GetConfiguration("aspnetcore", "win-x64")}/install"),
                    "12.0.0-dev", "12.0.0-dev", "10.0.401", "net10.0", "win-x64", selfContained,
                    "10.0.12", "10.0.12", "10.0.12", "App");

                var job = BuiltJob(Sha);
                CiBuildRecord.Write(_root, Path.Combine(_root, "published"), selection, job);

                _listener.Start();
                _serving = ServeAsync();
            }

            public Job NewHitJob(bool selfContained) => new()
            {
                BuildKey = "ci-hit-fixture", Project = "App.csproj", RuntimeVersion = Sha, AspNetCoreVersion = Sha,
                SdkVersion = "10.0.401", DesktopVersion = "10.0.12", SelfContained = selfContained, NoBuild = true, NoClean = true
            };

            public void AssertNoBcsLatestOrVersionMarkerRequestsMade() =>
                Assert.DoesNotContain(_requestPaths, p => p.Contains("latestBuilds.json") || p.EndsWith("/main/latest.version"));

            public void AssertInstalledExactRecordedVersionsOnly()
            {
                Assert.Equal(2, InstallInvocationCount);
                var log = File.ReadAllLines(Path.Combine(_installPath, "install-log.txt"));
                Assert.Contains(log, l => l.StartsWith("dotnet|12.0.0-dev|"));
                Assert.Contains(log, l => l.StartsWith("aspnetcore|12.0.0-dev|"));
                // The feed used for install came straight from the recorded AzureFeed; it embeds the full
                // commit SHA, proving the exact frozen build (not a re-resolved one) was installed.
                Assert.Contains(log, l => l.Contains(Sha));
            }

            private readonly List<string> _requestPaths = new();

            private async Task ServeAsync()
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();
                        lock (_requestPaths) { _requestPaths.Add(context.Request.Url.AbsolutePath); }
                        Interlocked.Increment(ref _requests);
                        if (_bcsAlwaysFails || !context.Request.Url.AbsolutePath.EndsWith("/latestBuilds.json"))
                        {
                            context.Response.StatusCode = 404;
                        }
                        else
                        {
                            // A hit must never even request this, but if it did, it would see an "advanced"
                            // (different) commit — proving the hit still freezes on the recorded SHA.
                            var payload = System.Text.Encoding.UTF8.GetBytes(
                                "{\"all\":{\"CommitSha\":\"" + _advancedSha + "\"}}");
                            context.Response.ContentLength64 = payload.Length;
                            await context.Response.OutputStream.WriteAsync(payload);
                        }
                        context.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            }

            private void WriteInstallerStub()
            {
                // Stands in for the real dotnet-install.ps1: records its exact invocation and materializes
                // only what ValidateInstallation actually checks, without any network access.
                File.WriteAllText(Path.Combine(_installPath, "dotnet-install.ps1"), """
                param(
                    [string]$Version,
                    [string]$Runtime,
                    [string]$Architecture,
                    [switch]$NoPath,
                    [switch]$SkipNonVersionedFiles,
                    [string]$InstallDir,
                    [string]$AzureFeed
                )
                Add-Content -Path (Join-Path $PSScriptRoot 'install-log.txt') -Value "$Runtime|$Version|$AzureFeed"
                $framework = if ($Runtime -eq 'aspnetcore') { 'Microsoft.AspNetCore.App' } else { 'Microsoft.NETCore.App' }
                $dir = Join-Path $InstallDir "shared\$framework\$Version"
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                if ($AzureFeed -match '([0-9a-f]{40})') { $sha = $Matches[1] } else { $sha = 'unknown' }
                Set-Content -Path (Join-Path $dir '.version') -Value "$sha`n$Version"
                Set-Content -Path (Join-Path $dir "$framework.deps.json") -Value '{}'
                Set-Content -Path (Join-Path $dir "$framework.runtimeconfig.json") -Value '{}'
                if ($Runtime -eq 'dotnet') { Set-Content -Path (Join-Path $InstallDir 'dotnet.exe') -Value 'stub' }
                """);
            }

            private void Save(FieldInfo field, object value)
            {
                _savedFields[field] = field.GetValue(null);
                field.SetValue(null, value);
            }

            private void Save(PropertyInfo property, object value)
            {
                // PropertyInfo isn't a FieldInfo; stash via a thin adapter key using its backing setter.
                _propertySaves.Add((property, property.GetValue(null)));
                property.SetValue(null, value);
            }

            private readonly List<(PropertyInfo Property, object Value)> _propertySaves = new();

            public void Dispose()
            {
                try { _listener?.Stop(); } catch { }
                try { _serving?.Wait(TimeSpan.FromSeconds(2)); } catch { }
                foreach (var (field, value) in _savedFields)
                    field.SetValue(null, value);
                foreach (var (property, value) in _propertySaves)
                    property.SetValue(null, value);
                try { Directory.Delete(_installPath, recursive: true); } catch { }
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
