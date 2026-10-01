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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Versioning;

namespace Microsoft.Crank.Agent;

// Only complete downloads are shared. Extracts, runtime homes and NuGet caches belong to one job.
internal sealed class BuildCacheClient
{
    internal const string RepoNameRuntime = "runtime";
    internal const string RepoNameAspNetCore = "aspnetcore";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks = new();
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;

    internal BuildCacheClient(HttpClient httpClient = null, string cacheRoot = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(Path.GetTempPath(), "crank-buildcache"));
    }

    internal static readonly IReadOnlyDictionary<string, string> RuntimeConfigurations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "coreclr_x64_linux",
            ["linux-arm64"] = "coreclr_arm64_linux",
            ["win-x64"] = "coreclr_x64_windows",
            ["win-arm64"] = "coreclr_arm64_windows",
            ["win-x86"] = "coreclr_x86_windows"
        };

    internal static readonly IReadOnlyDictionary<string, string> AspNetCoreConfigurations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "aspnetcore_x64_linux",
            ["linux-arm64"] = "aspnetcore_arm64_linux",
            ["win-x64"] = "aspnetcore_x64_windows",
            ["win-arm64"] = "aspnetcore_arm64_windows",
            ["win-x86"] = "aspnetcore_x86_windows"
        };

    internal sealed record CachedBuild(string Repository, string CommitSha, string Rid, string Archive, string Identity);
    internal sealed record Package(string Role, string Id, string Version, string Archive, string HostRid = null);
    internal sealed class PreparedBuild
    {
        public CachedBuild Download { get; init; }
        public string Directory { get; init; }
        public string Version { get; init; }
        public string Tfm { get; init; }
        public IReadOnlyList<Package> Packages { get; init; }
        public string FrameworkName => Download.Repository == RepoNameRuntime ? "Microsoft.NETCore.App" : "Microsoft.AspNetCore.App";
        public string RoleDirectory(string role) => Path.Combine(Directory, role);
    }

    internal sealed record FrameworkRequirement
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string RollForward { get; set; }
        public bool? ApplyPatches { get; set; }
    }

    internal sealed class BuildCacheIncompleteException(string message) : InvalidOperationException(message);
    internal sealed class BuildCacheNotFoundException(string message) : InvalidOperationException(message);
    internal static bool IsCommitSha(string value) => FrameworkSelector.IsCommit(value);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string BundleFileName(string repo, string rid) =>
        $"{(repo == RepoNameRuntime ? "RuntimeDistribution" : "FrameworkPackages")}_{(rid.StartsWith("win-") ? "windows" : "linux")}_{rid.Split('-')[1]}_Release_{(repo == RepoNameRuntime ? "coreclr" : "aspnetcore")}.zip";

    internal async Task<CachedBuild> ResolveAsync(string baseUrl, string repo, string commitSha, string rid, CancellationToken cancellationToken = default)
    {
        var configurations = repo switch
        {
            RepoNameRuntime => RuntimeConfigurations,
            RepoNameAspNetCore => AspNetCoreConfigurations,
            _ => throw new ArgumentException($"Unknown Build Cache repository '{repo}'.", nameof(repo))
        };
        if (!configurations.TryGetValue(rid, out var config))
        {
            throw new InvalidOperationException($"Build Cache does not support '{repo}' on RID '{rid}'.");
        }
        var source = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Build Cache source must be an HTTP(S) URL.", nameof(baseUrl));
        }
        if (string.IsNullOrEmpty(commitSha))
        {
            using var response = await GetResponseAsync(new Uri(source, $"builds/{repo}/latest/main/latestBuilds.json").AbsoluteUri, cancellationToken);
            var latest = ParseLatestBuilds(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!latest.Entries.TryGetValue(config, out var entry) && !latest.Entries.TryGetValue("all", out entry))
            {
                throw new BuildCacheNotFoundException($"Build Cache: no latest build for '{repo}/{config}'.");
            }
            commitSha = entry.CommitSha;
        }
        if (!IsCommitSha(commitSha))
        {
            throw new ArgumentException("Build Cache requires a full 40-hex commit SHA (short pins are not supported).");
        }
        commitSha = commitSha.ToLowerInvariant();
        var url = new Uri(source, $"builds/{repo}/buildArtifacts/{commitSha}/{config}/{BundleFileName(repo, rid)}").AbsoluteUri;
        var archive = await DownloadBundleAsync(url, cancellationToken);
        var identity = Hash(Encoding.UTF8.GetBytes(url + "\n" + Path.GetFileNameWithoutExtension(archive)));
        return new(repo, commitSha, rid, archive, identity);
    }

    private async Task<string> DownloadBundleAsync(string url, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_cacheRoot, "downloads", Hash(Encoding.UTF8.GetBytes(url)));
        var gate = DownloadLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var cached = SingleFile(Directory.GetFiles(directory, "*.zip"), "cached complete bundle", optional: true);
            if (cached != null)
            {
                await VerifyCachedBundleAsync(cached, cancellationToken);
                return cached;
            }
            var partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using var response = await GetResponseAsync(url, cancellationToken);
                await using (var output = File.Create(partial))
                {
                    await response.Content.CopyToAsync(output, cancellationToken);
                    if (output.Length == 0 || (response.Content.Headers.ContentLength is long length && output.Length != length))
                    {
                        throw new BuildCacheIncompleteException("Build Cache complete bundle download is empty or truncated.");
                    }
                }
                var digest = await FileHashAsync(partial, cancellationToken);
                var destination = Path.Combine(directory, digest + ".zip");
                await PublishAtomicAsync(partial, destination, () => VerifyCachedBundleAsync(destination, cancellationToken));
                // A different winner for one immutable URL is not a valid cache hit.
                return SingleFile(Directory.GetFiles(directory, "*.zip"), "immutable cached complete bundle");
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task VerifyCachedBundleAsync(string path, CancellationToken cancellationToken)
    {
        if (await FileHashAsync(path, cancellationToken) != Path.GetFileNameWithoutExtension(path))
        {
            throw new BuildCacheIncompleteException("Build Cache cached complete bundle is corrupt. Remove that cached download before retrying.");
        }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new BuildCacheNotFoundException($"Build Cache complete bundle not found: {url}. Legacy raw-only artifacts are unsupported; no feed fallback is performed.");
                    }
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (HttpRequestException ex) when (attempt < 3 && (ex.StatusCode == null || ex.StatusCode == HttpStatusCode.RequestTimeout ||
                ex.StatusCode == HttpStatusCode.TooManyRequests || (int)ex.StatusCode >= 500))
            {
                Log.Info($"Build Cache download attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }

    internal async Task<PreparedBuild> PrepareAsync(CachedBuild build, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_cacheRoot, "jobs", Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = Path.Combine(directory, "bundle");
            await ExtractArchiveAsync(build.Archive, bundle, "zip", cancellationToken);
            if (Directory.GetDirectories(bundle).Length != 0)
            {
                throw new BuildCacheIncompleteException("Build Cache complete bundle must contain original archives at its root.");
            }
            var files = Directory.GetFiles(bundle).ToList();
            var framework = build.Repository == RepoNameRuntime ? "Microsoft.NETCore.App" : "Microsoft.AspNetCore.App";
            var packages = new List<Package>();
            async Task AddPackage(string role, string id, bool optional = false)
            {
                var archive = SingleFile(files.Where(f => Path.GetFileName(f).StartsWith(id + ".", StringComparison.OrdinalIgnoreCase) &&
                    f.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)), id, optional);
                if (archive == null)
                {
                    return;
                }
                files.Remove(archive);
                var extracted = Path.Combine(directory, role);
                await ExtractArchiveAsync(archive, extracted, "nupkg", cancellationToken);
                packages.Add(ReadPackage(extracted, archive, role, id, build));
            }
            await AddPackage("runtime-pack", framework + ".Runtime." + build.Rid);
            await AddPackage("targeting-pack", framework + ".Ref");
            if (build.Repository == RepoNameRuntime)
            {
                await AddPackage("apphost-pack", "Microsoft.NETCore.App.Host." + build.Rid);
                await AddPackage("crossgen2-pack", "Microsoft.NETCore.App.Crossgen2", optional: true);
            }
            var version = packages[0].Version;
            if (packages.Any(p => p.Version != version))
            {
                throw new BuildCacheIncompleteException("Build Cache bundle contains packages with different framework versions.");
            }
            string frameworkDirectory;
            if (build.Repository == RepoNameRuntime)
            {
                var format = build.Rid.StartsWith("win-") ? "zip" : "tar.gz";
                var archive = SingleFile(files.Where(f => Path.GetFileName(f) == $"dotnet-runtime-{version}-{build.Rid}.{format}"), "canonical runtime distribution");
                files.Remove(archive);
                var distribution = Path.Combine(directory, "runtime-distribution");
                await ExtractArchiveAsync(archive, distribution, format, cancellationToken);
                frameworkDirectory = Path.Combine(distribution, "shared", framework, version);
                RequireFile(Path.Combine(distribution, build.Rid.StartsWith("win-") ? "dotnet.exe" : "dotnet"));
                RequireFile(Path.Combine(distribution, "host", "fxr", version, NativeLibrary("hostfxr", build.Rid)));
                foreach (var name in new[] { "System.Private.CoreLib.dll", NativeLibrary("coreclr", build.Rid), NativeLibrary("hostpolicy", build.Rid) })
                {
                    RequireFile(Path.Combine(frameworkDirectory, name));
                }
                var stamp = File.ReadAllLines(Path.Combine(frameworkDirectory, ".version"));
                if (stamp.Length < 2 || !string.Equals(stamp[0], build.CommitSha, StringComparison.OrdinalIgnoreCase) || stamp[1] != version ||
                    Directory.GetDirectories(Path.Combine(distribution, "shared")).Length != 1 ||
                    Directory.GetDirectories(Path.Combine(distribution, "shared", framework)).Length != 1 ||
                    Directory.GetDirectories(Path.Combine(distribution, "host", "fxr")).Length != 1)
                {
                    throw new BuildCacheIncompleteException("Build Cache distribution commit/version does not match the requested build and its packages.");
                }
            }
            else
            {
                var lib = Path.Combine(directory, "runtime-pack", "runtimes", build.Rid, "lib");
                RequireDirectory(lib);
                frameworkDirectory = SingleFile(Directory.GetDirectories(lib), "ASP.NET runtime-pack TFM");
            }
            if (files.Count != 0)
            {
                throw new BuildCacheIncompleteException("Build Cache bundle contains unexpected or duplicate archives.");
            }
            ReadRequirements(Path.Combine(frameworkDirectory, framework + ".runtimeconfig.json"), out var tfm);
            if (!Regex.IsMatch(tfm ?? "", @"^net[0-9]+\.[0-9]+$"))
            {
                throw new BuildCacheIncompleteException("Build Cache framework runtimeconfig is missing a supported TFM.");
            }
            using var deps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(frameworkDirectory, framework + ".deps.json")));
            if (deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString() != $".NETCoreApp,Version=v{tfm[3..]}/{build.Rid}")
            {
                throw new BuildCacheIncompleteException("Build Cache framework deps.json does not match its TFM and requested RID.");
            }
            var prepared = new PreparedBuild { Download = build, Directory = directory, Version = version, Tfm = tfm, Packages = packages };
            foreach (var package in packages.Where(p => p.Role is "runtime-pack" or "targeting-pack"))
            {
                var extracted = prepared.RoleDirectory(package.Role);
                RequireDirectory(package.Role == "targeting-pack" ? Path.Combine(extracted, "ref", tfm) : Path.Combine(extracted, "runtimes", build.Rid, "lib", tfm));
                ValidateFileList(extracted, package.Role == "runtime-pack" ? "RuntimeList.xml" : "FrameworkList.xml", framework, tfm);
            }
            return prepared;
        }
        catch
        {
            CleanupDirectory(directory);
            throw;
        }
    }

    private static Package ReadPackage(string directory, string archive, string role, string expectedId, CachedBuild build)
    {
        var nuspec = SingleFile(Directory.GetFiles(directory, "*.nuspec"), "package nuspec");
        var metadata = XDocument.Load(nuspec).Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata");
        string Value(string name) => metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value;
        var id = Value("id");
        var version = Value("version");
        var repository = metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == "repository");
        var source = ((string)repository?.Attribute("url"))?.TrimEnd('/').Replace(".git", "");
        var hostRid = role == "crossgen2-pack" && id?.StartsWith(expectedId + ".", StringComparison.OrdinalIgnoreCase) == true ? id[(expectedId.Length + 1)..] : null;
        if (!IsVersion(version) || !string.Equals(id, expectedId + (hostRid == null ? "" : "." + hostRid), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(archive), $"{id}.{version}.nupkg", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((string)repository?.Attribute("commit"), build.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            (source != "https://github.com/dotnet/" + build.Repository && source != "https://github.com/dotnet/dotnet") ||
            (role == "crossgen2-pack" && (hostRid == null || !RuntimeConfigurations.ContainsKey(hostRid))))
        {
            throw new BuildCacheIncompleteException($"Build Cache package '{Path.GetFileName(archive)}' has the wrong source, commit, version, ID or RID.");
        }
        if (role == "apphost-pack")
        {
            RequireFile(Path.Combine(directory, "runtimes", build.Rid, "native", build.Rid.StartsWith("win-") ? "apphost.exe" : "apphost"));
        }
        if (role == "crossgen2-pack")
        {
            RequireFile(Path.Combine(directory, "tools", hostRid.StartsWith("win-") ? "crossgen2.exe" : "crossgen2"));
        }
        return new(role, id, version, archive, hostRid);
    }

    private static string SingleFile(IEnumerable<string> paths, string description, bool optional = false)
    {
        var files = paths.ToArray();
        if (files.Length == 1)
        {
            return files[0];
        }
        if (optional && files.Length == 0)
        {
            return null;
        }
        throw new BuildCacheIncompleteException($"Build Cache requires exactly one {description}; found {files.Length}.");
    }

    private static void ValidateFileList(string directory, string name, string framework, string tfm)
    {
        var path = Path.Combine(directory, "data", name);
        RequireFile(path);
        var root = XDocument.Load(path).Root;
        if (root?.Name.LocalName != "FileList" || (string)root.Attribute("FrameworkName") != framework ||
            (string)root.Attribute("TargetFrameworkIdentifier") != ".NETCoreApp" || (string)root.Attribute("TargetFrameworkVersion") != tfm[3..] ||
            !root.Elements("File").Any())
        {
            throw new BuildCacheIncompleteException($"Build Cache {name} has invalid framework/TFM metadata.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.Elements("File"))
        {
            RequireFile(ArchivePath(directory, (string)file.Attribute("Path") ?? "", false, paths));
        }
    }

    internal static async Task PublishAtomicAsync(string partial, string destination, Func<Task> validateWinner)
    {
        try
        {
            try
            {
                File.Move(partial, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                await validateWinner();
            }
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    internal static async Task ExtractArchiveAsync(string archive, string destination, string format, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (format == "tar.gz")
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            TarEntry entry;
            while ((entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken)) != null)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                {
                    throw new BuildCacheIncompleteException($"Build Cache archive links and special entries are not supported: '{entry.Name}'.");
                }
                var path = ArchivePath(destination, entry.Name, entry.EntryType == TarEntryType.Directory, paths);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    await using (var output = new FileStream(path, FileMode.CreateNew))
                    {
                        if (entry.DataStream != null)
                        {
                            await entry.DataStream.CopyToAsync(output, cancellationToken);
                        }
                    }
                    if (!System.OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1FF);
                    }
                }
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                if (type != 0 && type != 0x8000 && type != 0x4000)
                {
                    throw new BuildCacheIncompleteException($"Build Cache archive links and special entries are not supported: '{entry.FullName}'.");
                }
                var isDirectory = entry.FullName.EndsWith('/');
                var path = ArchivePath(destination, entry.FullName, isDirectory, paths);
                if (isDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                await using (var output = new FileStream(path, FileMode.CreateNew))
                await using (var input = entry.Open())
                {
                    await input.CopyToAsync(output, cancellationToken);
                }
                var mode = (entry.ExternalAttributes >> 16) & 0x1FF;
                if (!System.OperatingSystem.IsWindows() && mode != 0)
                {
                    File.SetUnixFileMode(path, (UnixFileMode)mode);
                }
            }
        }
    }

    private static string ArchivePath(string destination, string name, bool directory, HashSet<string> paths)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }
        if (directory)
        {
            name = name.TrimEnd('/');
        }
        if (directory && name.Length == 0)
        {
            return destination;
        }
        var parts = name.Split('/');
        if (parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
            p.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 ||
            Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)) ||
            !paths.Add(name))
        {
            throw new BuildCacheIncompleteException($"Build Cache archive contains an unsafe or duplicate path: '{name}'.");
        }
        return Path.Combine(destination, Path.Combine(parts));
    }

    private static bool IsVersion(string value) => NuGetVersion.TryParse(value, out _) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    internal static List<FrameworkRequirement> ReadRequirements(string config, out string tfm)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(config));
        var options = document.RootElement.GetProperty("runtimeOptions");
        if (options.TryGetProperty("rollForwardOnNoCandidateFx", out _))
        {
            throw new BuildCacheIncompleteException("Build Cache requires modern rollForward metadata; legacy rollForwardOnNoCandidateFx is unsupported.");
        }
        tfm = options.TryGetProperty("tfm", out var tfmProperty) ? tfmProperty.GetString() : null;
        var requirements = new List<FrameworkRequirement>();
        void Add(JsonElement element)
        {
            var requirement = new FrameworkRequirement
            {
                Name = element.GetProperty("name").GetString(),
                Version = element.GetProperty("version").GetString(),
                RollForward = element.TryGetProperty("rollForward", out var roll) ? roll.GetString() :
                    options.TryGetProperty("rollForward", out roll) ? roll.GetString() : null,
                ApplyPatches = element.TryGetProperty("applyPatches", out var patch) ? patch.GetBoolean() :
                    options.TryGetProperty("applyPatches", out patch) ? patch.GetBoolean() : null
            };
            if (string.IsNullOrEmpty(requirement.Name) || !IsVersion(requirement.Version) || requirements.Any(r => r.Name == requirement.Name) ||
                requirement.RollForward is not (null or "Disable" or "LatestPatch" or "Minor" or "LatestMinor" or "Major" or "LatestMajor"))
            {
                throw new BuildCacheIncompleteException("Build Cache framework runtimeconfig has invalid or unsupported requirements.");
            }
            requirements.Add(requirement);
        }
        if (options.TryGetProperty("framework", out var framework))
        {
            Add(framework);
        }
        if (options.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var item in frameworks.EnumerateArray())
            {
                Add(item);
            }
        }
        return requirements;
    }

    internal string CreateDotnetHome(string feedHome, string runtimeVersion, string aspNetCoreVersion, PreparedBuild runtime, PreparedBuild aspNetCore)
    {
        if ((runtime != null && runtime.Version != runtimeVersion) || (aspNetCore != null && aspNetCore.Version != aspNetCoreVersion))
        {
            throw new BuildCacheIncompleteException("Build Cache frameworks must retain their original versions; rebuild invalid reuse metadata.");
        }
        var home = Path.Combine(_cacheRoot, "jobs", "home-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(home);
            if (runtime != null)
            {
                CopyDirectory(runtime.RoleDirectory("runtime-distribution"), home);
            }
            else
            {
                var dotnet = GetPlatformMoniker().StartsWith("win-") ? "dotnet.exe" : "dotnet";
                File.Copy(Path.Combine(feedHome, dotnet), Path.Combine(home, dotnet));
                CopyDirectory(Path.Combine(feedHome, "host", "fxr", runtimeVersion), Path.Combine(home, "host", "fxr", runtimeVersion));
                CopyDirectory(Path.Combine(feedHome, "shared", "Microsoft.NETCore.App", runtimeVersion),
                    Path.Combine(home, "shared", "Microsoft.NETCore.App", runtimeVersion));
            }
            var aspNetDirectory = Path.Combine(home, "shared", "Microsoft.AspNetCore.App", aspNetCoreVersion);
            if (aspNetCore != null)
            {
                var runtimes = Path.Combine(aspNetCore.RoleDirectory("runtime-pack"), "runtimes", aspNetCore.Download.Rid);
                CopyDirectory(Path.Combine(runtimes, "lib", aspNetCore.Tfm), aspNetDirectory);
                if (Directory.Exists(Path.Combine(runtimes, "native")))
                {
                    CopyDirectory(Path.Combine(runtimes, "native"), aspNetDirectory);
                }
            }
            else
            {
                CopyDirectory(Path.Combine(feedHome, "shared", "Microsoft.AspNetCore.App", aspNetCoreVersion), aspNetDirectory);
            }
            ValidateHomeRequirements(home, runtimeVersion, aspNetCoreVersion);
            return home;
        }
        catch
        {
            CleanupDirectory(home);
            throw;
        }
    }

    internal static void ValidateHomeRequirements(string home, string runtimeVersion, string aspNetCoreVersion)
    {
        var installed = new Dictionary<string, string>
        {
            ["Microsoft.NETCore.App"] = runtimeVersion,
            ["Microsoft.AspNetCore.App"] = aspNetCoreVersion
        };
        foreach (var (name, version) in installed)
        {
            var config = Path.Combine(home, "shared", name, version, name + ".runtimeconfig.json");
            foreach (var requirement in ReadRequirements(config, out _))
            {
                if (!installed.TryGetValue(requirement.Name, out var selected) || !SatisfiesRequirement(requirement, selected))
                {
                    throw new BuildCacheIncompleteException($"Selected {name} {version} requires {requirement.Name} {requirement.Version} " +
                        $"(rollForward={requirement.RollForward ?? "Minor"}, applyPatches={requirement.ApplyPatches?.ToString() ?? "default"}); " +
                        $"selected version '{selected ?? "(missing)"}' cannot satisfy that declaration. Select compatible builds; requirements are never lowered.");
                }
            }
        }
    }

    internal static bool SatisfiesRequirement(FrameworkRequirement requirement, string selected)
    {
        var requested = NuGetVersion.Parse(requirement.Version);
        var actual = NuGetVersion.Parse(selected);
        if (actual < requested || (!requested.IsPrerelease && actual.IsPrerelease))
        {
            return false;
        }
        if (requirement.RollForward == "Disable" ||
            (requirement.ApplyPatches == false && actual.Major == requested.Major && actual.Minor == requested.Minor && requirement.RollForward == null))
        {
            return actual == requested;
        }
        return (requirement.RollForward ?? "Minor") switch
        {
            "LatestPatch" => actual.Major == requested.Major && actual.Minor == requested.Minor,
            "Minor" or "LatestMinor" => actual.Major == requested.Major,
            "Major" or "LatestMajor" => true,
            _ => false
        };
    }

    internal static void CopyDirectory(string source, string destination)
    {
        RequireDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: false);
            if (!System.OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new BuildCacheIncompleteException($"Build Cache complete artifact is missing '{path}'.");
        }
    }

    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new BuildCacheIncompleteException($"Build Cache complete artifact is missing '{path}'.");
        }
    }

    internal static bool CleanupDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Build Cache: failed to clean up job directory '{path}': {ex.Message}");
            return false;
        }
    }

    internal static string NativeLibrary(string name, string rid) => rid.StartsWith("win-") ? name + ".dll" : rid.StartsWith("osx-") ? "lib" + name + ".dylib" : "lib" + name + ".so";
    internal static string GetNativeLibName(string name) => NativeLibrary(name, GetPlatformMoniker());
    internal static string GetPlatformMoniker()
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return (System.OperatingSystem.IsWindows() ? "win-" : System.OperatingSystem.IsMacOS() ? "osx-" : "linux-") + architecture;
    }

    internal sealed class LatestBuildsResponse
    {
        public string BranchName { get; set; }
        public Dictionary<string, LatestBuildEntry> Entries { get; } = new(StringComparer.Ordinal);
    }
    internal sealed class LatestBuildEntry
    {
        public string CommitSha { get; set; }
        public string CommitTime { get; set; }
    }
    internal static LatestBuildsResponse ParseLatestBuilds(string json)
    {
        var result = new LatestBuildsResponse();
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is "branch_name" or "BranchName")
            {
                result.BranchName = property.Value.GetString();
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                string Read(string pascal, string snake) =>
                    property.Value.TryGetProperty(pascal, out var value) || property.Value.TryGetProperty(snake, out value) ? value.GetString() : null;
                result.Entries.Add(property.Name, new() { CommitSha = Read("CommitSha", "commit_sha"), CommitTime = Read("CommitTime", "commit_time") });
            }
        }
        return result;
    }
}
