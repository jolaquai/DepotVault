# DepotVault: Implementation Plan

<!-- RESUME PROTOCOL - any agent opening this file must follow this section before touching code. -->

## Resume protocol

You are resuming work described by this file. This file is the single source of truth for progress.

1. Read this entire file, then read every file listed in **Key files** and in the current step.
2. Run `git status` and `git log --oneline -5`. The working tree should be clean and the parent of `HEAD` (`HEAD~1`) should match **Last synced commit** in **Status** (a commit cannot record its own hash, so each commit records its parent). If it does, this file is up to date; trust it and continue at the current step without re-auditing.
3. If the tree is dirty or `HEAD` does not match, reconcile first: figure out what happened, fix this file, commit the fix, then continue.
4. Work the first step that is not `[x]`. One step at a time.
5. **Every step ends with exactly one commit that contains both the code change and the update to this file.** They are never committed separately. This is what makes the file trustworthy.
6. Commit messages: one terse line, imperative, lowercase, no body, no trailing period. Example: `add token cache to auth handler`. No attribution lines or trailers.
7. `git commit` only. **Never** `git push`, `git commit --amend`, `git rebase`, or `git reset --hard` unless explicitly told to. Exception: in a cloud session (ephemeral container) push `main` to `origin` at the end of the session; never on the user's local machine. Stage by naming each path explicitly; never `git add -A`, `.`, `-u` or globs.
8. Never mark a step `[x]` before its **Verify** command has actually run and passed. If it fails, the step stays `[~]` and the failure goes in **Deviations**.
9. If reality diverges from the plan (a step is wrong, impossible, or unnecessary), amend the steps here and log it in **Deviations** in the same commit. Never silently deviate.
10. If a turn ends mid-step, the step stays `[~]` with a `Progress:` line describing exactly where it stopped and what is left. Commit whatever is coherent; if nothing is coherent, still update `Progress:` and commit only this file.
11. Do not ask for a plan-freshness check. Assume it is fresh unless step 2 says otherwise.
12. Code conventions (user's global rules): latest .NET/C#, `<Nullable>disable</Nullable>` with no nullable annotations, CRLF line endings (LF for `*.sh`, Dockerfiles, git hooks), terse one-line comments only for grave deviations, no en/em dashes in any text, xUnit.v3 on Microsoft.Testing.Platform + NSubstitute (never Moq), Span/stackalloc/`CollectionsMarshal` welcome, no hardware intrinsics. After code changes run `dotnet build` on the solution.

Step states: `[ ]` not started, `[~]` in progress, `[x]` done, `[!]` blocked, `[-]` dropped.

## Handoff notes (read before continuing)

Written 2026-10-08 when work moved from the user's Windows machine to a cloud session.

**User's standing instructions (verbatim intent, apply to everything):**
- Implement the plan. No walls of comments. No comments explaining the how and why of code, no historical analysis of why code is the way it is. Follow the comment rules in rule 12; most code has zero comments.
- Chunked commits, one per step (rule 5). Single-line, terse commit messages. No attribution trailers.
- Anything unclear: ask the user instead of guessing.

**Environment constraints from now on:**
- The session runs in the cloud (assume Linux, no display, no Steam client). Never launch the real app window and never drive a desktop. UI verification is only via headless rendering: `tools/DepotVault.UiSnap` (Avalonia.Headless + Skia). Run `dotnet run --project tools/DepotVault.UiSnap -- <outDir> [dataDir] [scenario]`, then read the PNGs. Scenarios: `pages` (default), `login`, `autologin`, `library-actions`, `downloads-live`; add new ones in `tools/DepotVault.UiSnap/Scenarios.cs`. Scenarios must stay synchronous and pump the dispatcher via the `pump`/`Wait` helpers; awaiting on the UI sync context deadlocks headless runs. Always run UiSnap with an external timeout (it seeds `Seed.cs` data into the data dir on first run).
- SDK: .NET 11 preview (`11.0.100-preview.6.26359.118` was used; any newer net11 SDK is fine). Install it if missing (`dotnet-install.sh --channel 11.0 --quality preview`). `Microsoft.Extensions.*` and `ProtectedData` are pinned to `11.0.0-preview.6.26359.118` in `Directory.Packages.props`; bump them together with the SDK if restore fails.
- Steam: the user's saved refresh token is DPAPI-protected on their Windows machine and is not available in the cloud. Do not ask for credentials and never type passwords anywhere. Steam-backed checks (`tools/DepotVault.Cli` = `dvcli`, UiSnap `autologin`/`downloads-live`) cannot run in the cloud: mark such checks as pending-user in the step's `Progress:` line, log it in Deviations, and continue with the next step.
- Linux is now the native test platform: run `dotnet test --solution DepotVault.slnx` early. Windows-only tests skip there; the Linux link strategy (`Linking/Linux/*`: `statx` offsets, `FICLONE`, `link()`, directory symlinks) and Unix file-mode code have never been executed yet, so failures there are real bugs to fix (log fixes in Deviations).
- The `plan-step.ps1` helper used earlier lived in a temp scratchpad and is gone; update this file by hand (mark step state, `Current step`, `Last synced commit` = parent of the commit you are about to make, Deviations).
- Line endings: CRLF for everything except `*.sh`, Dockerfiles, git hooks (`.gitattributes` has `* text=auto eol=crlf`). Files written by shell redirection or generators must be checked.

**Open threads for the remaining steps:**
- Step 22 still needs one interactive check by the user on their machine (sign in through the dialog via password and via QR scan). Leave it `[~]`.
- Dialogs: `AppDetailViewModel` calls the `AppDialogs` coordinator (`Services/AppDialogs.cs`) directly (import, mutable review, switch report); new dialogs derive from `Views/DialogWindow` with a VM implementing `IDialogViewModel`.
- Step 25 tutorial screenshots: deferred to the user (local). `TutorialViewModel` loads `Assets/tutorial/step1.png`..`step4.png` if present; just drop the files in.
- Settings UI (step 26) reloads from `Vault.Settings.Current` on page activation, so a remembered copy choice written by the step 27 dialog shows up there without extra wiring.
- Core composition root is `src/DepotVault.Core/Vault.cs`; UI gets everything through it via DI.

## Status

- **State:** implemented; open items are user-side only (step 22 interactive sign-in check, step 25 tutorial screenshots)
- **Current step:** none (all steps done except the step 22 interactive check)
- **Branch:** main
- **Base commit:** a0fe4d9
- **Last synced commit:** 2f4e05a (parent of HEAD)
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
tools/DepotVault.Cli/     dvcli: Steam harness (login, app, manifest, download, lib-download)
tools/DepotVault.UiSnap/  headless UI renderer for verification
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

### 13. Content index `[x]`

- **Files:** `src/DepotVault.Core/Library/ContentIndex.cs`, tests
- **Do:** key `(Sha1 as 20-byte struct, long size)` -> list of `FileRef(versionId, relPath)`. Built in memory from saved manifests at startup (no disk hashing). Implements `IContentIndex`. Respect hardlink limit (1023 on Windows): stop sharing beyond it.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add content index`

### 14. Dedupe sharing in pipeline `[x]`

- **Files:** `src/DepotVault.Core/Library/Deduper.cs`, `Download/FilePlanner.cs`, tests
- **Do:** replace the stub; on index hit (source verified intact, same volume) share target via reflink, else hardlink, else no share (fetch normally). Honor global exclusion globs and per-app mutable-file decisions (step 15): Isolate files never hardlinked (reflink or fetch/copy). Track "saved by dedupe" bytes.
- **Verify:** `dotnet test --solution DepotVault.slnx`; manual: second version of a game downloads with deduped bytes > 0.
- **Commit:** `add dedupe sharing`

### 15. Mutable-file review `[x]`

- **Files:** `src/DepotVault.Core/Library/MutableFileScanner.cs`, `Library/AppRecord.cs`, tests
- **Do:** heuristic scanner over a manifest's file list returning candidate mutable files (config/ini/cfg/json/xml/sav/dat patterns, cache/save/log/config dirs, files whose mtime changed since link, files reported by self-heal). Per-app persisted decisions `Share | Isolate` keyed by relPath or pattern in `apps/<appid>.json`. Unreviewed = treated as normal shared. API consumed by the UI dialog (step 28), linker (14, 19) and self-heal (16).
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add mutable file scanner`

### 16. Snapshot + self-heal `[x]`

- **Files:** `src/DepotVault.Core/Library/IntegrityChecker.cs`, `Healer.cs`, tests
- **Do:** at link time store `(size, LastWriteTimeUtc, linkKind)` per shared file in `state.json`. Stat-only scan via `FileSystemEnumerable` on app start (background), before switch, and before using a file as a dedupe source. On mismatch hash-verify against manifest SHA-1. If diverged: the active version keeps the modified inode unshared; other versions get pristine data from an intact sibling (different inode, same hash) or re-fetched chunks. Auto-add diverged paths to the per-app exclusion list, **except** files marked Share (skip entirely) and surface newly diverged files as candidates for the step 15 review.
- **Verify:** `dotnet test --solution DepotVault.slnx` (test: modify a hardlinked file in place, heal restores siblings)
- **Commit:** `add integrity check and self-heal`

### 17. Optional read-only protection `[x]`

- **Files:** `src/DepotVault.Core/Library/ReadOnlyProtection.cs`
- **Do:** setting-gated (default off): set read-only attribute on shared files; never on files marked Share. Reversible on version delete.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add read-only protection`

### 18. Steam install discovery + ACF/VDF `[x]`

- **Files:** `src/DepotVault.Core/SteamInstall/SteamLocator.cs`, `AcfFile.cs`, tests with fixture `.acf`/`libraryfolders.vdf`
- **Do:** Windows `HKCU\Software\Valve\Steam\SteamPath`; Linux `~/.steam/steam`, `~/.local/share/Steam`, Flatpak `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`. Parse `libraryfolders.vdf` (SteamKit2 `KeyValue` text reader) -> libraries -> `appmanifest_<appid>.acf` (`installdir`, `buildid`, `InstalledDepots`, `AutoUpdateBehavior`, `StateFlags`). ACF writer that patches only target keys and round-trips the rest.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add steam install discovery and acf`

### 19. Switcher `[x]`

- **Files:** `src/DepotVault.Core/SteamInstall/Switcher.cs`, `SwitchPlan.cs`, tests
- **Do:** require Steam not running (detect process, prompt; never kill). Adopt current install on first switch (same volume: rename/move; else reflink/hardlink; else ask per copy rule). Strategy: whole-folder junction (Linux dir symlink) if install dir contains only manifest-owned files; else per-file reflink -> hardlink -> symlink -> copy (only if `CopyFallback == Always` or user agrees) -> fail; foreign files untouched; files absent from target removed to a recoverable staging dir. Honor per-file Share/Isolate decisions (Isolate: reflink or copy, never hardlink). Patch ACF: `AutoUpdateBehavior=1`, `InstalledDepots` manifest ids, `buildid`; optional read-only ACF lock (setting, default on, reversible). Record active version per app. Revert = switch to adopted/latest + restore original ACF. Return a structured failure report (paths + reasons).
- **Verify:** `dotnet test --solution DepotVault.slnx` (temp-dir fake Steam library)
- **Commit:** `add version switcher`

### 20. SteamDB paste parser `[x]`

- **Files:** `src/DepotVault.Core/Import/SteamDbParser.cs`, tests
- **Do:** span-based line splitting (`MemoryExtensions.EnumerateLines`), no regex in the hot loop. Per line extract first 15-20 digit integer as manifest ID (ulong) and parse the date with invariant culture over known SteamDB formats (fallback null). Detect depot id from a pasted `steamdb.info/depot/<id>/manifests/` URL. Dedupe against existing history; produce rows with status new/duplicate/invalid. Plain ID lists must work.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Commit:** `add steamdb paste parser`

### 21. App shell + DI + navigation `[x]`

- **Files:** `src/DepotVault.App/App.axaml(.cs)`, `Program.cs`, `ViewModels/ShellViewModel.cs`, `Views/ShellView.axaml`
- **Do:** Microsoft DI wiring for Core services; left nav (Library, Downloads, Settings, Help) + account indicator; theme from settings; compiled bindings. File logging via `ILogger` file sink.
- **Verify:** `dotnet build DepotVault.slnx`; run app, shell opens (use the `run` skill).
- **Commit:** `add app shell`

### 22. Login dialog `[~]`

- **Files:** `src/DepotVault.App/Views/LoginDialog.axaml`, `ViewModels/LoginViewModel.cs`
- **Do:** tabs Credentials / QR (render challenge URL as QR image, refresh on change), Guard prompt (email/TOTP/mobile confirm), "remember me", auto token login on start.
- **Verify:** run app, log in via both flows, restart auto-logs in.
- **Progress:** dialog done (Account tab, QR tab with live Steam challenge URL rendered via QRCoder, inline Steam Guard prompt for email/device code/mobile confirm, remember-me). Verified headless: `UiSnap login` renders all states and a real QR; `UiSnap autologin` signs in from a saved token and the shell shows the account. Pending (user, interactive): sign in through the dialog via password and via QR scan.
- **Commit:** `add login dialog`

### 23. Library screen + app detail `[x]`

- **Files:** `src/DepotVault.App/Views/LibraryView.axaml`, `AppDetailView.axaml`, view models
- **Do:** apps list (icon, name, installed version, version count, total/unique size). App detail: depot selection (filter-matched default), manifest history grid (date, manifest ID, label, downloaded?), versions list (label, date, manifests, unique size, active badge) with Switch, Launch (`steam://run/<appid>`), Open folder, Verify, Delete (unlink only), Edit label/notes.
- **Verify:** run app with seeded library data; screens render and actions invoke Core.
- **Commit:** `add library and app detail screens`

### 24. Downloads screen `[x]`

- **Files:** `src/DepotVault.App/Views/DownloadsView.axaml`, `ViewModels/DownloadsViewModel.cs`
- **Do:** queue with progress, speed (EWMA), ETA, dedupe savings; pause/resume/cancel/reorder. Poll counters on a 250 ms `DispatcherTimer`.
- **Verify:** run app, start a download, observe live progress and pause/resume.
- **Commit:** `add downloads screen`

### 24a. Linux test pass `[x]`

- **Files:** `src/DepotVault.Core/Library/LibraryIndex.cs`, `tests/DepotVault.Tests/Library/MutableFileScannerTests.cs`
- **Do:** first run of the suite on Linux; fix real failures.
- **Verify:** `dotnet test --solution DepotVault.slnx`
- **Progress:** 75 passed, 2 skipped (DPAPI Windows-only; reflink: cloud volume is ext4, no btrfs/xfs tooling to build a loop volume).
- **Commit:** `fix linux test failures`

### 25. Import dialog + tutorial `[x]`

- **Files:** `src/DepotVault.App/Views/ImportDialog.axaml`, `TutorialDialog.axaml`, `ViewModels/ImportViewModel.cs`, `TutorialViewModel.cs`, `Services/AppDialogs.cs`, `Assets/tutorial/*` (deferred)
- **Do:** import dialog: paste, depot pick, preview grid, commit. Tutorial (4 steps: SteamDB game -> Depots, Manifests tab (may need SteamDB sign-in), copy rows, paste in DepotVault) with screenshots as assets; reachable from empty state, dialog "?" button, Help menu; "don't show again" in settings.
- **Verify:** run app, import a sample paste end to end.
- **Progress:** verified with `UiSnap import`: tutorial pages render, "don't show again" persists; import opened from app detail, depot detected from the pasted URL (480001), preview showed 3 new / 1 duplicate / 1 invalid, one row unticked, 2 imported and persisted to `apps/480000.json`, history grid refreshed. Screenshots pending (user, local).
- **Commit:** `add import dialog and tutorial`

### 26. Settings screen `[x]`

- **Files:** `src/DepotVault.App/Views/SettingsView.axaml`, `ViewModels/SettingsViewModel.cs`, `src/DepotVault.Core/Vault.cs`
- **Do:** all settings from step 3, including library roots, global exclusion globs, copy fallback, read-only protection, ACF lock, integrity-on-startup, theme.
- **Verify:** run app, change settings, restart, persisted.
- **Progress:** verified with `UiSnap settings`: page renders, every setting changed through the VM, removing a root that still holds versions is refused, adding/removing an empty root works, read-only toggle runs over the library, a fresh `SettingsStore` reloads all changed values.
- **Commit:** `add settings screen`

### 27. Copy-fallback, Steam-running, switch-failure dialogs `[x]`

- **Files:** `src/DepotVault.App/Views/Dialogs/*`, `Views/DialogWindow.cs`, `ViewModels/SteamRunningViewModel.cs`, `CopyConsentViewModel.cs`, `SwitchReportViewModel.cs`, `IDialogViewModel.cs`, `Services/SwitchPrompts.cs`
- **Do:** copy-fallback question with "remember my choice" (writes `CopyFallback`), Steam-running prompt (close Steam; never kill), switch failure report (failing paths + reasons).
- **Verify:** run app, trigger each dialog via switcher test hooks.
- **Progress:** verified with `UiSnap switch-dialogs` (fake Steam install via `HOME` override, `Vault.IsSteamRunning` hook): real switch from app detail showed the Steam-running dialog, continued once "Steam" exited, failed offline adoption and opened the report dialog; with a cached installed manifest the same switch succeeded (Linux dir symlink) and revert restored the original dir; copy consent returned allow+remember; cancelling the Steam wait returned false.
- **Commit:** `add switch dialogs`

### 28. Mutable-file review dialog `[x]`

- **Files:** `src/DepotVault.App/Views/Dialogs/MutableFilesDialog.axaml`, `ViewModels/MutableFilesViewModel.cs`, `ViewModels/AppDetailViewModel.cs`, `src/DepotVault.Core/Library/MutableFileScanner.cs`
- **Do:** shown before the first switch/dedupe of an app and re-openable from app detail and after self-heal reports new divergences. Lists candidates from step 15 with a per-file/pattern Share/Isolate choice (bulk select), explanation that Share means settings carry across versions and Isolate means reflink/copy. Persist to `apps/<appid>.json`. Dismissing keeps files unreviewed (shared as normal).
- **Verify:** run app, mark files, switch, confirm Isolate files are not hardlinks and Share files are.
- **Progress:** verified with `UiSnap mutable`: two hardlinked versions with saved manifests/state, pending-review banner shown for a self-heal candidate, the first switch opened the review (3 candidates with reasons), `saves/slot1.sav` set to Isolate via bulk select, `config/settings.ini` to Share, a `*.log` pattern added; after save the switch ran (junction): Isolate file link count 1, Share and undecided files link count 2; rules persisted, reviewed flag set, candidates cleared.
- **Commit:** `add mutable files dialog`

### 29. Hardening + tests `[x]`

- **Files:** `tests/DepotVault.Tests/*`, `src/DepotVault.Core/Download/JobErrors.cs`, `DownloadJob.cs`, `DownloadQueue.cs`, `Vault.cs`, `Logging/FileLoggerProvider.cs`, `src/DepotVault.App/App.axaml.cs`, `AppDetailView.axaml`, `HelpView.axaml`
- **Do:** error paths (CDN failures, purged manifests, token expiry, disk full), `ILogger` file sink review, tests for parser, linking, dedupe heal, ACF patch, switcher revert. Anti-cheat/DRM: per-app "force strategy" override (copy still opt-in). Document that Steam "verify integrity" reverts a switched game.
- **Verify:** `dotnet build DepotVault.slnx` and `dotnet test --solution DepotVault.slnx`
- **Progress:** build clean, 84 passed, 2 skipped (DPAPI Windows-only, reflink needs btrfs/xfs/ReFS). All UiSnap scenarios re-run; force strategy persists from the new Advanced picker.
- **Commit:** `harden error paths and add tests`

### 30. CI workflow `[x]`

- **Files:** `.github/workflows/ci.yml`, `tools/DepotVault.UiSnap/*`, `tests/DepotVault.Tests/Linking/LinkingTests.cs`
- **Do:** GitHub Actions on push to main, PRs and manual dispatch. Matrix: Linux ext4, Linux btrfs and xfs loop volumes (reflink via `FICLONE`), Windows NTFS, Windows ReFS VHD (block cloning). Reflink jobs point the temp dir at the volume and set `DV_EXPECT_REFLINK=1` so the reflink test fails instead of skipping. UiSnap scenarios get pass/fail checks (`Scenarios.Check`, exit code 1) and run as headless smoke tests (Steam-free ones on Linux and Windows, fake-Steam `switch-dialogs`/`mutable` on Linux); renders uploaded as artifacts. No macOS (not a target platform; postponed by the user).
- **Verify:** all matrix jobs green on GitHub.
- **Progress:** first run (2f4e05a) green on all 5 jobs: 86 tests, 81 passed / 5 skipped per job; on btrfs, xfs and ReFS the reflink test ran (hard-asserted via `DV_EXPECT_REFLINK`) while the hardlink-only tests skipped; UiSnap scenarios passed on Linux and Windows. Actions bumped to v5 (Node 20 deprecation).
- **Commit:** `add ci workflow`

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
- 2026-10-08 - Step 14: manual check used the same manifest (228988/6645201662696499616) for two library versions via `dvcli lib-download`; second version deduped 29,212,173 bytes with 0 downloaded. Share policy is injected into `LibraryTargetResolver` via `SharePolicy` (enabled flag, never-share predicate, isolate predicate); step 15 provides the per-app predicates. `PathGlob` (simple `*`/`?` matcher) added for exclusion globs.
- 2026-10-08 - Step 15: decisions are `MutableRule { Pattern, Decision }` on `AppRecord` (`Unreviewed` = enum default); explicit Share overrides per-app exclusions and global globs. `MutableFileScanner.BuildPolicy` produces the `SharePolicy` for the download resolver.
- 2026-10-08 - Step 16: the stat scan re-stats each file via `FileInfo` on Windows because NTFS directory entries of sibling hardlink names keep stale size/mtime. Diverged files in non-active versions are restored with reflink-or-copy (never hardlink) since they become excluded. `AtomicJsonStore` debounce param is now a plain `TimeSpan` (no nullable).
- 2026-10-08 - Step 17: protection applies to the inode, so clearing it on version delete also unprotects siblings until the next `Apply`. `FileUtil.ForceDelete` used wherever DepotVault replaces files.
- 2026-10-08 - Step 18: ACF patching uses an own span-preserving `VdfDocument` (edits value spans in place, inserts missing keys with sibling indentation) since SteamKit2 `KeyValue` re-serializes the whole file; `libraryfolders.vdf` still uses `KeyValue`. Verified read-only against the real install: 16 ACFs round-trip byte-exact.
- 2026-10-08 - Step 19: adoption loads the installed manifests (ACF `InstalledDepots`) to tell manifest-owned from foreign files; no foreign files on the same volume = one directory rename, otherwise owned files move individually and foreign files stay. Cross-volume adoption counts as a copy and goes through the copy rule. Per-app install state (`InstallState`: mode, version, placed files with size/mtime/link kind) lives in `apps/<appid>.json`; leaving per-file mode deletes unchanged placed files and stages changed independent copies. Foreign files written into a junctioned version dir are moved into a real install dir (per-file mode) on the next switch. ACF `buildid` is written from `SwitchRequest.AcfBuildId` (caller passes the app's current public build so Steam sees no pending update); `InstalledDepots` gets the target manifests. `AppRecord.ForceStrategy` (junction/hardlink/symlink/copy) is honored here already (UI in step 29).
- 2026-10-08 - Step 21: added `Core/Vault.cs` composition root (all services + queue/runner/resolver wiring) and `Core/Logging/FileLoggerProvider.cs`; DI registers `Vault` and page VMs. Shell is `Views/ShellWindow.axaml`. GUI verification uses `tools/DepotVault.UiSnap` (Avalonia headless + Skia, seeds a temp data dir, renders pages/scenarios to PNG) because the desktop must not be driven while the user is using it; `dotnet run --project tools/DepotVault.UiSnap -- <outDir> [dataDir] [scenario]`.
- 2026-10-08 - Step 22 follow-up: QR login is the default tab and starts when the dialog opens (no password typed into DepotVault); a browser login cannot replace it because Steam web logins only yield web-audience tokens, while CM logon needs a client refresh token. Step 23: no app icons in v1 (would need CDN image fetching); apps are added from installed Steam games or by App ID. History download picks, per selected depot, the newest imported manifest not newer than the chosen row. Switch uses a placeholder `ISwitchPromptsFactory` that declines prompts until step 27. Verified with `UiSnap pages` and `UiSnap library-actions`.
- 2026-10-08 - Step 24: verified headlessly with `UiSnap downloads-live` against real Steam (depot 228988, 3 MiB/s cap): live progress rendered, pause kept 4 resume bitmaps at 11.2 MiB, resume reused 11.7 MB and wrote 17.5 MB, version complete.
- 2026-10-08 - Step 24a added: first Linux run. Version delete now clears read-only on all platforms (Linux kept the shared inode read-only for the surviving sibling); scanner test looked up manifest names with Windows separators. Linux `FICLONE` path still unexecuted (no reflink-capable volume in the cloud).
- 2026-10-08 - Step 25: tutorial ships text-only (user's call: no SteamDB screenshots from the cloud; they add `Assets/tutorial/step1..4.png` locally, loaded automatically when present). Dialogs are created by a new `AppDialogs` coordinator (single import dialog at a time; tutorial auto-opens over the import dialog unless "don't show again" is set). Import dialog takes a depot from the pasted SteamDB URL, the app's depot list, or a typed depot ID; rows can be unticked. Library empty state and Help page link to the tutorial. UiSnap scenario `import` added.
- 2026-10-08 - Step 26: settings apply immediately (no Save button). Toggling read-only protection applies/removes it across all versions (`Vault.ApplyReadOnlyProtection`); toggling ACF lock re-locks/unlocks the ACF of every switched (non-adopted active) app (`Vault.ApplyAcfLock`). A library root can only be removed while no version lives in it (`Vault.RemoveRoot`); Steam-library suggestions are offered with one-click add. "Show walkthrough when importing" is the inverse of `TutorialDontShowAgain`. UiSnap `Seed` now registers its root in `settings.json` like a real download would; UiSnap scenario `settings` added.
- 2026-10-08 - Step 27: `SwitchPrompts` (real `ISwitchPrompts`, marshals to the UI thread since the switcher calls prompts after `ConfigureAwait(false)`) replaces `DeclineSwitchPrompts`. Steam-running dialog polls every second off the UI thread and closes itself when Steam exits. `Vault.IsSteamRunning` is the switcher test hook. Fixes found on the way: `Settings.Changed` theme handler now posts to the UI thread (a remembered copy choice saves settings from a pool thread); installed-manifest source fails fast when not signed in and the manifest is not cached (an offline SteamKit job timed out as a cancellation, so no report was shown). `DialogService.Showing` lets UiSnap capture dialogs; UiSnap `Pump` posts a no-op so headless `DispatcherTimer`s get promoted. First end-to-end run of the Linux switch path (dir symlink + revert) passed.
- 2026-10-08 - Step 28: candidates come from all saved manifests of the app (`MutableFileScanner.ScanApp`) plus a stat-only integrity scan for "changed since linked". The review opens (and is awaited) before the first switch and before the first download that could dedupe against an existing version; app detail shows a banner while self-heal candidates are pending and refreshes on `Vault.HealCompleted`. Per-file choices are stored as exact rules, pattern rules (globs) are edited in the dialog; per-file rules that equal what a pattern gives are not stored. Explicit "Not decided" cannot override a pattern. Import, review and switch-report events on `AppDetailViewModel` were replaced by direct `AppDialogs` calls. Also fixed: read-only protection now clears the flag on files marked Share; the download queue sets `Error`/`FinishedUtc` before publishing `Failed`/`Done` (a reader saw `Failed` with no error; flaky `FailureIsRecordedAndRetryable`). UiSnap `Seed` marks the sample app reviewed; UiSnap scenario `mutable` added.
- 2026-10-08 - Step 29: jobs carry `JobErrorKind` (`JobErrors.Classify`: purged manifest, depot access denied, disk full via ENOSPC/ERROR_DISK_FULL, network, Steam timeout, other) with a user-facing message; a purged manifest marks the matching history entry unavailable (`AppRecord.MarkUnavailable`); network/timeout failures resume automatically on the next sign-in. Token expiry was already covered (session lost re-opens sign-in). File logger survives `UnauthorizedAccessException`; unhandled AppDomain/task/UI exceptions are logged. Force strategy picker under Advanced in app detail (copy there is an explicit opt-in). Steam verify/update behavior documented in app detail and Help (Help also shows the log folder). New tests: error classification, queue error kind, history marking, file logger, forced copy/hardlink strategies, missing-file switch report. Parser, linking, heal, ACF patch and switcher revert were already covered.
- 2026-10-08 - Step 30 added at the user's request: CI workflow (see step). UiSnap `Save` now checks that each render was written; `Program` returns 1 when any check failed.
- 2026-10-08 - Step 30: first CI run executed the Linux `FICLONE` path (btrfs, xfs) and Windows block cloning on ReFS for the first time; all passed without code changes.

## Open questions

None. Q1-Q3 resolved (see decisions).
