// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.Agent;
using Xunit;

namespace Microsoft.Crank.UnitTests;

public class BuildCacheDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAtomicPublishAcceptsOnlyIdenticalWinner(bool different)
    {
        using var fixture = new BuildCacheTestFixture();
        var destination = Path.Combine(fixture.Root, "published");
        var first = Path.Combine(fixture.Root, "first.partial");
        var second = Path.Combine(fixture.Root, "second.partial");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, different ? "second" : "first");
        await BuildCacheClient.PublishAtomicAsync(first, destination, () => throw new InvalidOperationException("No winner expected."));
        var checkedWinner = false;
        var race = BuildCacheClient.PublishAtomicAsync(second, destination, async () =>
        {
            checkedWinner = true;
            if (await File.ReadAllTextAsync(destination) != (different ? "second" : "first"))
            {
                throw new BuildCacheClient.BuildCacheIncompleteException("Immutable conflict.");
            }
        });
        if (different)
        {
            await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() => race);
        }
        else
        {
            await race;
        }
        Assert.True(checkedWinner);
        Assert.Equal("first", File.ReadAllText(destination));
        Assert.False(File.Exists(second));
    }

    [Theory]
    [InlineData("nuspec")]
    [InlineData("runtime-list")]
    [InlineData("missing-asset")]
    [InlineData("wrong-tfm")]
    [InlineData("framework-config")]
    [InlineData("wrong-source")]
    [InlineData("wrong-commit")]
    public async Task InvalidOriginalPackageMetadataFailsClosed(string fault)
    {
        using var fixture = new BuildCacheTestFixture("win-x64");
        var archives = fixture.PrepareBuild("aspnetcore", BuildCacheTestFixture.AspNetCoreSha);
        BuildCacheTestFixture.RewriteZip(archives, archives.Keys.Single(k => k.Contains(".Runtime.")), files =>
        {
            switch (fault)
            {
                case "nuspec":
                    var nuspec = files.Keys.Single(k => k.EndsWith(".nuspec"));
                    files[nuspec] = files[nuspec].Replace("<version>12.0.0-ci</version>", "<version>10.0.0</version>");
                    break;
                case "runtime-list": files.Remove("data/RuntimeList.xml"); break;
                case "missing-asset": files.Remove(files.Keys.First(k => k.EndsWith(".dll"))); break;
                case "wrong-tfm": files["data/RuntimeList.xml"] = files["data/RuntimeList.xml"].Replace("11.0", "12.0"); break;
                case "framework-config":
                    var config = files.Keys.Single(k => k.EndsWith(".runtimeconfig.json"));
                    files[config] = files[config].Replace("net11.0", "net12.0");
                    break;
                case "wrong-source":
                    var source = files.Keys.Single(k => k.EndsWith(".nuspec"));
                    files[source] = files[source].Replace("dotnet/aspnetcore", "dotnet/runtime");
                    break;
                case "wrong-commit":
                    var commit = files.Keys.Single(k => k.EndsWith(".nuspec"));
                    files[commit] = files[commit].Replace(BuildCacheTestFixture.AspNetCoreSha, BuildCacheTestFixture.RuntimeSha);
                    break;
            }
        });
        fixture.PublishBundle("aspnetcore", BuildCacheTestFixture.AspNetCoreSha, archives);
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            BuildCacheClientTests.Prepare(fixture, "aspnetcore", BuildCacheTestFixture.AspNetCoreSha));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.CacheRoot, "jobs")));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("commit")]
    [InlineData("deps")]
    public async Task CompleteDistributionRequiresMatchingOriginalHostAndMetadata(string fault)
    {
        using var fixture = new BuildCacheTestFixture("win-x64");
        var archives = fixture.PrepareBuild("runtime", BuildCacheTestFixture.RuntimeSha);
        BuildCacheTestFixture.RewriteZip(archives, archives.Keys.Single(k => k.EndsWith(".zip")), files =>
        {
            switch (fault)
            {
                case "host": files.Remove("dotnet.exe"); break;
                case "commit":
                    var version = files.Keys.Single(k => k.EndsWith("/.version"));
                    files[version] = new string('a', 40) + "\n" + BuildCacheTestFixture.Version;
                    break;
                case "deps":
                    var deps = files.Keys.Single(k => k.EndsWith(".deps.json"));
                    files[deps] = files[deps].Replace("win-x64", "linux-x64");
                    break;
            }
        });
        fixture.PublishBundle("runtime", BuildCacheTestFixture.RuntimeSha, archives);
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            BuildCacheClientTests.Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha));
    }

    [Fact]
    public async Task ConcurrentJobsShareOnlyVerifiedArchives()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var downloads = await Task.WhenAll(
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid),
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid));
        var results = await Task.WhenAll(fixture.Client.PrepareAsync(downloads[0]), fixture.Client.PrepareAsync(downloads[1]));
        Assert.NotEqual(results[0].Directory, results[1].Directory);
        Assert.Single(fixture.Requests);
        Assert.Equal(downloads[0], downloads[1]);
        BuildCacheClient.CleanupDirectory(results[0].Directory);
        Assert.True(Directory.Exists(results[1].Directory));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(downloads[0].Archive), "*.zip"));
    }

    [Fact]
    public async Task LatestUsesOnlyRequestedRepositoryAndNormalizesFullSha()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var result = await fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", null, fixture.Rid);
        Assert.Equal(BuildCacheTestFixture.RuntimeSha, result.CommitSha);
        Assert.Contains("/builds/runtime/latest/main/latestBuilds.json", fixture.Requests);
        Assert.DoesNotContain(fixture.Requests, r => r.Contains("aspnetcore"));
        fixture.Requests.Clear();
        await fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha.ToUpperInvariant(), fixture.Rid);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task MissingCompleteBundleFailsWithoutRawOrFeedFallback()
    {
        using var fixture = new BuildCacheTestFixture();
        var error = await Assert.ThrowsAsync<BuildCacheClient.BuildCacheNotFoundException>(() =>
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid));
        Assert.Contains("Legacy raw-only", error.Message);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncatedOrEmptyDownloadsAreNotCached(bool wrongLength)
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.SendAsync = (_, _) =>
        {
            var content = new ByteArrayContent(wrongLength ? [1, 2, 3] : []);
            content.Headers.ContentLength = wrongLength ? 4 : 0;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("dir\\..\\outside")]
    [InlineData("dir/file:ads")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir/../file")]
    public async Task RejectsUnsafeArchivePathsBeforeWritingOutsideRoot(string entry)
    {
        using var fixture = new BuildCacheTestFixture();
        foreach (var zip in new[] { true, false })
        {
            var archive = Path.Combine(fixture.Root, zip ? "unsafe.zip" : "unsafe.tar.gz");
            File.WriteAllBytes(archive, BuildCacheTestFixture.CreateArchive(new() { [entry] = "bad" }, zip));
            await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
                BuildCacheClient.ExtractArchiveAsync(archive, Path.Combine(fixture.Root, zip ? "zip" : "tar"), zip ? "zip" : "tar.gz", default));
        }
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    public async Task RejectsTarLinksAndSpecialEntries(TarEntryType type)
    {
        using var fixture = new BuildCacheTestFixture();
        var archive = Path.Combine(fixture.Root, "unsafe.tar.gz");
        using (var output = File.Create(archive))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(type, "link");
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                entry.LinkName = "../outside";
            }
            writer.WriteEntry(entry);
        }
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            BuildCacheClient.ExtractArchiveAsync(archive, Path.Combine(fixture.Root, "extract"), "tar.gz", default));
    }

    [Fact]
    public async Task RejectsZipSymlinkAndCaseAliasedEntries()
    {
        using var fixture = new BuildCacheTestFixture();
        var archive = Path.Combine(fixture.Root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            BuildCacheClient.ExtractArchiveAsync(archive, Path.Combine(fixture.Root, "extract"), "zip", default));
        File.WriteAllBytes(archive, BuildCacheTestFixture.CreateArchive(new() { ["file"] = "1", ["FILE"] = "2" }, true));
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            BuildCacheClient.ExtractArchiveAsync(archive, Path.Combine(fixture.Root, "alias"), "zip", default));
    }

    [Fact]
    public async Task CancellationCleansOnlyOwnedExtractsAndPartialDownloads()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        using var cts = new CancellationTokenSource();
        fixture.SendAsync = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid, cts.Token));
        fixture.SendAsync = null;
        var resolved = await fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.PrepareAsync(resolved, cts.Token));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.CacheRoot, "jobs")));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CachedArchivesAreReverified()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var build = await BuildCacheClientTests.Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        File.WriteAllText(build.Download.Archive, "corrupt");
        await Assert.ThrowsAsync<BuildCacheClient.BuildCacheIncompleteException>(() =>
            fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid));
        Assert.True(Directory.Exists(build.Directory));
    }

    [Fact]
    public async Task CacheIdentityIncludesFullSourceAndActualBundleBytes()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var first = await fixture.Client.ResolveAsync(BuildCacheTestFixture.BaseUrl, "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid);
        var second = await fixture.Client.ResolveAsync("https://another-source.invalid", "runtime", BuildCacheTestFixture.RuntimeSha, fixture.Rid);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.NotEqual(first.Archive, second.Archive);
        var expectedHash = BuildCacheClient.Hash(fixture.Artifacts[fixture.BundlePath("runtime", BuildCacheTestFixture.RuntimeSha)]);
        Assert.Equal(expectedHash, Path.GetFileNameWithoutExtension(first.Archive));
        Assert.Equal(expectedHash, Path.GetFileNameWithoutExtension(second.Archive));
    }
}
