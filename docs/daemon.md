# Workspace Daemon

This document is the canonical design contract for RoslynKit's optional workspace daemon. The hidden host, short-lived client, on-demand startup, public lifecycle controls, and exact infrastructure-only standalone fallback are implemented.

## Purpose

Loading an `MSBuildWorkspace` reevaluates the target solution or project. Repeating that work in every short-lived CLI process dominates many read-only searches. The daemon keeps one loaded workspace and its current immutable `Solution` snapshot alive across compatible CLI invocations.

The daemon is a transparent performance optimization, not a correctness authority or a new user-facing server product. The fallback boundary preserves correctness by executing eligible read-only commands through the existing standalone path when daemon infrastructure is unavailable.

```mermaid
flowchart LR
    CLI[Short-lived RoslynKit CLI] --> Parse[Parse locally]
    Parse --> Local{Workspace command?}
    Local -->|No| ExecuteLocal[help / version / init / daemon control]
    Local -->|Yes| Pipe[Connect or auto-start daemon]
    Pipe --> Daemon[Long-lived same-user daemon]
    Daemon --> Fingerprint[Git fingerprint]
    Fingerprint --> Session[Workspace session]
    Session --> Executor[Roslyn command executor]
    Executor --> Response[Buffered stdout / stderr / exit code]
    Pipe -.->|Daemon infrastructure failure| Fallback[Standalone execution]
```

## Lifecycle and identity

- The first workspace-backed command connects to a compatible daemon or starts the same RoslynKit executable in the hidden internal server mode. There is no public `daemon start` command.
- `help`, `version`, `init`, `daemon status`, and `daemon stop` execute locally. Status and stop never start a daemon.
- A compatibility identity includes the current user, canonical Git worktree and target, exact protocol and RoslynKit code/build identity, process architecture, resolved .NET SDK and MSBuild identity, applicable `global.json`, agreed build environment values, and the runtime directory used by local IPC.
- The endpoint is a fixed-length opaque hash of the canonical identity. Repository paths never appear in endpoint names.
- A short-lived `DaemonBootstrapLease` prevents concurrent clients from racing to start a server. After acquiring it, the client rechecks connectivity before spawning and holds it only until the versioned handshake reports readiness. The separate `DaemonLifetimeLease` enforces one live server for the identity. Both use cross-process mutexes held by dedicated owner threads; Windows uses the global mutex namespace so desktop sessions for the same identity cannot create separate owners.
- Readiness requires a versioned protocol handshake. The existence of a pipe or socket is not sufficient.
- The daemon exits after five minutes with no active or queued requests. Status requests do not reset this timer.
- Stop rejects new work, drains active requests, and cancels remaining work after 30 seconds.
- Toolchain or identity changes select another daemon. An obsolete daemon exits through its normal idle lifecycle.
- The daemon writes no persistent log. Status may expose a bounded in-memory diagnostic summary.

### Workspace identity resolution

`GitWorkspaceIdentityResolver` implements the workspace and toolchain portion of the compatibility identity without loading an `MSBuildWorkspace`. Both the daemon client and hidden server resolve it before deriving the endpoint. Resolution currently:

1. Converts an existing `.sln`, `.slnx`, or `.csproj` target to an absolute path and resolves existing symbolic-link or reparse-point components.
2. Resolves the worktree with `git rev-parse --path-format=absolute --show-toplevel` and requires `git rev-parse --verify HEAD^{commit}` to succeed.
3. Rejects a target worktree that is a submodule, a repository nested under another worktree, or a repository containing configured submodules.
4. Requires the resolved root to occur exactly once in `git worktree list --porcelain -z`. Other linked worktrees for the same repository remain compatible because each target still belongs to exactly one worktree.
5. Captures the nearest `global.json` found by walking from the target directory to the filesystem root as its canonical path plus a SHA-256 content digest.
6. Captures `dotnet --version` from the target directory and queries `MSBuildLocator` with that directory to record the selected instance, discovery type, instance version, MSBuild path, and `MSBuild.dll` product version.
7. Captures the RoslynKit informational version, module version ID, daemon protocol version, and process architecture.

The explicit build-environment allowlist is `Configuration`, `Platform`, `DOTNET_CLI_HOME`, `DOTNET_HOST_PATH`, `DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR`, `DOTNET_ROLL_FORWARD`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE`, `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_ROOT_X86`, `MSBUILD_EXE_PATH`, `MSBuildExtensionsPath`, `MSBuildExtensionsPath32`, `MSBuildExtensionsPath64`, `MSBuildSDKsPath`, `NUGET_FALLBACK_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PACKAGES`, `NUGET_PLUGINS_CACHE_PATH`, and `NUGET_PLUGIN_PATHS`. Unset values are retained as explicit null entries so set and unset environments cannot compare as the same identity accidentally. Other environment-dependent evaluation remains outside the supported boundary.

The committed `HEAD` is validated here but is not part of daemon compatibility identity. `HEAD` is mutable worktree state and belongs to the per-request Git fingerprint, allowing a commit to reload the existing daemon rather than selecting a different endpoint.

### Endpoint identity and naming

`DaemonIdentityResolver` adds the current operating-system user and local IPC runtime directory to a resolved `GitWorkspaceIdentity`. Windows users are identified by SID, Unix users by effective numeric UID, and the runtime directory is the canonical form of the process runtime directory returned by `Path.GetTempPath()`. The resolver snapshots the build-environment mapping so later mutation cannot change an already-created identity.

`DaemonEndpointName` serializes every compatibility field in a fixed JSON property order, sorts build-environment entries ordinally, preserves null values, and hashes the UTF-8 bytes with SHA-256. The resulting endpoint name has the fixed format `roslynkit-v1-<64 lowercase hexadecimal characters>`. It contains no repository path, target path, environment value, or user identifier. The hidden daemon runner uses this endpoint for both its lifetime lease and named-pipe listener. Client bootstrap locking uses the same endpoint, and public CLI routing reaches that client through the workspace command router.

## Supported workspace boundary

Daemon acceleration initially supports one Git worktree with a committed `HEAD` and one solution or project target inside that worktree.

The following cases execute standalone or remain unsupported for daemon acceleration:

- unborn repositories or targets outside a Git worktree;
- submodules, nested repositories, or workspaces spanning multiple repositories;
- linked or imported build inputs outside the worktree;
- arbitrary environment-dependent MSBuild evaluation not represented in the compatibility identity;
- Git-ignored files used as active build inputs;
- tracked files hidden from status by `assume-unchanged` or `skip-worktree`;
- raw worktree-byte changes that Git clean filters, line-ending normalization, LFS, `ident`, or custom filters report as clean.

These limitations mean the performance-only correctness claim applies inside this supported boundary. A filesystem watcher and incremental `Solution.WithDocumentText` updates are explicitly out of scope. Any detected fingerprint change causes a full MSBuild workspace reload.

## Search index and daemon coordination

`index` and `search` use a persistent SQLite Full-Text Search 5 (FTS5) database in addition to the daemon's in-memory workspace snapshot. Both commands require an explicit `--target` and `--index-path`. A repository-local, Git-ignored path such as `artifacts/roslynkit.db` is the intended storage boundary. One database can contain separate partitions for targets in that repository; an index path outside the repository or not ignored by Git is rejected. SQLite persists target identities, project paths, and declaration source paths relative to the repository, then reconstructs public target and declaration locations as absolute paths from the resolved repository root.

SQLite write-ahead logging (WAL) allows readers to continue while a refresh writes a new coherent target partition. While the database is active, SQLite can create adjacent `roslynkit.db-wal` and `roslynkit.db-shm` files. The index database is independent of the local IPC endpoint identity and does not make the daemon a general-purpose database server.

`search` reconciles the target before querying. It builds the initial target index when absent and refreshes stale records automatically. `index` is the strict synchronization operation: it waits for a stable workspace before reporting success, and `--rebuild` forces a full rebuild of the selected target partition. An ongoing refresh never publishes partial records. When a previous coherent partition exists, concurrent searches can use it and report `index-state: stale`; otherwise they wait for initial indexing to finish.

Search indexing supports projects with one target framework and rejects multi-targeted projects rather than selecting a framework implicitly. Every indexed project and non-generated source document must have an existing physical path inside the target's Git worktree; missing project or non-generated source paths, external projects, and external linked non-generated source files are rejected. Search covers all target projects by default, can narrow to `--project`, and skips generated source documents, including source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree. Search returns ranked declarations for agent-mediated navigation through existing `id:` and `loc:` values; it has no standard-input pipeline contract.

## Git fingerprint

Each workspace request reconciles Git state before leasing a snapshot. Concurrent requests share one in-progress fingerprint calculation. The complete operation has a five-second deadline.

`GitWorktreeFingerprintService` accepts the canonical worktree root from workspace identity resolution, coalesces only concurrent captures, and returns either a structural `GitWorktreeFingerprint` or a typed failure that forbids cache reuse. `WorkspaceDaemonSession` consumes this service before leasing or replacing a workspace generation, and the hidden daemon runner owns that session through `WorkspaceDaemonHost`.

The capture uses `ProcessStartInfo.ArgumentList` and Git-native object hashing:

```text
git -C <root> rev-parse --verify HEAD^{commit}
git -C <root> --no-optional-locks status --porcelain=v1 -z --untracked-files=all --no-renames --ignore-submodules=none
git -C <root> hash-object --no-filters -- <changed-or-untracked-paths>
```

`hash-object` is never invoked with `-w`. Paths are passed in bounded argument batches, not through a shell or newline-delimited `--stdin-paths`. Missing paths receive an explicit marker. Records and per-file blob IDs are sorted deterministically.

A stable capture is:

1. Read `HEAD` as `HEAD0`.
2. Capture porcelain bytes as `status0`.
3. Hash every changed or untracked existing path.
4. Capture porcelain bytes again as `status1`.
5. Read `HEAD` as `HEAD1`.
6. Accept only when both HEAD values and both status captures match and every expected hash result is present.

The stable capture reduces races but is not an atomic filesystem snapshot. A Git failure, timeout, malformed record, path race, or incomplete hash result is an infrastructure failure and does not authorize reuse of the cached workspace.

## Immutable snapshots and reloads

Each command leases one captured immutable Roslyn `Solution`. A reload creates a new `MSBuildWorkspace` and `Solution`; it does not mutate the snapshot held by an active command. Historical generations are released after their active leases finish.

At most three clean-snapshot searches run concurrently. Asynchronous writer-priority coordination prevents a pending reload from admitting new searches against the old generation.

`WorkspaceDaemonSession` implements the workspace ownership boundary beneath the lifecycle host. It owns the current `RoslynWorkspaceLoader`, successful fingerprint baseline, monotonically increasing generation number, active and queued request counts, workspace state, and latest Git infrastructure diagnostic. A generation dispatches through the caller-owned `RoslynCommandExecutor` overload, so command execution never reloads or disposes the leased workspace.

Reload behavior is:

1. Compute a stable pre-load fingerprint.
2. Block new searches and wait for active searches to finish.
3. Dispose the old workspace and load a fresh workspace.
4. Compute a stable post-load fingerprint.
5. Accept the new generation when the fingerprints match.
6. Otherwise require 250 milliseconds of quiet, represented by two equal stable captures; restart the quiet interval while the fingerprint continues changing, then reload once more.
7. If the retry is also unstable, keep the latest completed snapshot usable for the current request and force the next request to reconcile and reload again.

Only a usable stable load updates the successful fingerprint baseline. The second completed snapshot may serve its initiating request after a second pre/post mismatch, but its baseline remains unset and no later request can reuse it. A Git failure returns a typed infrastructure result that triggers standalone fallback. A normal workspace-load or semantic command exception propagates unchanged and is not converted into daemon fallback. Session disposal waits for active leases and prevents a generation that finishes loading after disposal begins from being published. `WorkspaceDaemonSession.CaptureSnapshot` reads state, generation, active and queued request counts, and the latest diagnostic under the session coordination lock so lifecycle status cannot combine fields from different moments.

## Host lifecycle coordination

`WorkspaceDaemonHost` implements the transport-independent lifecycle boundary around one session. `WorkspaceDaemonServer` is the implemented named-pipe adapter: it dispatches decoded requests into this host rather than owning workspace generation or shutdown policy itself.

- Every accepted command is registered by request ID before session execution. A duplicate active request ID is rejected instead of replacing the original registration.
- The command's absolute UTC deadline and the connection-lifetime cancellation token both flow into `WorkspaceDaemonSession.ExecuteAsync`. `WorkspaceDaemonServer` cancels that token when the peer disconnects; cancellation of the short-lived client closes the same request path. `Program.Main` does not yet install a process-signal handler for `Ctrl+C`.
- Command admission cancels the current idle wait. After the final command fully unwinds, a fresh five-minute wait starts through an injected `TimeProvider`. Status snapshots do not touch this activity version or timer.
- Stop changes lifecycle state before returning its acknowledgement, rejects later commands, and allows accepted commands 30 seconds to drain. At the deadline it cancels every remaining command and waits for lease unwind before disposing the session.
- Direct host disposal skips the grace period, cancels accepted commands immediately, and still waits for their session leases to unwind.
- Status reports daemon availability as `running` while the host is alive, plus the target, process ID, atomic session snapshot, and at most 4,096 characters of the latest infrastructure diagnostic.

### Hidden server process

- `Program` intercepts the exact internal daemon token before public CLI parsing. The token is neither registered nor documented as a public command, and malformed internal arguments exit without falling through to the public parser.
- `DaemonServerRunner` resolves and validates the Git workspace and complete compatibility identity, acquires the per-endpoint lifetime lease, and constructs one `WorkspaceDaemonSession`, `WorkspaceDaemonHost`, and `WorkspaceDaemonServer`. Workspace loading remains lazy until the first command. An already-held lifetime lease makes a duplicate internal launch exit without creating another listener.
- The server bounds live connections at 32. Every connection must complete a compatible handshake and may then issue exactly one command, status, or stop operation. A handshake-only connection may close cleanly after readiness is confirmed.
- Separate connections run concurrently. Command execution remains bounded by the session's three-reader limit, while idle or malformed connections cannot create an unbounded set of handlers.
- Command targets are canonicalized again and must equal the target captured by daemon identity. The process never changes its global current directory for a request.
- A clean peer disconnect, pipe failure, or unexpected byte after a command frame cancels the connection token passed into the lifecycle host. Malformed or incompatible clients close only their connection; the listener remains available.
- Stop writes its acknowledgement before canceling the accept loop, then relies on the lifecycle host to drain or cancel existing work. When `WorkspaceDaemonServer.RunAsync` receives process-lifetime cancellation, it disposes the host before awaiting connection handlers, so active Roslyn work cannot keep that shutdown path alive indefinitely. `Program.Main` does not yet install a graceful process-signal handler.
- A Git fingerprint infrastructure failure closes the command connection without publishing a partial result. The client classifies that transport loss as infrastructure, and the fallback router redirects it to standalone execution. Ordinary command exceptions are converted through the same buffered `CliProcessResult` error formatting used by standalone execution.

## Local protocol and security

- `DaemonNamedPipe` creates only local asynchronous duplex streams. Server streams use byte transmission mode and `PipeOptions.CurrentUserOnly`; client streams address the local machine and also request current-user-only access. On Unix, the .NET named-pipe implementation may use a Unix-domain socket; RoslynKit supplies the opaque endpoint name rather than choosing an explicit socket path.
- `DaemonProtocol` frames each message as a four-byte little-endian signed length followed by strict UTF-8 JSON. Request frames are limited to 1 MiB and response frames to 64 MiB. Zero, negative, and over-limit lengths are rejected before renting payload memory, and partial headers or payloads are rejected by exact-read loops.
- The JSON schema is case-sensitive and uses strict numeric tokens. It rejects comments, trailing commas, quoted numbers, unknown properties, unknown message discriminators, empty request IDs, invalid UTF-8, and malformed JSON. The closed message set covers handshake, command, status, and stop requests and responses. Every message carries the exact protocol version and request ID; the handshake layer calls `DaemonProtocol.EnsureCompatible` before dispatch.
- Command requests carry an absolute UTC deadline. `DaemonCommandRequest.Create` canonicalizes `target`, `project`, and `file` paths through the same reparse-point-aware path boundary used by workspace identity before serialization, while `ToParsedCommand` rebinds the wire options through `CliParser` before server execution. Both boundaries enforce an explicit allowlist of the current read-only workspace commands; local lifecycle commands cannot be encoded as command requests.
- Command responses carry one complete buffered `CliProcessResult`, so no process output needs to be published from a partial frame. A later aggregation layer may use bounded start/chunk/end frames if logical output must exceed the response-frame limit.
- Framing cancellation propagates through asynchronous stream operations. A peer closing between frames is distinguished from a truncated header or payload so the server can treat an idle disconnect normally. Client disconnect cancellation and workspace-lease unwind remain daemon-host responsibilities.
- The daemon server never changes global current directory per request and rejects a target that does not match its identity.
- Client cancellation or disconnect cancels queued or running work and flows through Roslyn operations. The lifecycle host waits for cancellation unwind before disposing the workspace session.

`DaemonPipeClient` opens a short-lived connection, completes the required handshake, sends one operation, verifies protocol and request correlation, and exposes only a complete buffered response. `DaemonClient` resolves the endpoint, tries the command against an existing daemon, normalizes command-path infrastructure and protocol failures, and coordinates startup when the endpoint is absent. Startup uses a five-second readiness window with short probes and one recheck when an endpoint disappears between readiness and the command exchange. `DaemonProcessStarter` invokes either the current apphost directly or `dotnet` with the current entry assembly, passes only the hidden token and canonical target, and does not wait for or kill the long-lived child. On Windows it follows Roslyn's compiler-server launch boundary: `CreateProcess` uses `CREATE_NO_WINDOW`, disables inherited handles, and supplies invalid standard handles so the daemon cannot retain or interfere with the short-lived client's terminal streams. Other platforms use the non-waiting `Process.Start` path with redirected streams. `Program` injects `DaemonFallbackWorkspaceCommandRouter` for workspace commands. That router falls back only on its typed daemon infrastructure failure, prefixes the fallback result's buffered stderr with the warning, and invokes the existing standalone path once. Public lifecycle controls use the non-starting client exchange and never use fallback.

Daemon-eligible commands are read-only. If mutating commands are introduced, request deduplication or a stricter pre-dispatch-only fallback rule is required before those commands may use the daemon.

## Standalone fallback

Fallback is limited to daemon infrastructure failures:

- unsupported daemon workspace identity;
- Git fingerprint failure or timeout;
- daemon startup, handshake, transport, or protocol failure;
- daemon crash or connection loss.

Fallback is not selected for usage errors, ordinary workspace-load errors, semantic command errors, or cancellation during the daemon attempt. Once an infrastructure failure has selected fallback, its warning remains when standalone execution later reports cancellation.

For a daemon-eligible read-only command, `DaemonFallbackWorkspaceCommandRouter` prefixes the standalone result's buffered stderr with exactly one line:

```text
warning: daemon unavailable; executing standalone
```

Normal command stdout remains unchanged. Because daemon responses are complete, a failed transport cannot mix partial daemon stdout with standalone output. Read-only execution makes a retry after an ambiguous disconnect logically safe; mutating commands require stronger at-most-once handling.

## Public lifecycle commands

The public control surface is:

```text
roslynkit daemon status --target <target>
roslynkit daemon stop --target <target>
```

Both commands are idempotent and exit successfully when no compatible daemon is running. They parse and execute from the short-lived CLI, never load a workspace there, and never start a daemon. Status reports running state, target, process ID, workspace readiness, generation, active and queued request counts, and the latest bounded infrastructure diagnostic when available. Stop reports `state: stopping` after the server acknowledges graceful shutdown; an absent compatible daemon reports `state: not-running` for either command.
