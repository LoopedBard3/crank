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

The `ci` channel defaults each framework to the latest complete Build Cache Service (BCS) publication on `main`. `runtimeVersion` and `aspNetCoreVersion` can independently select BCS or a feed, regardless of the job channel. SDK and desktop defaults remain feed `latest` under `channel: ci`; an explicit SDK version remains independent.

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

Each framework selector is independent:

| Selector | Meaning under any channel |
|---|---|
| empty | Inherit `job.channel` |
| `ci` | Latest complete BCS build for that repository on `main` |
| full 40-hex SHA | Pin that repository's BCS build; uppercase is normalized to lowercase |
| `current`, `latest`, `edge`, concrete feed version | Use the corresponding feed selection |

Explicit `latest` always means **feed latest**, even under `channel: ci`. Short SHAs are not BCS pins. Only selected BCS repositories are queried.

The explicit `framework` property wins; otherwise the existing project TFM/legacy feed-prefix detection applies. A commit's first character is irrelevant. A selected pack whose actual TFM differs from the project fails with a clear error rather than silently retargeting the project. For a project without a discoverable TFM, set `framework` explicitly if the default does not match.

### Basic usage (latest cached build of both frameworks on main)

```
> crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.channel ci
```

### Bisecting ASP.NET Core (pin aspnetcore, runtime stays latest)

```
> crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.channel ci --application.aspNetCoreVersion 1111aaaa2222bbbb3333cccc4444dddd5555eeee
```

### Bisecting the base runtime (pin runtime, aspnetcore stays latest)

```
> crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.channel ci --application.runtimeVersion 1111aaaa2222bbbb3333cccc4444dddd5555eeee
```

### BCS runtime with feed ASP.NET

```
> crank --config benchmarks.yml --scenario json --profile aspnet-perf-lin --application.runtimeVersion ci --application.aspNetCoreVersion latest
```

Feed runtime with BCS ASP.NET is also supported: use `runtimeVersion: latest` and `aspNetCoreVersion: ci`. Use actual full commit SHAs instead of `ci` for reproducible selections.

### Execution, compatibility and reuse

Framework-dependent jobs receive an empty private runtime home. The selected BCS runtime supplies the **complete canonical runtime distribution**: real muxer, hostfxr, shared framework version and original metadata. No feed runtime is cloned beneath it. The canonical distribution may contain ReadyToRun-compiled framework assemblies; this is intentionally different from benchmarking raw IL build-output overlays. ASP.NET comes from its complete selected runtime pack or selected feed installation, preserving its real version and original requirements.

Self-contained jobs restore and publish against actual selected runtime, reference and apphost packs, with matching crossgen2 for supported same-host ReadyToRun. Per-job pack roots, local feeds and NuGet caches isolate reused package versions across commits. The SDK generates application deps; no post-publish binary overlay is used. NativeAOT, trimming, single-file, Mono, additional shared frameworks and cross-RID/cross-host publishing fail explicitly.

Framework dependencies must be satisfiable under their original declared host policy. Equal package versions/TFMs do not guarantee compatibility between arbitrary commits; mismatches fail instead of lowering requirements or pretending a BCS build is a feed version. The agent requires a [complete transport ZIP](build_cache_requirements.md) containing the original canonical distribution and packages. It derives versions, TFM and requirements from their existing metadata; no server manifest is required. Missing/old raw-only artifacts do not fall back to feeds.

Build reuse requires a valid `.bcs-build-meta.json` and identical resolved immutable selections, publish mode, RID and TFM. Changed selectors or advancing BCS `ci` results require an explicit rebuild, even for FDD, so the application is never run against stale compile/publish inputs. Same selections preserve reuse; FDD materializes a new private runtime home. A missing selected feed baseline also fails rather than substituting another version.

Requested selectors are retained separately from actual `runtimeVersion`/`aspNetCoreVersion` and immutable BCS identities. Fresh and reused results include full framework dependency SHAs and measurements in `{actualVersion}+{fullSHA}` format; RegressionBot also accepts historical `+ci.sha` measurements.

Only immutable verified downloads are shared. Job-owned extracts, publish caches and homes are removed after build/benchmark processes stop unless `noClean` is set or cleanup is disabled. RID/configuration is derived per repository from the agent platform; unsupported mappings fail explicitly. No old shared-home cache is reused.

### Agent configuration

The agent supports these command-line options for BCS:

| Option | Default | Description |
|--------|---------|-------------|
| `--build-cache-base-url` | `https://pvscmdupload.z22.web.core.windows.net` | Base URL for BCS blob storage. |
| `--build-cache-disabled` | (not set) | Disables BCS integration on this agent. |
