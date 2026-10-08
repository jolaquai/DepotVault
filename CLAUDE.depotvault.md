# DepotVault: Implementation Plan

<!-- RESUME PROTOCOL - any agent opening this file must follow this section before touching code. -->

## Resume protocol

You are resuming work described by this file. This file is the single source of truth for progress.

1. Read this entire file, then read every file listed in **Key files** and in the current step.
2. Run `git status` and `git log --oneline -5`. The working tree should be clean and `HEAD` should match the commit recorded in **Status**. If it does, this file is up to date; trust it and continue at the current step without re-auditing.
3. If the tree is dirty or `HEAD` does not match, reconcile first: figure out what happened, fix this file, commit the fix, then continue.
4. Work the first step that is not `[x]`. One step at a time.
5. **Every step ends with exactly one commit that contains both the code change and the update to this file.** They are never committed separately. This is what makes the file trustworthy.
6. Commit messages: one terse line, imperative, lowercase, no body, no trailing period. Example: `add token cache to auth handler`. No attribution lines or trailers.
7. `git commit` only. **Never** `git push`, `git commit --amend`, `git rebase`, or `git reset --hard` unless explicitly told to. Stage by naming each path explicitly; never `git add -A`, `.`, `-u` or globs.
8. Never mark a step `[x]` before its **Verify** command has actually run and passed. If it fails, the step stays `[~]` and the failure goes in **Deviations**.
9. If reality diverges from the plan (a step is wrong, impossible, or unnecessary), amend the steps here and log it in **Deviations** in the same commit. Never silently deviate.
10. If a turn ends mid-step, the step stays `[~]` with a `Progress:` line describing exactly where it stopped and what is left. Commit whatever is coherent; if nothing is coherent, still update `Progress:` and commit only this file.
11. Do not ask for a plan-freshness check. Assume it is fresh unless step 2 says otherwise.
12. Code conventions (user's global rules): latest .NET/C#, `<Nullable>disable</Nullable>` with no nullable annotations, CRLF line endings (LF for `*.sh`, Dockerfiles, git hooks), terse one-line comments only for grave deviations, no en/em dashes in any text, xUnit.v3 on Microsoft.Testing.Platform + NSubstitute (never Moq), Span/stackalloc/`CollectionsMarshal` welcome, no hardware intrinsics. After code changes run `dotnet build` on the solution.

Step states: `[ ]` not started, `[~]` in progress, `[x]` done, `[!]` blocked, `[-]` dropped.

## Status

- **State:** in-progress
- **Current step:** 13 - Content index
- **Branch:** main
- **Base commit:** a0fe4d9
- **Last synced commit:** 832f61b (parent of the step commit)
- **Last updated:** 2026-10-08

## Goal

Desktop app (Windows + Linux, Avalonia) that downloads specific historic Steam app versions via SteamKit2, stores them deduplicated in per-volume libraries, and switches a Steam install between stored versions via junction/link tricks. Done = user can log in, pick an owned app, import manifest IDs from a SteamDB paste, download a version, download a second version with shared files deduped, switch the Steam install between them, and revert.

## Non-goals

- No DepotDownloader process or stdout parsing.
- No database; JSON only.
- No automatic manifest-history discovery (PICS gives current manifests only).
- No handling/curation of game save data beyond the mutable-file prompt (§ decisions).
- No auto-copy fallback; copy is always opt-in.
- No libsecret on Linux in v1.

## Constraints and decisions

- **UI:** Avalonia on net11.0, compiled bindings everywhere (`x:CompileBindings="True"`, `x:DataType`). Rejected: reflection bindings.
- **MVVM:** CommunityToolkit.Mvvm (source-gen). Rejected: hand-rolled base (more boilerplate, no benefit).
- **Steam access:** SteamKit2 referenced directly. Rejected: DepotDownloader subprocess.
- **Persistence:** JSON only, System.Text.Json source-gen `JsonSerializerContext`, atomic writes (tmp + `File.Move` overwrite). Rejected: SQLite/LiteDB.
- **Manifest history:** manual SteamDB paste import + tutorial dialog. PICS used for depot metadata only.
- **Platforms:** Windows + Linux.
- **Link order (dedupe and switcher):** reflink -> hardlink -> symlink -> copy (opt-in only) -> error. Whole-folder junction (Linux: dir symlink) preferred in the switcher when the install dir holds only manifest-owned files. Reflink first because it gives zero extra space with full write isolation where the FS supports it (ReFS/Dev Drive, btrfs, XFS).
- **Copy fallback:** setting `CopyFallback = Unset | Never | Always`. If `Unset` and copy is needed: dialog with "remember my choice". Never silent.
- **Dedupe:** enabled by default. Hash-identical files (SHA-1 + size) across versions are shared.
- **Mutable files (resolved Q2):** shared mutable files (configs etc.) are not necessarily leaks; users may want settings to carry across versions. So no blanket exclusion and no forced copy. Instead: heuristically collect plausibly-mutable files per app (configs, ini/cfg/json/xml/sav, cache/save/log dirs), show a review dialog, and let the user pick per file/pattern: **Share** (link as normal, writes intentionally propagate) or **Isolate** (reflink, else copy; never hardlink). Decision stored per app in `apps/<appid>.json`. Unreviewed files are treated as normal shared files. Files marked Share are skipped by self-heal (no detach, no auto-exclusion). Rejected: global exclusion globs as default behavior; forced small-file copy regardless of `CopyFallback`.
- **Login:** user/pass + Steam Guard (email/TOTP/mobile confirm), QR, persisted refresh token.
- **Token protection:** Windows DPAPI (`ProtectedData`, CurrentUser); Linux 0600 file.
- **Library layout:** one library root per volume (suggested next to each Steam library folder) so links to the install are possible. `<root>/<appid>/<versionId>/<files>`; multiple depots merge into one version folder.
- **Progress:** `Interlocked` byte counters polled by a 250 ms UI timer. Rejected: per-chunk UI events.
- **Interop:** `LibraryImport` source-gen P/Invoke; capability probed once per volume and cached.

## Key files

- `CLAUDE.depotvault.md` - this plan.
- `.gitattributes`, `.gitignore`, `README.md` - existing repo files; do not break CRLF rules in `.gitattributes`.
- Everything else is created by the steps below. Target layout:

```
DepotVault.slnx
src/DepotVault.Core/   Steam/ Download/ Library/ Linking/ SteamInstall/ Import/ Persistence/
src/DepotVault.App/    Avalonia UI
tests/DepotVault.Tests/
```

Persistence root: `Environment.SpecialFolder.ApplicationData/DepotVault/`

| File | Content |
|---|---|
| `settings.json` | settings (§ Step 3) |
| `library.json` | library roots, apps, versions (index only) |
| `apps/<appid>.json` | depots, imported manifest history, labels, notes, mutable-file decisions, exclusions |
| `versions/<versionId>/manifest.bin` | SteamKit2 `DepotManifest` per depot |
| `versions/<versionId>/state.json` | per-file snapshot (size, mtime, link kind) |
| `queue.json` | pending/paused jobs + chunk bitmaps |
| `auth.bin` | protected refresh token + account name |

Every root JSON object carries a schema version field with a migration hook.

## Steps

### 1. Scaffold solution `[x]`

- **Files:** `DepotVault.slnx`, `src/DepotVault.Core/DepotVault.Core.csproj`, `src/DepotVault.App/DepotVault.App.csproj`, `tests/DepotVault.Tests/DepotVault.Tests.csproj`, `Directory.Build.props`, `Directory.Packages.props`
- **Do:** create solution and three projects on the latest .NET SDK/TFM (net11.0 or newest installed; log in Deviations if net11.0 is unavailable). `Directory.Build.props`: `LangVersion` latest, `Nullable` disable, `AllowUnsafeBlocks` true, `ImplicitUsings` enable, central package management. Core references SteamKit2; App references Avalonia (+Fluent theme, `Avalonia.Desktop`), CommunityToolkit.Mvvm, Core; tests use xUnit.v3 on Microsoft.Testing.Platform + NSubstitute. App has `AvaloniaUseCompiledBindingsByDefault` true. Check line endings of generated files are CRLF.
- **Verify:** `dotnet build DepotVault.slnx` and `dotnet test --solution DepotVault.slnx`
- **Commit:** `scaffold solution`

### 2. Persistence primitives `[x]`

- **Files:** `src/DepotVault.Core/Persistence/JsonContext.cs`, `AtomicJsonStore.cs`, `AppPaths.cs`, tests
- **Do:** `AppPaths` resolves the root (overridable for tests). `AtomicJsonStore<T>`: serialize UTF-8 via source-gen context, write `*.tmp`, flush, `File.Move(tmp, path, overwrite: true)`; debounced save (500 ms) option; schema version + migration hook interface. Tests: round-trip, crash-leftover tmp ignored, debounce coalesces.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add atomic json store`

### 3. Settings model `[x]`

- **Files:** `src/DepotVault.Core/Persistence/Settings.cs`, tests
- **Do:** `Settings`: library roots per volume; `MaxConcurrentJobs` (1), `MaxConcurrentChunks` (16), optional bandwidth cap; `CopyFallback` enum (Unset default); dedupe enabled (true), global exclusion globs (default empty list; user-managed), read-only protection (false), integrity check on startup (true); ACF lock (true); tutorial seen/"don't show again"; theme. Loaded/saved through `AtomicJsonStore`.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add settings model`

### 4. Secret store `[x]`

- **Files:** `src/DepotVault.Core/Persistence/SecretStore.cs`, tests
- **Do:** `auth.bin` read/write. Windows: `ProtectedData` CurrentUser. Linux: file mode 0600 via `File.SetUnixFileMode`. Store refresh token + account name.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add secret store`

### 5. Steam session + auth `[x]`

- **Files:** `src/DepotVault.Core/Steam/SteamSession.cs`, `IGuardPrompt.cs`, `QrLogin.cs`
- **Do:** `SteamClient` + `CallbackManager` on a dedicated pump thread; async wrappers via `TaskCompletionSource`. Credentials flow with `SteamAuthentication.BeginAuthSessionViaCredentialsAsync` and an `IAuthenticator` bridging to `IGuardPrompt` (email code, TOTP, mobile confirm polling). QR flow via `BeginAuthSessionViaQRAsync`, refresh on `ChallengeURLChanged`. `ShouldRememberPassword = true`, persist token via `SecretStore`, log in with token on start, fall back to prompt on expiry. Reconnect with backoff, re-auth via token.
- **Verify:** `dotnet build DepotVault.slnx`; manual: console harness or test login against a real account logs in and reuses token on second run (note result in Progress).
- **Progress:** code + 	ools/DepotVault.Cli harness (dvcli login|login-qr|whoami|logout) built. Manual check pending: user runs dvcli login once, then dvcli whoami must log in via saved token.
- **Commit:** `add steam session and auth`

### 6. PICS depot metadata `[x]`

- **Files:** `src/DepotVault.Core/Steam/DepotMetadataService.cs`, `src/DepotVault.Core/Library/AppRecord.cs`, tests for the parser
- **Do:** `PICSGetAccessTokens` -> `PICSGetProductInfo(app)`; parse `depots` KeyValues: depot ids, names, `config` (oslist, language, osarch), `sharedinstall`/`depotfromapp`, `maxsize`, `manifests/public` (shown as "current" only). Cache in `apps/<appid>.json` with timestamp; refresh on demand. Default depot selection filters by OS/arch/language of the target install, user-overridable. Parser unit-tested against a captured KeyValues fixture.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add pics depot metadata`

### 7. Depot keys, manifest codes, CDN pool, manifest fetch `[x]`

- **Files:** `src/DepotVault.Core/Steam/DepotKeyCache.cs`, `CdnPool.cs`, `ManifestService.cs`
- **Do:** `SteamApps.GetDepotDecryptionKey` cached in memory. `SteamContent.GetManifestRequestCode(depot, app, manifestId, "public")`. `SteamContent.GetServersForSteamPipe()` ranked pool (prefer SteamCache/CDN types, penalty + backoff on failing hosts). `CDN.Client.DownloadManifestAsync` -> `DecryptFilenames(key)` -> save `versions/<versionId>/manifest.bin`. Surface "unavailable" state when a historic manifest is purged.
- **Verify:** `dotnet build DepotVault.slnx`; manual: fetch a known owned manifest, `manifest.bin` loads back with file count > 0.
- **Progress:** verified 2026-10-08: `dvcli manifest 228980 228988` fetched manifest 6645201662696499616 (5 files), reloaded from disk with 5 files.
- **Commit:** `add manifest fetch and cdn pool`

### 8. Download job model + queue `[x]`

- **Files:** `src/DepotVault.Core/Download/DownloadJob.cs`, `DownloadQueue.cs`, `Counters.cs`, tests
- **Do:** `DownloadJob { AppId, DepotId, ManifestId, TargetVersionId, State(Queued/Running/Paused/Done/Failed/Canceled), counters }`. Queue concurrency = `MaxConcurrentJobs`. Linked CTS per job. Counters via `Interlocked` (downloaded, written, deduped). Persist to `queue.json` debounced.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add download job queue`

### 9. Chunk pipeline `[x]`

- **Files:** `src/DepotVault.Core/Download/ChunkPipeline.cs`, `FilePlanner.cs`, tests
- **Do:** per-job pipeline. Plan per file: dedupe hit -> share (step 14 provides the index; stub `IContentIndex` returning no hit until then); older local version of same path -> chunk diff (copy unchanged chunks locally, fetch rest); else full fetch. Preallocate with `File.OpenHandle(..., preallocationSize)`. Bounded `Channel<ChunkWork>` with `MaxConcurrentChunks` workers: CDN download into `ArrayPool<byte>` buffers, `DepotChunk.Process` (decrypt + decompress), chunk checksum, `RandomAccess.Write(handle, data, offset)`. Finalize: verify file SHA-1 for fetched files (parallel), write `state.json`.
- **Verify:** `dotnet test --solution DepotVault.slnx`; manual: download a small owned depot, all file hashes match the manifest.
- **Progress:** verified 2026-10-08: `dvcli download 228980 228988 6645201662696499616` downloaded 27.9 MiB, all file SHA-1s matched.
- **Commit:** `add chunk download pipeline`

### 10. Pause/resume `[x]`

- **Files:** `src/DepotVault.Core/Download/ResumeState.cs`, `DownloadQueue.cs`
- **Do:** per-file chunk completion bitmap persisted (debounced) in `queue.json`. On restart, re-verify claimed chunks by checksum before trusting. Pause = cancel linked CTS and keep state; resume re-plans.
- **Verify:** `dotnet test --solution DepotVault.slnx` (test: kill mid-job simulated by cancel, resume completes with correct hashes)
- **Commit:** `add pause and resume`

### 11. Linking primitives `[x]`

- **Files:** `src/DepotVault.Core/Linking/ILinkStrategy.cs`, `Win/*.cs`, `Linux/*.cs`, `VolumeCapabilities.cs`, tests
- **Do:** `LibraryImport` P/Invoke. Windows: junction (`DeviceIoControl` `FSCTL_SET_REPARSE_POINT`, `IO_REPARSE_TAG_MOUNT_POINT`), `CreateHardLinkW`, `File.CreateSymbolicLink`, reflink (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`, cluster-aligned), volume/file id via `GetVolumeInformationByHandleW`. Linux: dir symlink as junction, `link()`, symlink, `ioctl(FICLONE)`, `stat().st_dev`. Probe capability once per volume (reflink/hardlink/symlink support, hardlink count limit) and cache. Tests gated by platform + capability skip.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add linking primitives`

### 12. Library model `[x]`

- **Files:** `src/DepotVault.Core/Library/LibraryIndex.cs`, `VersionRecord.cs`, `LibraryRoots.cs`, tests
- **Do:** `library.json` with roots, apps, versions (id = short stable id, label, date, manifests, active flag). Root suggestion next to each Steam library folder. Merge multi-depot downloads into one version folder. "Unique size" per version computed from link counts.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add library model`

### 13. Content index `[ ]`

- **Files:** `src/DepotVault.Core/Library/ContentIndex.cs`, tests
- **Do:** key `(Sha1 as 20-byte struct, long size)` -> list of `FileRef(versionId, relPath)`. Built in memory from saved manifests at startup (no disk hashing). Implements `IContentIndex`. Respect hardlink limit (1023 on Windows): stop sharing beyond it.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add content index`

### 14. Dedupe sharing in pipeline `[ ]`

- **Files:** `src/DepotVault.Core/Library/Deduper.cs`, `Download/FilePlanner.cs`, tests
- **Do:** replace the stub; on index hit (source verified intact, same volume) share target via reflink, else hardlink, else no share (fetch normally). Honor global exclusion globs and per-app mutable-file decisions (step 15): Isolate files never hardlinked (reflink or fetch/copy). Track "saved by dedupe" bytes.
- **Verify:** `dotnet test --solution DepotVault.slnx`; manual: second version of a game downloads with deduped bytes > 0.
- **Commit:** `add dedupe sharing`

### 15. Mutable-file review `[ ]`

- **Files:** `src/DepotVault.Core/Library/MutableFileScanner.cs`, `Library/AppRecord.cs`, tests
- **Do:** heuristic scanner over a manifest's file list returning candidate mutable files (config/ini/cfg/json/xml/sav/dat patterns, cache/save/log/config dirs, files whose mtime changed since link, files reported by self-heal). Per-app persisted decisions `Share | Isolate` keyed by relPath or pattern in `apps/<appid>.json`. Unreviewed = treated as normal shared. API consumed by the UI dialog (step 28), linker (14, 19) and self-heal (16).
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add mutable file scanner`

### 16. Snapshot + self-heal `[ ]`

- **Files:** `src/DepotVault.Core/Library/IntegrityChecker.cs`, `Healer.cs`, tests
- **Do:** at link time store `(size, LastWriteTimeUtc, linkKind)` per shared file in `state.json`. Stat-only scan via `FileSystemEnumerable` on app start (background), before switch, and before using a file as a dedupe source. On mismatch hash-verify against manifest SHA-1. If diverged: the active version keeps the modified inode unshared; other versions get pristine data from an intact sibling (different inode, same hash) or re-fetched chunks. Auto-add diverged paths to the per-app exclusion list, **except** files marked Share (skip entirely) and surface newly diverged files as candidates for the step 15 review.
- **Verify:** `dotnet test --solution DepotVault.slnx` (test: modify a hardlinked file in place, heal restores siblings)
- **Commit:** `add integrity check and self-heal`

### 17. Optional read-only protection `[ ]`

- **Files:** `src/DepotVault.Core/Library/ReadOnlyProtection.cs`
- **Do:** setting-gated (default off): set read-only attribute on shared files; never on files marked Share. Reversible on version delete.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add read-only protection`

### 18. Steam install discovery + ACF/VDF `[ ]`

- **Files:** `src/DepotVault.Core/SteamInstall/SteamLocator.cs`, `AcfFile.cs`, tests with fixture `.acf`/`libraryfolders.vdf`
- **Do:** Windows `HKCU\Software\Valve\Steam\SteamPath`; Linux `~/.steam/steam`, `~/.local/share/Steam`, Flatpak `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`. Parse `libraryfolders.vdf` (SteamKit2 `KeyValue` text reader) -> libraries -> `appmanifest_<appid>.acf` (`installdir`, `buildid`, `InstalledDepots`, `AutoUpdateBehavior`, `StateFlags`). ACF writer that patches only target keys and round-trips the rest.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add steam install discovery and acf`

### 19. Switcher `[ ]`

- **Files:** `src/DepotVault.Core/SteamInstall/Switcher.cs`, `SwitchPlan.cs`, tests
- **Do:** require Steam not running (detect process, prompt; never kill). Adopt current install on first switch (same volume: rename/move; else reflink/hardlink; else ask per copy rule). Strategy: whole-folder junction (Linux dir symlink) if install dir contains only manifest-owned files; else per-file reflink -> hardlink -> symlink -> copy (only if `CopyFallback == Always` or user agrees) -> fail; foreign files untouched; files absent from target removed to a recoverable staging dir. Honor per-file Share/Isolate decisions (Isolate: reflink or copy, never hardlink). Patch ACF: `AutoUpdateBehavior=1`, `InstalledDepots` manifest ids, `buildid`; optional read-only ACF lock (setting, default on, reversible). Record active version per app. Revert = switch to adopted/latest + restore original ACF. Return a structured failure report (paths + reasons).
- **Verify:** `dotnet test --solution DepotVault.slnx` (temp-dir fake Steam library)
- **Commit:** `add version switcher`

### 20. SteamDB paste parser `[ ]`

- **Files:** `src/DepotVault.Core/Import/SteamDbParser.cs`, tests
- **Do:** span-based line splitting (`MemoryExtensions.EnumerateLines`), no regex in the hot loop. Per line extract first 15-20 digit integer as manifest ID (ulong) and parse the date with invariant culture over known SteamDB formats (fallback null). Detect depot id from a pasted `steamdb.info/depot/<id>/manifests/` URL. Dedupe against existing history; produce rows with status new/duplicate/invalid. Plain ID lists must work.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add steamdb paste parser`

### 21. App shell + DI + navigation `[ ]`

- **Files:** `src/DepotVault.App/App.axaml(.cs)`, `Program.cs`, `ViewModels/ShellViewModel.cs`, `Views/ShellView.axaml`
- **Do:** Microsoft DI wiring for Core services; left nav (Library, Downloads, Settings, Help) + account indicator; theme from settings; compiled bindings. File logging via `ILogger` file sink.
- **Verify:** `dotnet build DepotVault.slnx`; run app, shell opens (use the `run` skill).
- **Commit:** `add app shell`

### 22. Login dialog `[ ]`

- **Files:** `src/DepotVault.App/Views/LoginDialog.axaml`, `ViewModels/LoginViewModel.cs`
- **Do:** tabs Credentials / QR (render challenge URL as QR image, refresh on change), Guard prompt (email/TOTP/mobile confirm), "remember me", auto token login on start.
- **Verify:** run app, log in via both flows, restart auto-logs in.
- **Commit:** `add login dialog`

### 23. Library screen + app detail `[ ]`

- **Files:** `src/DepotVault.App/Views/LibraryView.axaml`, `AppDetailView.axaml`, view models
- **Do:** apps list (icon, name, installed version, version count, total/unique size). App detail: depot selection (filter-matched default), manifest history grid (date, manifest ID, label, downloaded?), versions list (label, date, manifests, unique size, active badge) with Switch, Launch (`steam://run/<appid>`), Open folder, Verify, Delete (unlink only), Edit label/notes.
- **Verify:** run app with seeded library data; screens render and actions invoke Core.
- **Commit:** `add library and app detail screens`

### 24. Downloads screen `[ ]`

- **Files:** `src/DepotVault.App/Views/DownloadsView.axaml`, `ViewModels/DownloadsViewModel.cs`
- **Do:** queue with progress, speed (EWMA), ETA, dedupe savings; pause/resume/cancel/reorder. Poll counters on a 250 ms `DispatcherTimer`.
- **Verify:** run app, start a download, observe live progress and pause/resume.
- **Commit:** `add downloads screen`

### 25. Import dialog + tutorial `[ ]`

- **Files:** `src/DepotVault.App/Views/ImportDialog.axaml`, `TutorialDialog.axaml`, `Assets/tutorial/*`
- **Do:** import dialog: paste, depot pick, preview grid, commit. Tutorial (4 steps: SteamDB game -> Depots, Manifests tab (may need SteamDB sign-in), copy rows, paste in DepotVault) with screenshots as assets; reachable from empty state, dialog "?" button, Help menu; "don't show again" in settings.
- **Verify:** run app, import a sample paste end to end.
- **Commit:** `add import dialog and tutorial`

### 26. Settings screen `[ ]`

- **Files:** `src/DepotVault.App/Views/SettingsView.axaml`, `ViewModels/SettingsViewModel.cs`
- **Do:** all settings from step 3, including library roots, global exclusion globs, copy fallback, read-only protection, ACF lock, integrity-on-startup, theme.
- **Verify:** run app, change settings, restart, persisted.
- **Commit:** `add settings screen`

### 27. Copy-fallback, Steam-running, switch-failure dialogs `[ ]`

- **Files:** `src/DepotVault.App/Views/Dialogs/*`
- **Do:** copy-fallback question with "remember my choice" (writes `CopyFallback`), Steam-running prompt (close Steam; never kill), switch failure report (failing paths + reasons).
- **Verify:** run app, trigger each dialog via switcher test hooks.
- **Commit:** `add switch dialogs`

### 28. Mutable-file review dialog `[ ]`

- **Files:** `src/DepotVault.App/Views/Dialogs/MutableFilesDialog.axaml`, view model
- **Do:** shown before the first switch/dedupe of an app and re-openable from app detail and after self-heal reports new divergences. Lists candidates from step 15 with a per-file/pattern Share/Isolate choice (bulk select), explanation that Share means settings carry across versions and Isolate means reflink/copy. Persist to `apps/<appid>.json`. Dismissing keeps files unreviewed (shared as normal).
- **Verify:** run app, mark files, switch, confirm Isolate files are not hardlinks and Share files are.
- **Commit:** `add mutable files dialog`

### 29. Hardening + tests `[ ]`

- **Files:** `tests/DepotVault.Tests/*`, logging config
- **Do:** error paths (CDN failures, purged manifests, token expiry, disk full), `ILogger` file sink review, tests for parser, linking, dedupe heal, ACF patch, switcher revert. Anti-cheat/DRM: per-app "force strategy" override (copy still opt-in). Document that Steam "verify integrity" reverts a switched game.
- **Verify:** `dotnet build DepotVault.slnx` and `dotnet test --solution DepotVault.slnx`
- **Commit:** `harden error paths and add tests`

## Risks

- SteamDB format/access changes: parser stays tolerant (ID + date per line); plain ID lists always work.
- Old manifests purged from CDN: per-manifest "unavailable" state.
- Steam "verify integrity" reverts a switched game to latest: documented in UI; ACF lock + update-on-launch reduce it.
- Anti-cheat/DRM may reject links: per-app force-strategy override.
- Windows hardlink limit (1023/file): index stops sharing beyond it.

## Deviations

<Append-only. `YYYY-MM-DD` - what changed vs the original plan and why.>

- 2026-10-08 - Solution is `DepotVault.slnx` (SDK 11 default format), all verify commands updated. SDK 11.0.100-preview.6; Microsoft.Extensions.* and ProtectedData pinned to the matching preview.6 packages. Avalonia 12.1.3, SteamKit2 3.4.0, xunit.v3 4.0.1. Added root `global.json` with MTP test runner.
- 2026-10-08 - Step 2: `JsonContext` deferred to step 3 (an empty source-gen context does not compile). Per-depot manifests stored as `versions/<versionId>/<depotId>.manifest.bin` since a version can merge several depots.
- 2026-10-08 - Added `tools/DepotVault.Cli` (`dvcli`) console harness for manual Steam verification (steps 5, 7, 9, 14). User logs in once; later checks reuse the saved token. Steps whose only open item is a Steam-backed manual check stay `[~]` until verified and work continues with the next step.
- 2026-10-08 - Step 7: `CdnPool` ranks `CdnServer` descriptors wrapping SteamKit2 `Server` (its setters are internal, so tests cannot construct it).
- 2026-10-08 - Step 9: `state.json` model (`VersionState`, `FileSnapshot`, `LinkKind`) created here instead of step 16 since the pipeline writes it. `Sha1Hash` 20-byte struct created here (shared with step 13). `AtomicJsonStore.ScheduleSave(Func<T>)` added so debounced saves serialize a snapshot.
- 2026-10-08 - Step 10: resume bitmaps live on `DownloadJob.Resume` (persisted via queue.json); `ResumeTracker` lives in `Download/ResumeState.cs`. Debounced saves got a max-wait (4x debounce) so continuous chunk progress cannot starve persistence.
- 2026-10-08 - Step 11: reflink support on Windows detected via `FSCTL_GET_INTEGRITY_INFORMATION` (fails on non-ReFS) and integrity settings are mirrored onto the clone target. Linux file identity uses `statx` (arch-independent layout). Fallback logic lives in `Linker` + `LinkRules` in `VolumeCapabilities.cs`. Dev box has no ReFS/Dev Drive, so the reflink test is skipped here.
- 2026-10-08 - Step 12: library roots are owned by `Settings.LibraryRoots` (single source); `library.json` holds apps (name, active/adopted version) and versions. Root suggestion is `<steam library>/DepotVault` (inside the library folder, guaranteed same volume). `LibraryTargetResolver` (download target + previous-version chunk diff) added here.

## Open questions

None. Q1-Q3 resolved (see decisions).
