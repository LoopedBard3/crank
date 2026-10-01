// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Crank.Models;
using NuGet.Configuration;

namespace Microsoft.Crank.Agent;

internal static class BuildCachePublish
{
    internal sealed class Workspace
    {
        public string Directory { get; init; }
        public string Targets { get; init; }
        public string NuGetConfig { get; init; }
        public string Packages => Path.Combine(Directory, "packages");
        public string Packs => Path.Combine(Directory, "packs");
        public string Arguments =>
            $"/p:CustomAfterMicrosoftCommonTargets=\"{Targets}\" /p:RestoreConfigFile=\"{NuGetConfig}\" " +
            $"/p:RestorePackagesPath=\"{Packages}\" /p:NetCoreTargetingPackRoot=\"{Packs}\" " +
            "/p:RestoreFallbackFolders= /p:DisableImplicitNuGetFallbackFolder=true " +
            "/p:DisableTransitiveFrameworkReferenceDownloads=true /p:RestoreEnablePackagePruning=false ";

        public void SetEnvironment(IDictionary<string, string> environment)
        {
            environment["NUGET_PACKAGES"] = Packages;
            environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Directory, "http-cache");
            environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
    }

    internal static Workspace CreateWorkspace(string applicationDirectory, string tfm, string rid,
        BuildCacheClient.PreparedBuild runtime, BuildCacheClient.PreparedBuild aspNetCore,
        IReadOnlyDictionary<string, string> additionalSources = null)
    {
        var builds = new[] { runtime, aspNetCore }.Where(b => b != null).ToArray();
        var directory = Path.Combine(builds[0].Directory, "publish");
        var workspace = new Workspace
        {
            Directory = directory,
            Targets = Path.Combine(directory, "BuildCache.targets"),
            NuGetConfig = Path.Combine(directory, "NuGet.config")
        };
        var feed = Path.Combine(directory, "feed");
        System.IO.Directory.CreateDirectory(feed);
        var packageIds = new List<string>();
        foreach (var build in builds)
        {
            if (build.Tfm != tfm)
            {
                throw new InvalidOperationException($"Build Cache {build.Download.Repository} packs target '{build.Tfm}', but the project targets '{tfm}'. " +
                    "Select a matching commit or explicitly set job.framework; Crank does not retarget a project from a commit or product version.");
            }
            foreach (var package in build.Packages)
            {
                File.Copy(package.Archive, Path.Combine(feed, Path.GetFileName(package.Archive)));
                BuildCacheClient.CopyDirectory(build.RoleDirectory(package.Role), Path.Combine(workspace.Packs, package.Id, package.Version));
                packageIds.Add(package.Id);
            }
        }
        WriteNuGetConfig(Settings.LoadDefaultSettings(applicationDirectory), workspace, feed, packageIds, additionalSources);
        WriteTargets(workspace, tfm, rid, runtime, aspNetCore);
        return workspace;
    }

    internal static void WriteNuGetConfig(ISettings effective, Workspace workspace, string feed, List<string> packageIds,
        IReadOnlyDictionary<string, string> additionalSources = null)
    {
        // Materialize NuGet's merged settings without mutating any ancestor/user configuration.
        // Clone credential items verbatim; never access the decrypted Password accessor.
        new XDocument(new XElement("configuration")).Save(workspace.NuGetConfig);
        var destination = Settings.LoadSpecificSettings(workspace.Directory, Path.GetFileName(workspace.NuGetConfig));
        var sections = effective.GetConfigFilePaths()
            .SelectMany(path => XDocument.Load(path).Root?.Elements().Select(e => e.Name.LocalName) ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (section is "packageSources" or "packageSourceMapping" or "fallbackPackageFolders")
            {
                continue;
            }
            foreach (var item in effective.GetSection(section)?.Items ?? [])
            {
                destination.AddOrUpdate(section, (SettingItem)item.Clone());
            }
        }
        const string key = "crank-bcs-immutable";
        var sources = new PackageSourceProvider(effective).LoadPackageSources()
            .Where(s => s.Name != key).Select(s => s.Clone()).ToList();
        var addedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalSources != null)
        {
            foreach (var (name, url) in additionalSources)
            {
                var existing = sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Source, url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    sources.Add(new PackageSource(url, name));
                    addedSources.Add(name);
                }
                else
                {
                    addedSources.Add(existing.Name);
                }
            }
        }
        // PackageSourceProvider resolves relative source paths against each item's original file.
        foreach (var source in sources)
        {
            source.IsMachineWide = false;
        }
        sources.Add(new PackageSource(feed, key));
        destination.AddOrUpdate("packageSources", new ClearItem());
        new PackageSourceProvider(destination).SavePackageSources(sources);
        destination.AddOrUpdate("fallbackPackageFolders", new ClearItem());
        destination.AddOrUpdate("packageSourceMapping", new ClearItem());
        var mappings = effective.GetSection("packageSourceMapping")?.Items.OfType<PackageSourceMappingSourceItem>().ToList() ?? [];
        foreach (var source in sources.Where(s => s.Name != key))
        {
            var mapping = mappings.FirstOrDefault(m => string.Equals(m.Key, source.Name, StringComparison.OrdinalIgnoreCase));
            var patterns = mapping?.Patterns.Select(p => p.Pattern).ToList() ??
                (mappings.Count == 0 || addedSources.Contains(source.Name) ? ["*"] : new List<string>());
            patterns.RemoveAll(p => packageIds.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (patterns.Count > 0)
            {
                destination.AddOrUpdate("packageSourceMapping",
                    new PackageSourceMappingSourceItem(source.Name, patterns.Select(p => new PackagePatternItem(p))));
            }
        }
        destination.AddOrUpdate("packageSourceMapping", new PackageSourceMappingSourceItem(key, packageIds.Select(id => new PackagePatternItem(id))));
        destination.SaveToDisk();
    }

    private static void WriteTargets(Workspace workspace, string tfm, string rid, BuildCacheClient.PreparedBuild runtime, BuildCacheClient.PreparedBuild aspNetCore)
    {
        var target = new XElement("Target", new XAttribute("Name", "CrankSelectImmutableFrameworkPacks"),
            new XAttribute("BeforeTargets", "ProcessFrameworkReferences"),
            new XAttribute("Condition", "'$(TargetFrameworkIdentifier)' == '.NETCoreApp'"),
            new XElement("Error", new XAttribute("Condition", "'$(PublishAot)' == 'true' Or '$(PublishTrimmed)' == 'true' Or '$(PublishSingleFile)' == 'true' Or '$(UseMonoRuntime)' == 'true'"),
                new XAttribute("Text", "Build Cache does not support NativeAOT, trimming, single-file or Mono publishing. No feed fallback is allowed.")),
            new XElement("Error", new XAttribute("Condition", $"'$(RuntimeIdentifier)' != '' And '$(RuntimeIdentifier)' != '{rid}'"),
                new XAttribute("Text", $"Build Cache packs are for {rid}; cross-RID publishing is not supported.")),
            new XElement("Error", new XAttribute("Condition", $"'$(TargetFramework)' != '{tfm}'"),
                new XAttribute("Text", $"Build Cache selected packs require {tfm}, including referenced projects.")),
            new XElement("Error", new XAttribute("Condition", "'%(FrameworkReference.Identity)' != '' And '%(FrameworkReference.Identity)' != 'Microsoft.NETCore.App' And '%(FrameworkReference.Identity)' != 'Microsoft.AspNetCore.App'"),
                new XAttribute("Text", "Build Cache supports only Microsoft.NETCore.App and Microsoft.AspNetCore.App framework references, not %(FrameworkReference.Identity).")));
        var items = new XElement("ItemGroup");
        foreach (var build in new[] { runtime, aspNetCore }.Where(b => b != null))
        {
            var framework = build.FrameworkName;
            // Replace only this TFM's selected framework, preserving SDK behavior for the feed side.
            items.Add(new XElement("KnownFrameworkReference", new XAttribute("Remove", framework),
                new XAttribute("Condition", $"'%(KnownFrameworkReference.TargetFramework)' == '{tfm}'")));
            items.Add(new XElement("KnownFrameworkReference", new XAttribute("Include", framework),
                new XAttribute("TargetFramework", tfm),
                new XAttribute("RuntimeFrameworkName", framework),
                new XAttribute("DefaultRuntimeFrameworkVersion", build.Version),
                new XAttribute("LatestRuntimeFrameworkVersion", build.Version),
                new XAttribute("TargetingPackName", framework + ".Ref"),
                new XAttribute("TargetingPackVersion", build.Version),
                new XAttribute("RuntimePackNamePatterns", framework + ".Runtime.**RID**"),
                new XAttribute("RuntimePackRuntimeIdentifiers", rid)));
            items.Add(new XElement("FrameworkReference", new XAttribute("Update", framework),
                new XElement("RuntimeFrameworkVersion", build.Version),
                new XElement("TargetingPackVersion", build.Version)));
        }
        if (runtime != null)
        {
            items.Add(new XElement("KnownAppHostPack", new XAttribute("Remove", "Microsoft.NETCore.App"),
                new XAttribute("Condition", $"'%(KnownAppHostPack.TargetFramework)' == '{tfm}'")));
            items.Add(new XElement("KnownAppHostPack", new XAttribute("Include", "Microsoft.NETCore.App"),
                new XAttribute("TargetFramework", tfm), new XAttribute("AppHostPackNamePattern", "Microsoft.NETCore.App.Host.**RID**"),
                new XAttribute("AppHostPackVersion", runtime.Version), new XAttribute("AppHostRuntimeIdentifiers", rid)));
            var crossgen = runtime.Packages.SingleOrDefault(p => p.Role == "crossgen2-pack");
            if (crossgen == null)
            {
                target.Add(new XElement("Error", new XAttribute("Condition", "'$(PublishReadyToRun)' == 'true'"),
                    new XAttribute("Text", "Build Cache ReadyToRun requires a matching per-commit crossgen2-pack for this host RID.")));
            }
            else
            {
                target.Add(new XElement("Error", new XAttribute("Condition", $"'$(PublishReadyToRun)' == 'true' And '$(NETCoreSdkRuntimeIdentifier)' != '{crossgen.HostRid}'"),
                    new XAttribute("Text", "Build Cache does not support cross-host ReadyToRun.")));
                items.Add(new XElement("KnownCrossgen2Pack", new XAttribute("Remove", "Microsoft.NETCore.App.Crossgen2"),
                    new XAttribute("Condition", $"'%(KnownCrossgen2Pack.TargetFramework)' == '{tfm}'")));
                items.Add(new XElement("KnownCrossgen2Pack", new XAttribute("Include", "Microsoft.NETCore.App.Crossgen2"),
                    new XAttribute("TargetFramework", tfm), new XAttribute("Crossgen2PackNamePattern", "Microsoft.NETCore.App.Crossgen2.**RID**"),
                    new XAttribute("Crossgen2PackVersion", crossgen.Version),
                    new XAttribute("Crossgen2RuntimeIdentifiers", crossgen.HostRid),
                    new XAttribute("Crossgen2PortableRuntimeIdentifiers", crossgen.HostRid)));
            }
        }
        target.Add(items);
        var verify = new XElement("Target", new XAttribute("Name", "CrankVerifyImmutableFrameworkPacks"),
            new XAttribute("AfterTargets", "ResolveFrameworkReferences"),
            new XAttribute("Condition", "'$(TargetFrameworkIdentifier)' == '.NETCoreApp'"));
        foreach (var build in new[] { runtime, aspNetCore }.Where(b => b != null))
        {
            foreach (var package in build.Packages)
            {
                var item = package.Role switch
                {
                    "targeting-pack" => "ResolvedTargetingPack",
                    "runtime-pack" => "ResolvedRuntimePack",
                    "crossgen2-pack" => "ResolvedCrossgen2Pack",
                    _ => "ResolvedAppHostPack"
                };
                var pathMetadata = package.Role is "runtime-pack" or "crossgen2-pack" ? "PackageDirectory" : "Path";
                var expected = Path.Combine(workspace.Packs, package.Id, package.Version);
                if (package.Role == "apphost-pack")
                {
                    expected = Path.Combine(expected, "runtimes", rid, "native", rid.StartsWith("win-") ? "apphost.exe" : "apphost");
                    verify.Add(new XElement("Error",
                        new XAttribute("Condition", $"'$(UseAppHost)' == 'true' And '$(AppHostSourcePath)' != '{expected}'"),
                        new XAttribute("Text", $"Build Cache SDK resolved apphost outside its selected immutable pack: $(AppHostSourcePath).")));
                    continue;
                }
                // Runtime packs can be resolved from the private NuGet cache instead of the packs
                // root; both are fed exclusively by the verified, exact-ID mapped local package.
                var alternate = Path.Combine(workspace.Packages, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
                verify.Add(new XElement("Error",
                    new XAttribute("Condition", $"'%({item}.NuGetPackageId)' == '{package.Id}' And " +
                        $"('%({item}.NuGetPackageVersion)' != '{package.Version}' Or " +
                        $"('%({item}.{pathMetadata})' != '{expected}' And '%({item}.{pathMetadata})' != '{alternate}'))"),
                    new XAttribute("Text", $"Build Cache SDK resolved {package.Id} outside its selected immutable pack: %({item}.{pathMetadata}).")));
            }
        }
        verify.Add(new XElement("WriteLinesToFile",
            new XAttribute("File", "$(IntermediateOutputPath)crank-bcs-packs.txt"),
            new XAttribute("Overwrite", "true"),
            new XAttribute("Lines", "@(ResolvedTargetingPack->'ref|%(NuGetPackageId)|%(NuGetPackageVersion)|%(Path)');@(ResolvedRuntimePack->'runtime|%(NuGetPackageId)|%(NuGetPackageVersion)|%(PackageDirectory)');@(ResolvedAppHostPack->'host|%(NuGetPackageId)|%(NuGetPackageVersion)|%(Path)');@(ResolvedCrossgen2Pack->'crossgen2|%(NuGetPackageId)|%(NuGetPackageVersion)|%(PackageDirectory)')")));
        new XDocument(new XElement("Project", target, verify)).Save(workspace.Targets);
    }

    internal sealed class BuildMetadata
    {
        public int SchemaVersion { get; set; } = 1;
        public string RuntimeSelector { get; set; }
        public string AspNetCoreSelector { get; set; }
        public string RuntimeIdentity { get; set; }
        public string AspNetCoreIdentity { get; set; }
        public string RuntimeVersion { get; set; }
        public string AspNetCoreVersion { get; set; }
        public string RuntimeCommitSha { get; set; }
        public string AspNetCoreCommitSha { get; set; }
        public string Rid { get; set; }
        public string Framework { get; set; }
        public string SdkVersion { get; set; }
        public bool SelfContained { get; set; }
        public List<Dependency> Dependencies { get; set; }
    }

    internal static BuildMetadata ReadMetadata(string path)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<BuildMetadata>(File.ReadAllText(path), BuildCacheClient.JsonOptions);
            if (metadata?.SchemaVersion != 1 || metadata.Dependencies == null ||
                !NuGet.Versioning.NuGetVersion.TryParse(metadata.RuntimeVersion, out _) ||
                !NuGet.Versioning.NuGetVersion.TryParse(metadata.AspNetCoreVersion, out _) ||
                string.IsNullOrEmpty(metadata.Framework) || string.IsNullOrEmpty(metadata.Rid))
            {
                throw new InvalidOperationException("Invalid or legacy build metadata.");
            }
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Build Cache reuse marker '{path}' is missing or invalid. Rebuild without build reuse.", ex);
        }
    }

    internal static void WriteMetadata(string path, BuildMetadata metadata)
    {
        var partial = path + ".partial";
        try
        {
            File.WriteAllText(partial, JsonSerializer.Serialize(metadata, BuildCacheClient.JsonOptions));
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
