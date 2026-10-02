## Description

This guide describes how to benchmark the same application on different .NET versions. It's using the same services as in [Getting Started](getting_started.md).

## Switching TFMs

TFMs ([Target Framework Moniker](https://docs.microsoft.com/en-us/dotnet/standard/frameworks)) in a .NET project allow for multi-target deployment of an app.

By default **crank** will deploy a .NET application using the first TFM that is specified in the project file.

The **hello** sample application targets both `netcoreapp3.1` and `netcoreapp5.0`.

```xml
<TargetFrameworks>netcoreapp3.1;netcoreapp5.0</TargetFrameworks>
```

When running this command line, the TFM `netcoreapp3.1` is used.

```
> crank --config /crank/samples/hello/hello.benchmarks.yml --scenario hello --profile local
```

To force the application to use `netcoreapp5.0` instead, use the `framework` property of the job. This can be set directly in the `.yml` configuration file, or using a command line argument like this:

```
> crank --config /crank/samples/hello/hello.benchmarks.yml --scenario hello --profile local --application.framework netcoreapp5.0
```

The argument makes use of the name of the service as a benchmark can depend on multiple services. 

## Verifying which versions were used

When **crank** deploys each service on an agent, it output a url that can be used to query a JSON representation of its state.

```
[10:54:53.142] Starting job 'application' ...
[10:54:53.199] Fetching job: http://localhost:5010/jobs/3
```

In this example the resulting document would contain these properties with the `netcoreapp3.1` TFM:

```json
{
    "aspNetCoreVersion": "3.1.8",
    "runtimeVersion": "3.1.8",
    "sdkVersion": "3.1.402"
}
```

## Switching framework versions

When a TFM is configured, the agent will download the corresponding .NET SDK version and use the latest public shared runtimes to run the application.

**crank** is also able to use any version of a .NET runtime using the notion of **channels**. The values can be:
- `current`: only latest public versions, this is the default
- `latest`: latest versions used by ASP.NET 
- `edge`: latest nightly builds available
- `ci`: base runtime + ASP.NET Core from the Build Cache Service, resolved per-commit (see below)

The difference between `latest` and `edge` is that `latest` will pick runtimes and SDKs that are deemed compatible together. For instance a very recent .NET core runtime might be compatible with a less recent ASP.NET runtime. The `edge` is used to pick the absolute latest build for the select TFM.

The `ci` channel uses the Build Cache Service (BCS) to resolve complete framework installers by individual commit SHA rather than VMR feed version. Empty `runtimeVersion` and `aspNetCoreVersion` selectors inherit this channel; either component can independently use a full commit SHA, `ci`, or a normal feed selector. SDK and desktop versions default to `latest` on this channel and can be set separately.

In order to benchmark and ASP.NET application using very recent runtimes of .NET 5, the `latest` channel is recommended:

```
> crank --config /crank/samples/hello/hello.benchmarks.yml --scenario hello --profile local --application.framework netcoreapp5.0 --application.channel latest
```

The following values are gathered with the **current** channel. They represent runtimes and SDKs that are available as public preview releases usually published on NuGet.org. 

```json
{
    "aspNetCoreVersion": "5.0.0-preview.4.20257.10",
    "runtimeVersion": "5.0.0-preview.4.20251.6",
    "sdkVersion": "5.0.100-preview.4.20258.7"
}
```

When using the **latest** channel we enlist for nightly build versions which vary much more frequently. However the .NET Core runtime and SDK versions might represent the very latest build available, only the ones that ASP.NET is currently using. 

```json
{
    "aspNetCoreVersion": "5.0.0-preview.6.20279.12",
    "runtimeVersion": "5.0.0-preview.6.20278.9",
    "sdkVersion": "5.0.100-preview.6.20266.3"
}
```

Finally, with the **edge** channel, all versions represent the latest available continuous builds.

```json
{
    "aspNetCoreVersion": "5.0.0-preview.6.20279.12",
    "runtimeVersion": "5.0.0-preview.6.20301.4",
    "sdkVersion": "5.0.100-preview.6.20301.7"
}
```

## Specifying different channels

Channels can be set individually on each component including
- ASP.NET runtime with `aspNetCoreVersion`
- .NET Core runtime (CLR) with `runtimeVersion`
- SDK with `sdkVersion`

The following example uses the default channel for ASP.NET but forces to use the most recent runtime.

```
> crank --config /crank/samples/hello/hello.benchmarks.yml --scenario hello --profile local --application.framework netcoreapp5.0 --application.runtimeVersion edge
```

## Specifying specific versions

Using channels provides a way to always be using recent versions. However when comparing benchmarks we might need to used fixed version numbers to be sure no external changes might be responsible for a variation. For instance when checking for a CLR improvement it's recommended to set a fixed ASP.NET version across runs. Specific versions can be used together with channels.

The following command uses the `edge` channel but ASP.NET is fixed so it doesn't vary over time.

```
> crank --config /crank/samples/hello/hello.benchmarks.yml --scenario hello --profile local --application.framework netcoreapp5.0 --application.channel edge --application.aspnetCoreVersion 5.0.0-preview.6.20279.12
```

## Using the ci channel

The `ci` channel resolves pre-built binaries for individual commits from the Build Cache Service (BCS). This is useful for performance regression bisection where VMR feed gaps make it hard to pinpoint which commit caused a regression.

Each framework resolves independently from its own repository:

- **Base runtime** (`Microsoft.NETCore.App`) uses a [dotnet/runtime](https://github.com/dotnet/runtime) commit's complete installer archive, including its matching host and original metadata.
- **ASP.NET Core** (`Microsoft.AspNetCore.App`) uses a [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) commit's complete installer archive. Its bundled base runtime is isolated and discarded; only the complete ASP.NET shared-framework directory is promoted.

For either `runtimeVersion` or `aspNetCoreVersion`:

| Selector | Meaning |
|----------|---------|
| Empty | Inherit the job channel |
| `ci` | Latest cached installer build on `main` |
| Full 40-hex SHA | Exact BCS commit, normalized to lowercase |
| `current`, `latest`, `edge`, or concrete version | Normal feeds, even when the job channel is `ci` |

Short SHAs are not accepted. A commit without ready installer artifacts fails rather
than falling back to feed or raw-overlay binaries.

### Compilation and execution are separate

Framework-dependent jobs build using the selected SDK and its reference packs.
For a BCS component, compile-time framework-version properties resolve from the
ordinary job channel (`latest` when the channel is `ci`), never from the execution
commit's product version. `framework` or the project TFM remains the compilation
target: even a numeric-leading SHA cannot select or retarget it.

The SDK installs through the existing global flow. Complete execution frameworks
install through stock `dotnet-install` into a fresh, private job home. The published
runtimeconfig selects their actual versions with roll-forward disabled. Framework
dependency metadata and version directories are never synthesized or renamed.

For controlled comparisons, choose an explicit `sdkVersion` and TFM; this is
recommended, not mandatory. Configure the application's ordinary NuGet sources for
its dependencies and any feed-selected framework packs. CI jobs preserve the
application's NuGet hierarchy and package-source mapping.

### Examples

Both frameworks from the latest ready BCS builds:

```
crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.channel ci
```

Pin runtime while explicitly retaining ASP.NET from feeds:

```
crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.channel ci --application.runtimeVersion 1111aaaa2222bbbb3333cccc4444dddd5555eeee --application.aspNetCoreVersion latest
```

Select only ASP.NET from BCS while keeping runtime/SDK on the normal channel:

```
crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.aspNetCoreVersion ci
```

### Results and current scope

`runtimeVersion` and `aspNetCoreVersion` report plain, actual installed versions.
`runtimeCommitSha` / `aspNetCoreCommitSha` retain full resolved BCS commits;
`requestedRuntimeVersion` / `requestedAspNetCoreVersion` retain the selectors.
Version measurements use `{actualVersion}+{fullSHA}` for BCS components, preserving
the existing version/commit result schema. CI provenance stays in the separate fields.
`sdkVersion` and `buildFramework` identify the compilation baseline.

Ordinary self-contained jobs are supported when the selected commits also publish
their original runtime nupkgs and Crank performs the project publish (without an
`executable` override). A local-only SDK `PackageDownload` restore populates
a job-private NuGet cache before normal publishing, isolating same-version packages
from different commits. The SDK selects runtime versions independently per framework;
reference packs, compiler, and apphost remain SDK-baseline inputs. There is no
post-publish overlay. A missing selected BCS pack fails without fallback.
Active `useMonoRuntime` modes are rejected for CI selections because they would replace
the selected CoreCLR bits after publishing.

### Reusing a CI application build

`reuseBuild` / `noBuild` can reuse a completed **project publish** under the existing
`buildKey` when both the requested build settings and the resolved inputs still match.
No controller build-key changes are needed: explicit commit selectors already
participate in the key. The agent resolves floating `ci` selectors once per component
on every run, as well as the current SDK and compile-feed versions. Matching product
versions alone are not sufficient; a different full commit or BCS feed URL is a miss.

The agent atomically writes `.ci-build.json` beside the cached build only after the
whole build succeeds, including runtimeconfig changes, output attachments, and
dependency reporting. This versioned completion record includes resolved commits,
framework versions, source identities, SDK/TFM/RID/publish mode, compile-feed versions,
and reusable result metadata. The existing `_options` request guard alone is not
proof that a publish succeeded.

| Cache hit | Work performed |
|-----------|----------------|
| Framework-dependent | Install the same selected frameworks into a **new private runtime home**; run the cached application without SDK installation, restore, compile, publish, output-attachment copying, or runtimeconfig rewriting |
| Self-contained | Run the cached output unchanged, without framework installation, runtime-pack bootstrap, restore, or publish |

Both paths restore full commit/version and SDK measurements and dependency results,
but do not report the previous build duration as a new build. Missing, malformed,
or old-schema completion records, changed resolved inputs, or missing expected
assembly/deps/runtimeconfig (and SCD apphost) cause a **normal rebuild**, not a permanent
reuse error. The previous record is invalidated before rebuilding. Resolution or
installation failures fail the job; cached old selections are never a fallback.
Without `noBuild`, the normal fresh-build behavior remains.

This is exact-selection reuse, not reuse of one compilation across different runtime
commits: any resolved commit change conservatively rebuilds FDD and SCD. Ordinary
source caching is unchanged. Custom executable jobs do not get a completion record;
the existing rejection of SCD executable overrides still applies.

Jobs sharing a build directory are serialized for its entire lifetime, including
execution and shutdown. Contending jobs are deferred before initialization without
blocking the owner's stop/delete processing. `noClean` can retain files without
retaining ownership once the owner is confirmed stopped. If writer or process
termination cannot be confirmed, the agent retains ownership and logs the path
rather than allowing another job to overwrite it; that key remains unavailable.
Completed faulted or canceled builds do not retain the key once stopping and process
exit are confirmed, so a failed resolution can be retried under the same key.
No persistent framework-home cache, eviction budget, or latest-resolution scheduler
is introduced. See [the producer contract](build_cache_requirements.md) for payload
paths and supported platforms.
If build completion or process termination cannot be confirmed, cleanup retains the
private CI home and logs its path rather than deleting files still in use.

### Agent configuration

The agent supports these command-line options for BCS:

| Option | Default | Description |
|--------|---------|-------------|
| `--build-cache-base-url` | `https://pvscmdupload.z22.web.core.windows.net` | Base URL for BCS blob storage. |
| `--build-cache-disabled` | (not set) | Disables BCS integration on this agent. |
