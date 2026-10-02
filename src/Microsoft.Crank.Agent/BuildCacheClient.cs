// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Versioning;

namespace Microsoft.Crank.Agent
{
    // BCS only resolves installer feeds. Stock dotnet-install owns downloading and installing frameworks.
    internal static class BuildCacheClient
    {
        internal sealed record ResolvedBuild(string CommitSha, string Version, string AzureFeed);

        internal static bool IsCommitSha(string value) =>
            value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{40}\z");

        internal static string GetSelector(string version, string channel) =>
            string.IsNullOrEmpty(version) ? (string.IsNullOrEmpty(channel) ? "current" : channel) : version;

        internal static bool IsCiSelector(string selector) =>
            string.Equals(selector, "ci", StringComparison.OrdinalIgnoreCase) || IsCommitSha(selector);

        internal static string FormatVersion(string version, string commit) => $"{version}+{commit}";

        internal static void ValidateSelector(string selector)
        {
            if (!IsCiSelector(selector) &&
                !new[] { "current", "latest", "edge" }.Contains(selector, StringComparer.OrdinalIgnoreCase) &&
                !IsVersion(selector) &&
                !Regex.IsMatch(selector, @"\A\d+\.\d+(\.\*)?\z"))
            {
                throw new ArgumentException($"Invalid runtime selector '{selector}'. Use ci, a full 40-character commit SHA, current, latest, edge, or a feed version.");
            }
        }

        private static bool IsVersion(string value) =>
            Regex.IsMatch(value, @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z") && NuGetVersion.TryParse(value, out _);

        internal static string GetConfiguration(string repo, string rid)
        {
            var prefix = repo switch
            {
                "runtime" => "coreclr",
                "aspnetcore" => "aspnetcore",
                _ => throw new ArgumentException($"Unsupported BCS repository '{repo}'.")
            };
            var platform = rid switch
            {
                "linux-x64" => "x64_linux",
                "linux-arm64" => "arm64_linux",
                "win-x64" => "x64_windows",
                "win-arm64" => "arm64_windows",
                "win-x86" => "x86_windows",
                _ => throw new InvalidOperationException($"No BCS {repo} installer configuration for '{rid}'.")
            };
            return $"{prefix}_{platform}";
        }

        internal static async Task<ResolvedBuild> ResolveAsync(
            HttpClient client, string baseUrl, string repo, string selector, string rid, CancellationToken cancellationToken)
        {
            if (!IsCiSelector(selector))
            {
                throw new ArgumentException($"'{selector}' is not a CI selector.");
            }

            var config = GetConfiguration(repo, rid);
            var root = $"{baseUrl.TrimEnd('/')}/builds/{repo}";
            var sha = IsCommitSha(selector)
                ? selector
                : ReadCommit(await client.GetStringAsync($"{root}/latest/main/latestBuilds.json", cancellationToken), config);

            if (!IsCommitSha(sha))
            {
                throw new InvalidOperationException($"BCS {repo}/{config} did not resolve to a full 40-character commit SHA.");
            }

            sha = sha.ToLowerInvariant();
            var feed = $"{root}/buildArtifacts/{sha}/{config}/install";
            var product = repo == "runtime" ? "Runtime" : "aspnetcore/Runtime";
            var versionFile = $"{feed}/{product}/main/latest.version";
            var content = await client.GetStringAsync(versionFile, cancellationToken);
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length is < 1 or > 2 || !IsVersion(lines[^1]) ||
                (lines.Length == 2 && !string.Equals(lines[0], sha, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Invalid commit/version in BCS installer version file '{versionFile}'.");
            }

            Log.Info($"Build Cache: {repo} {sha}, version {lines[^1]}, installer feed {feed}");
            return new ResolvedBuild(sha, lines[^1], feed);
        }

        private static string ReadCommit(string json, string config)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty(config, out var entry) && !root.TryGetProperty("all", out entry))
            {
                throw new InvalidOperationException($"BCS latest index has no entry for '{config}'.");
            }

            if (entry.TryGetProperty("CommitSha", out var sha) || entry.TryGetProperty("commit_sha", out sha))
            {
                return sha.GetString();
            }

            throw new InvalidOperationException($"BCS latest index entry '{config}' has no commit.");
        }

        internal static void ValidateInstallation(string home, string framework, ResolvedBuild build)
        {
            var directory = Path.Combine(home, "shared", framework, build.Version);
            foreach (var file in new[] { ".version", $"{framework}.deps.json", $"{framework}.runtimeconfig.json" })
            {
                if (!File.Exists(Path.Combine(directory, file)))
                {
                    throw new InvalidOperationException($"BCS installer did not provide '{Path.Combine(directory, file)}'.");
                }
            }

            var installedCommit = File.ReadLines(Path.Combine(directory, ".version")).FirstOrDefault();
            if (!string.Equals(installedCommit, build.CommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"BCS {framework} installed commit '{installedCommit}', expected '{build.CommitSha}'.");
            }
        }

        internal static void PromoteAspNetCoreFramework(string home, string stagingHome) =>
            Directory.Move(Path.Combine(stagingHome, "shared", "Microsoft.AspNetCore.App"),
                Path.Combine(home, "shared", "Microsoft.AspNetCore.App"));

        internal static async Task<string> PrepareRuntimePacksAsync(HttpClient client, string dotnet, string root,
            string sdkVersion, string framework, string rid, ResolvedBuild runtime, ResolvedBuild aspnet,
            CancellationToken cancellationToken)
        {
            var source = Path.Combine(root, "runtime-packs");
            var packages = Path.Combine(root, "nuget");
            var bootstrap = Path.Combine(root, "restore-packs");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(bootstrap);
            var downloads = new XElement("ItemGroup");
            foreach (var (build, id, product) in new[]
            {
                (runtime, $"Microsoft.NETCore.App.Runtime.{rid}", "Runtime"),
                (aspnet, $"Microsoft.AspNetCore.App.Runtime.{rid}", "aspnetcore/Runtime")
            })
            {
                if (build == null)
                    continue;
                var filename = $"{id}.{build.Version}.nupkg";
                var url = $"{build.AzureFeed}/{product}/{build.Version}/{filename}";
                Log.Info($"Build Cache: downloading required SCD runtime pack {url}");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"BCS self-contained runtime pack is unavailable: {url} (HTTP {(int)response.StatusCode}). No feed fallback is allowed.");
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(Path.Combine(source, filename));
                await response.Content.CopyToAsync(file, cancellationToken);
                downloads.Add(new XElement("PackageDownload", new XAttribute("Include", id), new XAttribute("Version", $"[{build.Version}]")));
            }

            // Bootstrap only PackageDownload items, not a benchmark or framework reference restore.
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("TargetFramework", framework),
                    new XElement("DisableImplicitFrameworkReferences", true)),
                downloads)).Save(Path.Combine(bootstrap, "packs.csproj"));
            new XDocument(new XElement("configuration",
                new XElement("packageSources", new XElement("clear"),
                    new XElement("add", new XAttribute("key", "ci"), new XAttribute("value", source)))))
                .Save(Path.Combine(bootstrap, "NuGet.Config"));
            File.WriteAllText(Path.Combine(bootstrap, "global.json"),
                JsonSerializer.Serialize(new { sdk = new { version = sdkVersion, rollForward = "disable" } }));

            await ProcessUtil.RunAsync(dotnet,
                $"restore packs.csproj --configfile NuGet.Config --packages \"{packages}\"",
                workingDirectory: bootstrap, throwOnError: true, cancellationToken: cancellationToken);
            return packages;
        }
    }
}
