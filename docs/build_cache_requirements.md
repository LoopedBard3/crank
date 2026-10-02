# BCS installer feed contract

Crank resolves a commit and delegates complete framework installation to unchanged
`dotnet-install.ps1` / `dotnet-install.sh`. It does not extract raw build output,
overlay feed assemblies, rename framework versions, or synthesize framework metadata.
The producer must publish the installer layout below; raw-artifact-only builds fail.

## Resolution

An empty component selector inherits the job channel. `ci` selects the latest BCS
build on `main`; a full 40-hex SHA pins a commit. Explicit `current`, `latest`, `edge`,
and concrete versions select ordinary feeds, even on the `ci` channel.

Latest commits use the existing public index:

```text
builds/{repo}/latest/main/latestBuilds.json
```

The platform configuration's `CommitSha` (or `commit_sha`) is used, falling back to
`all` only when the configuration entry is absent. Returned commits must be full
SHAs. Pins are normalized to lowercase and never require a latest-index request.

The installer AzureFeed root is:

```text
builds/{repo}/buildArtifacts/{full-lowercase-sha}/{config}/install
```

Runtime configurations use `coreclr_{arch}_{os}`; ASP.NET uses
`aspnetcore_{arch}_{os}`. Supported configurations are Linux x64/arm64 and Windows
x64/arm64/x86. The agent uses its existing
platform detection. Unsupported selected components fail rather than switching repos.

## Required files relative to AzureFeed

```text
Runtime/main/latest.version
Runtime/{V}/dotnet-runtime-{V}-{RID}.tar.gz
Runtime/{V}/dotnet-runtime-{V}-{RID}.zip

aspnetcore/Runtime/main/latest.version
aspnetcore/Runtime/{V}/aspnetcore-runtime-{V}-{RID}.tar.gz
aspnetcore/Runtime/{V}/aspnetcore-runtime-{V}-{RID}.zip
```

Each repository publishes its own product paths. Version markers contain either
`V` alone or `fullSHA` followed by `V` on the next line. Crank uses the exact original
product version, including prerelease text, as installer `Version`. A two-line marker's
SHA must match the resolved commit. Publish the marker **last**, after all payloads.

Publish original Linux tarballs and **both original Windows ZIP and tar.gz archives**.
Current PowerShell installers use tar.gz for major version 11 onward and ZIP for older
versions; the shell installer uses tar.gz. Do not rename a ZIP to tar.gz, repack,
or change the stock installer to fit a payload. Stock product-version probes may return
404; the exact-version installer path then uses `V`.

Archives must contain the canonical `shared/{framework}/{V}` tree, including its original
`.version`, `.deps.json`, and `.runtimeconfig.json`. The runtime archive also supplies its
original `dotnet` host and `host/fxr`. Crank checks the installed framework's `.version`
commit against the selected full SHA.

**ASP.NET archives bundle base-runtime and host files.** Crank installs ASP.NET into a
fresh staging home and moves only the complete `shared/Microsoft.AspNetCore.App`
directory into the job's runtime home. Bundled base-runtime files are never promoted.
This prevents a same-version ASP.NET dependency runtime from replacing the explicitly
selected runtime or causing dotnet-install to skip it.

## Optional self-contained payloads

Ordinary self-contained publishing additionally requires each selected BCS component's
original runtime nupkg, preserving its package filename and version:

```text
Runtime/{V}/Microsoft.NETCore.App.Runtime.{RID}.{V}.nupkg
aspnetcore/Runtime/{V}/Microsoft.AspNetCore.App.Runtime.{RID}.{V}.nupkg
```

Missing selected packs fail without feed fallback. A small SDK `PackageDownload`
restore from a local-only source populates a **job-private NuGet package cache** before
the application's ordinary publish. This prevents same-ID/version packages from another
commit or global cache from winning. The application's NuGet hierarchy, credentials,
and package-source mapping are not rewritten. Only selected BCS packs are preloaded;
feed-selected components and application dependencies use normal restore sources.

The SDK's existing per-framework `RuntimeFrameworkVersion` metadata selects those
runtime packs during publish, so the application's dependency manifest describes the
actual runtime assets. There is no post-publish overlay. Reference packs, compiler,
and apphost remain SDK/build-baseline inputs; this does not provide new reference APIs
or guarantee compatibility with arbitrary SDK/TFM/runtime combinations.
