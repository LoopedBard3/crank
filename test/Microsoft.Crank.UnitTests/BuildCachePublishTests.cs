// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Crank.Agent;
using NuGet.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Crank.UnitTests;

public class BuildCachePublishTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GeneratedTargetsSelectAllPackRolesAndUsePrivateSourceMapping()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var runtime = await BuildCacheClientTests.Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        var aspnet = await BuildCacheClientTests.Prepare(fixture, "aspnetcore", BuildCacheTestFixture.AspNetCoreSha);
        var workspace = BuildCachePublish.CreateWorkspace(fixture.Root, BuildCacheTestFixture.Tfm, fixture.Rid, runtime, aspnet);
        var targets = XDocument.Load(workspace.Targets);
        Assert.Equal(2, targets.Descendants("KnownFrameworkReference").Count(e => e.Attribute("Include") != null));
        Assert.Contains(targets.Descendants("KnownAppHostPack"), e => (string)e.Attribute("AppHostPackVersion") == BuildCacheTestFixture.Version);
        Assert.Contains(targets.Descendants("Error"), e => ((string)e.Attribute("Text")).Contains("crossgen2-pack"));
        var configuration = XDocument.Load(workspace.NuGetConfig);
        var mappings = configuration.Descendants("packageSource").Single(e => (string)e.Attribute("key") == "crank-bcs-immutable");
        Assert.Equal(5, mappings.Elements("package").Count());
        Assert.StartsWith(runtime.Directory, workspace.Packages);
        Assert.Contains("/p:NetCoreTargetingPackRoot=", workspace.Arguments);
    }

    [Fact]
    public async Task MismatchedFrameworkFailsRatherThanRetargetingProject()
    {
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareArtifacts();
        var runtime = await BuildCacheClientTests.Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        var error = Assert.Throws<InvalidOperationException>(() => BuildCachePublish.CreateWorkspace(fixture.Root, "net12.0", fixture.Rid, runtime, null));
        Assert.Contains("explicitly set job.framework", error.Message);
    }

    [Fact]
    public void NuGetConfigurationPreservesEffectiveHierarchyAndEncryptedCredentials()
    {
        using var fixture = new BuildCacheTestFixture();
        var child = Path.Combine(fixture.Root, "child");
        var outputDirectory = Path.Combine(fixture.Root, "output");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(outputDirectory);
        var parentConfig = Path.Combine(fixture.Root, "NuGet.config");
        var childConfig = Path.Combine(child, "NuGet.config");
        File.WriteAllText(parentConfig, """
            <configuration>
              <packageSources><clear/><add key="parent" value="./parent-feed"/><add key="auth" value="https://private.invalid/v3/index.json"/></packageSources>
              <packageSourceCredentials><auth><add key="Username" value="fixture"/><add key="Password" value="opaque-encrypted-fixture"/></auth></packageSourceCredentials>
              <disabledPackageSources><add key="auth" value="true"/></disabledPackageSources>
            </configuration>
            """);
        File.WriteAllText(childConfig, """
            <configuration>
              <packageSources><add key="child" value="./child-feed"/></packageSources>
              <packageSourceMapping>
                <packageSource key="parent"><package pattern="Parent.*"/><package pattern="Microsoft.NETCore.App.Ref"/></packageSource>
                <packageSource key="child"><package pattern="Child.*"/></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var effective = Settings.LoadSettingsGivenConfigPaths([childConfig, parentConfig]);
        var workspace = new BuildCachePublish.Workspace { Directory = outputDirectory, NuGetConfig = Path.Combine(outputDirectory, "NuGet.config") };
        BuildCachePublish.WriteNuGetConfig(effective, workspace, Path.Combine(fixture.Root, "bcs-feed"), ["Microsoft.NETCore.App.Ref"]);
        var restored = Settings.LoadSpecificSettings(outputDirectory, "NuGet.config");
        var sources = new PackageSourceProvider(restored).LoadPackageSources().ToList();
        Assert.Equal(Path.Combine(fixture.Root, "parent-feed"), sources.Single(s => s.Name == "parent").Source);
        Assert.Equal(Path.Combine(child, "child-feed"), sources.Single(s => s.Name == "child").Source);
        var auth = sources.Single(s => s.Name == "auth");
        Assert.False(auth.IsEnabled);
        Assert.False(auth.Credentials.IsPasswordClearText);
        Assert.Equal("opaque-encrypted-fixture", auth.Credentials.PasswordText);
        Assert.DoesNotContain("ClearTextPassword", File.ReadAllText(workspace.NuGetConfig));
        var mappings = PackageSourceMapping.GetPackageSourceMapping(restored);
        Assert.Equal(["parent"], mappings.GetConfiguredPackageSources("Parent.Library"));
        Assert.Equal(["child"], mappings.GetConfiguredPackageSources("Child.Library"));
        Assert.Equal(["crank-bcs-immutable"], mappings.GetConfiguredPackageSources("Microsoft.NETCore.App.Ref"));
        Assert.Contains("value=\"./parent-feed\"", File.ReadAllText(parentConfig));
        Assert.Contains("value=\"./child-feed\"", File.ReadAllText(childConfig));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public async Task RealSdkPublishesAndRunsSelectedPacks(bool selfContained, bool selectRuntime, bool selectAspNet, bool readyToRun)
    {
        // Opt-in real-SDK smoke test. Fixture root contains complete official transport bundles and
        // an offline feed; no test consults or mutates the user's global NuGet cache.
        var fixtureRoot = Environment.GetEnvironmentVariable("CRANK_BCS_SDK_FIXTURE");
        var sdkHome = Environment.GetEnvironmentVariable("CRANK_BCS_SDK_HOME");
        if (string.IsNullOrEmpty(fixtureRoot) || string.IsNullOrEmpty(sdkHome))
        {
            return;
        }
        using var fixture = new BuildCacheTestFixture();
        var runtimeSha = fixture.ImportBundle(Path.Combine(fixtureRoot, BuildCacheClient.BundleFileName("runtime", fixture.Rid)), "runtime");
        var aspNetSha = fixture.ImportBundle(Path.Combine(fixtureRoot, BuildCacheClient.BundleFileName("aspnetcore", fixture.Rid)), "aspnetcore");
        var runtime = selectRuntime ? await BuildCacheClientTests.Prepare(fixture, "runtime", runtimeSha) : null;
        var aspNet = selectAspNet ? await BuildCacheClientTests.Prepare(fixture, "aspnetcore", aspNetSha) : null;
        var app = Path.Combine(fixture.Root, "app");
        Directory.CreateDirectory(app);
        var tfm = (runtime ?? aspNet).Tfm;
        var version = (runtime ?? aspNet).Version;
        var library = Path.Combine(fixture.Root, "library");
        var portable = Path.Combine(fixture.Root, "portable");
        Directory.CreateDirectory(portable);
        File.WriteAllText(Path.Combine(portable, "Portable.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(portable, "Portable.cs"), "public class Portable { public static bool Enabled => true; }");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "Library.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>" +
            $"<ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" RuntimeFrameworkVersion=\"{version}\" />" +
            $"<ProjectReference Include=\"{Path.Combine(portable, "Portable.csproj")}\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(library, "Library.cs"), "public class Library { public static System.Type Host => Portable.Enabled ? typeof(Microsoft.AspNetCore.Hosting.IWebHost) : null; }");
        File.WriteAllText(Path.Combine(app, "Smoke.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{tfm}</TargetFramework></PropertyGroup>" +
            $"<ItemGroup><ProjectReference Include=\"{Path.Combine(library, "Library.csproj")}\" /><PackageReference Include=\"Smoke.Parent\" Version=\"1.0.0\" />" +
            $"<FrameworkReference Update=\"Microsoft.NETCore.App\" RuntimeFrameworkVersion=\"{version}\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(app, "Program.cs"),
            "System.Console.WriteLine(typeof(object).Assembly.Location); System.Console.WriteLine(Library.Host.Assembly.Location);");
        var dependencyFeed = Path.Combine(fixture.Root, "dependencies");
        Directory.CreateDirectory(dependencyFeed);
        foreach (var id in new[] { "Smoke.Parent", "Smoke.Leaf" })
        {
            var dependencies = id == "Smoke.Parent" ? "<dependencies><dependency id=\"Smoke.Leaf\" version=\"[1.0.0]\" /></dependencies>" : "";
            File.WriteAllBytes(Path.Combine(dependencyFeed, id + ".1.0.0.nupkg"), BuildCacheTestFixture.CreateArchive(new Dictionary<string, string>
            {
                [id + ".nuspec"] = $"<package><metadata><id>{id}</id><version>1.0.0</version><authors>Test</authors><description>Offline transitive restore test</description>{dependencies}</metadata></package>",
                [$"lib/{tfm}/_._"] = ""
            }, true));
        }
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "offline"), new XAttribute("value", Path.Combine(fixtureRoot, "feed"))),
            new XElement("add", new XAttribute("key", "dependencies"), new XAttribute("value", dependencyFeed))))).Save(Path.Combine(app, "NuGet.config"));
        var workspace = BuildCachePublish.CreateWorkspace(app, tfm, fixture.Rid, runtime, aspNet);
        var published = Path.Combine(app, "published");
        var dotnet = Path.Combine(sdkHome, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var publish = new ProcessStartInfo(dotnet,
            $"publish \"{Path.Combine(app, "Smoke.csproj")}\" --disable-build-servers -c Release -o \"{published}\" " +
            $"-r {fixture.Rid} --self-contained {selfContained.ToString().ToLowerInvariant()} -p:NuGetAudit=false " +
            $"-p:PublishReadyToRun={readyToRun.ToString().ToLowerInvariant()} " + workspace.Arguments);
        workspace.SetEnvironment(publish.Environment);
        publish.Environment["DOTNET_ROOT"] = sdkHome;
        await Run(publish, app);

        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "obj", "project.assets.json")));
        Assert.All(assets.RootElement.GetProperty("packageFolders").EnumerateObject(),
            property => Assert.Equal(Path.GetFullPath(workspace.Packages).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(property.Name).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: OperatingSystem.IsWindows()));
        Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty("Smoke.Leaf/1.0.0", out _));
        Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty("Portable/1.0.0", out _));
        var packReport = File.ReadAllLines(Directory.GetFiles(Path.Combine(app, "obj"), "crank-bcs-packs.txt", SearchOption.AllDirectories).Single());
        output.WriteLine(string.Join(Environment.NewLine, packReport));
        foreach (var selected in new[] { runtime, aspNet }.Where(b => b != null))
        {
            foreach (var package in selected.Packages.Where(p => p.Role is "targeting-pack" or "apphost-pack"))
            {
                var matches = packReport.Where(line => package.Role == "apphost-pack" ? line.StartsWith("host|") :
                    line.Contains("|" + package.Id + "|", StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.True(matches.Length == 1, package.Id + Environment.NewLine + string.Join(Environment.NewLine, packReport));
                var report = matches[0];
                if (package.Role != "apphost-pack")
                {
                    Assert.Contains("|" + package.Version + "|", report);
                }
                Assert.Contains(Path.Combine(workspace.Packs, package.Id, package.Version), report, StringComparison.OrdinalIgnoreCase);
            }
        }
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "Smoke.deps.json")));
        if (readyToRun)
        {
            using var binary = File.OpenRead(Path.Combine(published, "Smoke.dll"));
            using var pe = new PEReader(binary);
            Assert.True(pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size > 0);
            Assert.Contains(packReport, line => line.StartsWith("crossgen2|") &&
                line.Contains(Path.Combine(workspace.Packs, "Microsoft.NETCore.App.Crossgen2." + fixture.Rid, version), StringComparison.OrdinalIgnoreCase));
        }
        if (selfContained)
        {
            Assert.Contains(deps.RootElement.GetProperty("libraries").EnumerateObject(), property => property.Name.Contains("Microsoft.NETCore.App.Runtime." + fixture.Rid + "/" + version, StringComparison.OrdinalIgnoreCase));
            if (runtime != null)
            {
                Assert.Equal(File.ReadAllBytes(Path.Combine(runtime.RoleDirectory("runtime-pack"), "runtimes", fixture.Rid, "lib", tfm, "System.Private.CoreLib.dll")),
                    File.ReadAllBytes(Path.Combine(published, "System.Private.CoreLib.dll")));
            }
            if (aspNet != null)
            {
                Assert.Equal(File.ReadAllBytes(Path.Combine(aspNet.RoleDirectory("runtime-pack"), "runtimes", fixture.Rid, "lib", tfm, "Microsoft.AspNetCore.Hosting.Abstractions.dll")),
                    File.ReadAllBytes(Path.Combine(published, "Microsoft.AspNetCore.Hosting.Abstractions.dll")));
            }
            var execution = await Run(new ProcessStartInfo(Path.Combine(published, OperatingSystem.IsWindows() ? "Smoke.exe" : "Smoke")), app);
            Assert.All(execution.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith(published, line));
        }
        else
        {
            var home = fixture.Client.CreateDotnetHome(sdkHome, version, version, runtime, aspNet);
            var host = Path.Combine(home, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var start = new ProcessStartInfo(host, $"\"{Path.Combine(published, "Smoke.dll")}\"");
            start.Environment["DOTNET_ROOT"] = home;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            var execution = await Run(start, app);
            Assert.Contains(Path.Combine(home, "shared", "Microsoft.NETCore.App", version), execution);
            Assert.Contains(Path.Combine(home, "shared", "Microsoft.AspNetCore.App", version), execution);
        }
    }

    [Theory]
    [InlineData("PublishAot=true", "does not support NativeAOT")]
    [InlineData("PublishTrimmed=true", "does not support NativeAOT")]
    [InlineData("PublishSingleFile=true", "does not support NativeAOT")]
    [InlineData("RuntimeIdentifier=linux-arm64", "cross-RID publishing")]
    public async Task RealSdkRejectsUnsupportedModes(string property, string message)
    {
        var sdkHome = Environment.GetEnvironmentVariable("CRANK_BCS_SDK_HOME");
        if (string.IsNullOrEmpty(sdkHome))
        {
            return;
        }
        using var fixture = new BuildCacheTestFixture();
        fixture.PrepareBuild("runtime", BuildCacheTestFixture.RuntimeSha, tfm: "net10.0");
        var runtime = await BuildCacheClientTests.Prepare(fixture, "runtime", BuildCacheTestFixture.RuntimeSha);
        File.WriteAllText(Path.Combine(fixture.Root, "Smoke.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var workspace = BuildCachePublish.CreateWorkspace(fixture.Root, "net10.0", fixture.Rid, runtime, null);
        var start = new ProcessStartInfo(Path.Combine(sdkHome, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            $"msbuild Smoke.csproj -target:ProcessFrameworkReferences -p:{property} " + workspace.Arguments);
        workspace.SetEnvironment(start.Environment);
        await Run(start, fixture.Root, message);
    }

    private async Task<string> Run(ProcessStartInfo start, string directory, string expectedError = null)
    {
        start.WorkingDirectory = directory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var text = await stdout;
        var error = await stderr;
        output.WriteLine(start.FileName + " " + start.Arguments + Environment.NewLine + text + error);
        Assert.True(expectedError == null ? process.ExitCode == 0 : process.ExitCode != 0, text + error);
        if (expectedError != null)
        {
            Assert.Contains(expectedError, text + error);
        }
        return text;
    }
}
