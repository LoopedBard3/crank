# Complete Build Cache Service bundles

Crank downloads one complete transport ZIP per selected repository from the existing public BCS paths:

```text
{base}/builds/{runtime|aspnetcore}/latest/main/latestBuilds.json
{base}/builds/{runtime|aspnetcore}/buildArtifacts/{full-lowercase-sha}/{configKey}/{bundleFile}
```

Only selected BCS repositories are queried. Full SHA pins bypass latest discovery. The existing latest index accepts `CommitSha`/`CommitTime` or `commit_sha`/`commit_time`, a configuration entry (or `all`), and optional `branch_name`/`BranchName`.

## Transport layout

The producer selects exactly one of each required original archive and stores those files unchanged at the outer ZIP root. There is no server manifest, schema document, checksum sidecar, or per-file object tree. Existing raw build artifacts remain alongside these bundles, but Crank does not use them as a fallback.

| Repository | Fixed transport ZIP filename | Required original files at its root |
|---|---|---|
| runtime | `RuntimeDistribution_{linux|windows}_{arch}_Release_coreclr.zip` | Canonical `dotnet-runtime-{V}-{RID}.zip` (Windows) or `.tar.gz` (Linux), `Microsoft.NETCore.App.Runtime.{RID}.{V}.nupkg`, `Microsoft.NETCore.App.Ref.{V}.nupkg`, `Microsoft.NETCore.App.Host.{RID}.{V}.nupkg` |
| aspnetcore | `FrameworkPackages_{linux|windows}_{arch}_Release_aspnetcore.zip` | `Microsoft.AspNetCore.App.Runtime.{RID}.{V}.nupkg`, `Microsoft.AspNetCore.App.Ref.{V}.nupkg` |

Runtime bundles may additionally contain `Microsoft.NETCore.App.Crossgen2.{hostRID}.{V}.nupkg`. ReadyToRun publishing requires this original package for the selected runtime and the SDK host RID.

Current mappings cover `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, and `win-x86`; configuration keys remain `coreclr_{arch}_{linux|windows}` and `aspnetcore_{arch}_{linux|windows}`.

Crank derives package ID, version and source commit from original nuspecs; the canonical runtime's `.version` must agree. GitHub repository metadata must identify the selected repository (or the official `dotnet/dotnet` monorepo), and the commit must match the requested SHA. Framework TFM and dependency policy come from the original runtimeconfig and SDK pack metadata, not the package major version. For example, `12.0.0-ci` packages can target `net11.0`.

The canonical distribution supplies its original muxer, `host/fxr/{V}` and `shared/Microsoft.NETCore.App/{V}`, including deps/runtimeconfig files. Runtime/ref packs retain their original RID/TFM directories and the SDK's `data/RuntimeList.xml`/`data/FrameworkList.xml`. Crank checks required archives and assets, source/commit/version/RID consistency, and host dependency requirements. It never manufactures package metadata, borrows a feed runtime beneath BCS, or lowers declared framework requirements.

## Publication and local cache

The publisher uploads the single complete ZIP with create-only semantics. An existing destination is an explicit failure, including reruns; it is not overwritten or compared remotely for idempotency. Advance the latest index only after the complete blob is available. Crank relies on this immutable per-commit publication policy.

Crank caches by the full source URL (including repository, SHA and configuration) and the downloaded ZIP's actual SHA256. A local hash-named ZIP detects subsequent cache corruption without introducing another storage protocol or database. Downloads use private partial files and atomic promotion. Concurrent local publishers may reuse an identical verified cache winner, but never overwrite it. There are no networked manifests or per-file downloads.

Only downloads are shared. Each job owns its outer/nested extraction directories, runtime home and NuGet workspace. Crank extracts each original archive once per preparation and rejects unsafe paths, duplicate/case-aliased entries, links and special files. Unix modes are preserved. No shared-home LRU or arbitrary default archive byte budget is introduced.

## SDK publishing

Crank selects the actual reference/runtime/apphost packs through SDK framework-reference metadata. A private pack root and exact local NuGet source mappings prevent same-version packages from other commits or feeds being substituted. The SDK generates application deps; there is no post-publish binary overlay. Resolved pack paths are checked and recorded in `obj/.../crank-bcs-packs.txt`.

NuGet configuration uses effective merged ancestor/user settings, preserving sources, mappings, disabled sources and encrypted credential representations. Relative feeds are resolved against their original configuration file. BCS preparation does not modify those configurations or log credentials.

Two properties apply **only to BCS publishes**:

* `DisableTransitiveFrameworkReferenceDownloads=true` prevents speculative downloads of unrelated known frameworks into the private pack root. Actual project-reference requirements still participate in restore.
* `RestoreEnablePackagePruning=false` avoids applying installed-SDK/feed pruning data before the private reference packs resolve. Ordinary transitive NuGet dependencies still restore.

References consuming selected .NETCoreApp packs must use the matching TFM; unrelated library targets such as `netstandard2.0` are untouched. Only `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` shared frameworks are supported. NativeAOT, trimming, single-file, Mono, cross-RID publishing and cross-host ReadyToRun fail explicitly. Same-host ReadyToRun requires matching crossgen2. SDK selection remains separate.

See [framework selection and reuse](dotnet_versions.md#using-the-ci-channel). Miniature producer ZIP fixtures validate transport and metadata handling, not executable framework contents. Opt-in real-SDK tests use unchanged official inner artifacts wrapped in these transport ZIPs and an isolated local feed; they do not demonstrate a live BCS deployment.
