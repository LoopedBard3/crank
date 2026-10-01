// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Crank.Agent;

namespace Microsoft.Crank.UnitTests;

internal sealed class BuildCacheTestFixture : IDisposable
{
    internal const string BaseUrl = "https://build-cache.invalid";
    internal const string Version = "12.0.0-ci";
    internal const string Tfm = "net11.0";
    internal const string RuntimeSha = "603403d9cb49d3d1c35b56bcff024ce99a8c5c3a";
    internal const string AspNetCoreSha = "abcdef0123456789abcdef0123456789abcdef01";
    private readonly HttpClient _httpClient;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "crank_bcs_fixture_" + Guid.NewGuid().ToString("N"));
    internal string CacheRoot => Path.Combine(Root, "cache");
    internal string FeedHome => Path.Combine(Root, "feed");
    internal BuildCacheClient Client { get; }
    internal ConcurrentDictionary<string, byte[]> Artifacts { get; } = new();
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> SendAsync { get; set; }
    internal string Rid { get; }

    internal BuildCacheTestFixture(string rid = null)
    {
        Rid = rid ?? BuildCacheClient.GetPlatformMoniker();
        Directory.CreateDirectory(CacheRoot);
        _httpClient = new HttpClient(new FakeHandler(this));
        Client = new BuildCacheClient(_httpClient, CacheRoot);
    }

    internal string BundlePath(string repo, string sha) =>
        $"/builds/{repo}/buildArtifacts/{sha}/{Config(repo)}/{BuildCacheClient.BundleFileName(repo, Rid)}";
    private string Config(string repo) => repo == "runtime" ? BuildCacheClient.RuntimeConfigurations[Rid] : BuildCacheClient.AspNetCoreConfigurations[Rid];

    internal void PrepareArtifacts()
    {
        PrepareBuild("runtime", RuntimeSha);
        PrepareBuild("aspnetcore", AspNetCoreSha);
    }

    internal Dictionary<string, byte[]> PrepareBuild(string repo, string sha, string version = Version, string tfm = Tfm)
    {
        var framework = repo == "runtime" ? "Microsoft.NETCore.App" : "Microsoft.AspNetCore.App";
        var requirements = repo == "runtime" ? new List<BuildCacheClient.FrameworkRequirement>() :
            [new() { Name = "Microsoft.NETCore.App", Version = version }];
        var config = JsonSerializer.Serialize(new { runtimeOptions = new { tfm, frameworks = requirements } }, BuildCacheClient.JsonOptions);
        var deps = JsonSerializer.Serialize(new { runtimeTarget = new { name = $".NETCoreApp,Version=v{tfm[3..]}/{Rid}" } });
        var frameworkFiles = new Dictionary<string, string>
        {
            [framework + ".deps.json"] = deps,
            [framework + ".runtimeconfig.json"] = config,
            [".version"] = sha + "\n" + version + "\n",
            [repo == "runtime" ? "System.Private.CoreLib.dll" : "Microsoft.AspNetCore.dll"] = sha,
            [repo == "runtime" ? "System.Runtime.dll" : "Microsoft.AspNetCore.Hosting.dll"] = sha
        };
        var archives = new Dictionary<string, byte[]>();
        if (repo == "runtime")
        {
            var distribution = frameworkFiles.ToDictionary(k => $"shared/{framework}/{version}/{k.Key}", v => v.Value);
            distribution[Rid.StartsWith("win-") ? "dotnet.exe" : "dotnet"] = "BCS muxer " + sha;
            distribution[$"host/fxr/{version}/{BuildCacheClient.NativeLibrary("hostfxr", Rid)}"] = "BCS fxr " + sha;
            distribution[$"shared/{framework}/{version}/{BuildCacheClient.NativeLibrary("hostpolicy", Rid)}"] = "BCS hostpolicy";
            distribution[$"shared/{framework}/{version}/{BuildCacheClient.NativeLibrary("coreclr", Rid)}"] = "BCS coreclr";
            var format = Rid.StartsWith("win-") ? "zip" : "tar.gz";
            archives[$"dotnet-runtime-{version}-{Rid}.{format}"] = CreateArchive(distribution, format == "zip");
        }
        void AddPackage(string id, Dictionary<string, string> files)
        {
            files[id + ".nuspec"] = $"<package><metadata><id>{id}</id><version>{version}</version><repository type=\"git\" url=\"https://github.com/dotnet/{repo}\" commit=\"{sha}\" /></metadata></package>";
            archives[$"{id}.{version}.nupkg"] = CreateArchive(files, true);
        }
        var runtimeFiles = frameworkFiles.ToDictionary(k => $"runtimes/{Rid}/lib/{tfm}/{k.Key}", v => v.Value);
        runtimeFiles[$"runtimes/{Rid}/native/{BuildCacheClient.NativeLibrary(repo == "runtime" ? "coreclr" : "aspnet", Rid)}"] = sha;
        runtimeFiles["data/RuntimeList.xml"] = $"<FileList FrameworkName=\"{framework}\" TargetFrameworkIdentifier=\".NETCoreApp\" TargetFrameworkVersion=\"{tfm[3..]}\">" +
            string.Concat(runtimeFiles.Keys.Select(p => $"<File Path=\"{p}\" />")) + "</FileList>";
        AddPackage(framework + ".Runtime." + Rid, runtimeFiles);
        AddPackage(framework + ".Ref", new()
        {
            [$"ref/{tfm}/" + (repo == "runtime" ? "System.Runtime.dll" : "Microsoft.AspNetCore.dll")] = sha,
            ["data/FrameworkList.xml"] = $"<FileList FrameworkName=\"{framework}\" TargetFrameworkIdentifier=\".NETCoreApp\" TargetFrameworkVersion=\"{tfm[3..]}\"><File Path=\"ref/{tfm}/{(repo == "runtime" ? "System.Runtime.dll" : "Microsoft.AspNetCore.dll")}\" /></FileList>"
        });
        if (repo == "runtime")
        {
            AddPackage("Microsoft.NETCore.App.Host." + Rid,
                new() { [$"runtimes/{Rid}/native/" + (Rid.StartsWith("win-") ? "apphost.exe" : "apphost")] = sha });
        }
        PublishBundle(repo, sha, archives);
        Artifacts[$"/builds/{repo}/latest/main/latestBuilds.json"] =
            Encoding.UTF8.GetBytes($"{{\"branch_name\":\"main\",\"{Config(repo)}\":{{\"CommitSha\":\"{sha}\",\"CommitTime\":\"2026-01-01\"}}}}");
        return archives;
    }

    internal void PublishBundle(string repo, string sha, Dictionary<string, byte[]> archives) =>
        Artifacts[BundlePath(repo, sha)] = CreateZip(archives);

    internal static void RewriteZip(Dictionary<string, byte[]> archives, string name, Action<Dictionary<string, string>> edit)
    {
        using var bytes = new MemoryStream(archives[name]);
        using var zip = new ZipArchive(bytes);
        var files = zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
        edit(files);
        archives[name] = CreateArchive(files, true);
    }

    internal string ImportBundle(string path, string repo)
    {
        var bytes = File.ReadAllBytes(path);
        using var outer = new ZipArchive(new MemoryStream(bytes));
        var prefix = (repo == "runtime" ? "Microsoft.NETCore.App" : "Microsoft.AspNetCore.App") + ".Runtime." + Rid + ".";
        using var package = new ZipArchive(outer.Entries.Single(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Open());
        using var nuspec = package.Entries.Single(e => e.Name.EndsWith(".nuspec")).Open();
        var sha = XDocument.Load(nuspec).Descendants().Single(e => e.Name.LocalName == "repository").Attribute("commit").Value;
        Artifacts[BundlePath(repo, sha)] = bytes;
        return sha;
    }

    internal void PrepareFeedHome(string version = Version)
    {
        foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" })
        {
            var directory = Path.Combine(FeedHome, "shared", framework, version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, framework + ".runtimeconfig.json"), $"{{\"runtimeOptions\":{{\"tfm\":\"{Tfm}\"}}}}");
            File.WriteAllText(Path.Combine(directory, framework + ".deps.json"), "{}");
            File.WriteAllText(Path.Combine(directory, ".version"), (framework == "Microsoft.NETCore.App" ? RuntimeSha : AspNetCoreSha) + "\n" + version);
            File.WriteAllText(Path.Combine(directory, "feed-only.dll"), "must not enter selected BCS framework");
        }
        var host = Path.Combine(FeedHome, "host", "fxr", version);
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, BuildCacheClient.NativeLibrary("hostfxr", Rid)), "feed fxr");
        File.WriteAllText(Path.Combine(FeedHome, Rid.StartsWith("win-") ? "dotnet.exe" : "dotnet"), "feed muxer");
    }

    internal static byte[] CreateZip(Dictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.ExternalAttributes = 0x81ED << 16;
                using var stream = entry.Open();
                stream.Write(contents);
            }
        }
        return output.ToArray();
    }

    internal static byte[] CreateArchive(Dictionary<string, string> files, bool zip)
    {
        if (zip)
        {
            return CreateZip(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var archive = new TarWriter(gzip))
        {
            foreach (var (name, contents) in files)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(contents));
                archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = (UnixFileMode)0x1ED });
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeHandler(BuildCacheTestFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.Requests.Enqueue(request.RequestUri.AbsolutePath);
            if (fixture.SendAsync != null)
            {
                return fixture.SendAsync(request, cancellationToken);
            }
            return Task.FromResult(fixture.Artifacts.TryGetValue(request.RequestUri.AbsolutePath, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
