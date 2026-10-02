// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Microsoft.Crank.Models;
using Newtonsoft.Json;

namespace Microsoft.Crank.Agent
{
    internal sealed class CiBuildRecord
    {
        internal const string FileName = ".ci-build.json";
        private static readonly string[] ResultNames =
        [
            Measurements.BenchmarksNetSdkVersion, Measurements.BenchmarksNetCoreAppVersion,
            Measurements.BenchmarksAspNetCoreVersion, Measurements.BenchmarksPublishedSize,
            Measurements.BenchmarksSymbolsSize, Measurements.BenchmarksPublishedNativeAOTSizeRaw
        ];

        internal sealed record Inputs(
            BuildCacheClient.ResolvedBuild RuntimeBuild, BuildCacheClient.ResolvedBuild AspNetCoreBuild,
            string RuntimeVersion, string AspNetCoreVersion, string SdkVersion, string Framework,
            string Rid, bool SelfContained, string BuildRuntimeVersion, string BuildAspNetCoreVersion,
            string DesktopVersion, string AssemblyName);

        public int SchemaVersion { get; set; }
        public Inputs Selection { get; set; }
        public string DesktopVersion { get; set; }
        public long PublishedSize { get; set; }
        public Measurement[] Results { get; set; }
        public MeasurementMetadata[] Metadata { get; set; }
        public Dependency[] Dependencies { get; set; }

        internal static bool OutputsExist(string output, Inputs selection) =>
            new[] { $"{selection.AssemblyName}.dll", $"{selection.AssemblyName}.deps.json", $"{selection.AssemblyName}.runtimeconfig.json" }
                .All(file => File.Exists(Path.Combine(output, file))) &&
            (!selection.SelfContained || File.Exists(Path.Combine(output,
                selection.AssemblyName + (selection.Rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""))));

        internal static CiBuildRecord Read(string path, string output, Inputs selection)
        {
            try
            {
                var record = JsonConvert.DeserializeObject<CiBuildRecord>(File.ReadAllText(Path.Combine(path, FileName)));
                if (record?.SchemaVersion == 1 && record.Selection == selection &&
                    record.Results != null && record.Metadata != null && record.Dependencies != null &&
                    record.Results.All(result => result?.Name != null && result.Value != null) &&
                    record.Metadata.All(metadata => metadata?.Name != null) &&
                    record.Dependencies.All(dependency => dependency != null) &&
                    ResultNames.Take(3).All(name => record.Results.Any(result => result?.Name == name && result.Value != null) &&
                        record.Metadata.Any(metadata => metadata?.Name == name)) &&
                    OutputsExist(output, selection))
                {
                    return record;
                }
                Log.Info("CI build cache miss: completion record, resolved inputs, or published output do not match.");
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Info($"CI build cache miss: {ex.Message}");
            }
            return null;
        }

        internal static void Invalidate(string path) => File.Delete(Path.Combine(path, FileName));

        internal static void Write(string path, string output, Inputs selection, Job job)
        {
            if (!OutputsExist(output, selection))
            {
                Log.Info("Not caching CI build: expected assembly, dependency manifest, runtimeconfig, or apphost is missing.");
                return;
            }

            var record = new CiBuildRecord
            {
                SchemaVersion = 1, Selection = selection, DesktopVersion = job.DesktopVersion,
                PublishedSize = job.PublishedSize, Dependencies = job.Dependencies.ToArray(),
                Results = job.Measurements.Where(m => ResultNames.Contains(m.Name)).ToArray(),
                Metadata = job.Metadata.Where(m => ResultNames.Contains(m.Name)).ToArray()
            };
            var temporary = Path.Combine(path, FileName + "." + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(record));
                File.Move(temporary, Path.Combine(path, FileName), overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        internal void Restore(Job job)
        {
            job.RuntimeVersion = Selection.RuntimeVersion;
            job.AspNetCoreVersion = Selection.AspNetCoreVersion;
            job.RuntimeCommitSha = Selection.RuntimeBuild?.CommitSha;
            job.AspNetCoreCommitSha = Selection.AspNetCoreBuild?.CommitSha;
            job.SdkVersion = Selection.SdkVersion;
            job.BuildFramework = Selection.Framework;
            job.DesktopVersion = DesktopVersion;
            job.PublishedSize = PublishedSize;
            job.BuildTime = TimeSpan.Zero;
            job.Dependencies.Clear();
            job.Dependencies.AddRange(Dependencies);
            foreach (var metadata in Metadata.Where(m => ResultNames.Contains(m.Name)))
                if (!job.Metadata.Any(m => m.Name == metadata.Name))
                    job.Metadata.Enqueue(metadata);
            foreach (var result in Results.Where(m => ResultNames.Contains(m.Name)))
                if (!job.Measurements.Any(m => m.Name == result.Name))
                    job.Measurements.Enqueue(new Measurement { Name = result.Name, Value = result.Value, Timestamp = DateTime.UtcNow });
        }
    }
}
