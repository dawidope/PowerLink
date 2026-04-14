# TODO — PowerLink backlog

Deferred work, in no particular order. Pick one, read the notes, file an
issue if needed, implement.

## Faza 2.5 — Modern IExplorerCommand (Windows 11 main context menu)

**Goal:** make "PowerLink: Pick as link source" and "PowerLink: Drop as
hardlink here" appear in the **main** Windows 11 right-click menu, not
under "Show more options" / Shift+F10. This is the PowerToys approach.

**Why:** current Phase 2 uses per-user registry entries under
`HKCU\Software\Classes\*\shell\...` — Win11 hides those behind "Show more
options". `IExplorerCommand`-registered verbs appear in the default flat
menu.

**Approach:**
1. New project `src/PowerLink.ShellCommands/` — C# WinRT component (class
   library, `net8.0-windows10.0.19041.0`, `<CsWinRTComponent>true</CsWinRTComponent>`,
   `<EnableComHosting>true</EnableComHosting>`).
2. Implement `Microsoft.UI.Shell.IExplorerCommand` for each verb:
   - `PickAsLinkSourceCommand` (file / folder)
   - `DropAsHardlinkCommand` (folder / directory background)
   Each class returns title, icon, state (enabled/hidden based on
   selection type), and an `InvokeAsync` that delegates to
   `PowerLink.Cli` via `Process.Start` or directly calls `Core`
   (in-process inside explorer.exe — must be non-blocking!).
3. Package as a **Sparse Package** (AppxManifest.xml only, no full MSIX
   install). Sparse packages register WinRT activation via registry,
   accepted by Win11 without Store distribution.
4. `ShellExtensionService` gains a parallel code path:
   `InstallModern()` / `UninstallModern()` that register the sparse
   package via `PackageManager.AddPackageByUriAsync` with the sparse
   package flag. Keep the old registry path as fallback for Win10.
5. Settings page: detect Win11 22H2+ and offer the modern mode by
   default; fall back to legacy on Win10.

**Critical files to touch:**
- `src/PowerLink.App/PowerLink.App.csproj` — maybe reference the
  ShellCommands project or bundle its dll next to App.
- `src/PowerLink.App/Services/ShellExtensionService.cs` — new install
  path.
- `src/PowerLink.App/Pages/SettingsPage.xaml` — toggle or auto-detect.

**Reference reading:**
- <https://learn.microsoft.com/en-us/windows/win32/shell/nse-cpp-iexplorercommand>
- <https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps#sparse-package-identity>
- PowerToys source: `src/modules/PowerAccent/PowerAccent.ContextMenu/`
  is the closest template.

**Pitfalls:**
- Shell extensions run INSIDE `explorer.exe`. Don't block, don't crash.
- Sparse packages need a signed `.msix`-like manifest. Dev can
  self-sign; distribution eventually needs a real cert.
- Icon resources — `IExplorerCommand.GetIcon` wants a resource string,
  not a file path. We'll need an embedded icon resource.

---

## Show hardlinks of this file

**Goal:** right-click any file in Explorer → "PowerLink: Show hardlinks"
→ opens PowerLink.App on a dedicated Hardlinks page listing every path
that shares the file's data (ref count, file index, size).

**Approach:**
1. `PowerLink.Core/Native/Win32Hardlink.cs` — add
   `EnumerateHardLinks(string path)` using `FindFirstFileNameW` +
   `FindNextFileNameW`. They return paths without a drive — prepend
   `Path.GetPathRoot(path).TrimEnd('\\')` to each.
2. New `src/PowerLink.App/Pages/HardlinksPage.xaml` with a picker +
   list + "Copy all paths" / "Open in Explorer".
3. `App.xaml.cs` — parse command line in `OnLaunched`; if
   `--show-links <path>`, after `MainWindow.Activate()` navigate to
   HardlinksPage and set `ViewModel.FilePath`.
4. `ShellExtensionService.Install` — add a fourth verb
   `HKCU\Software\Classes\*\shell\PowerLinkShowLinks` with command
   `"PowerLink.App.exe" --show-links "%1"`.
5. CLI: `powerlink links <path>` — prints each path on its own line
   (one-shot, for scripting).

**Tests:** create two hardlinks to the same file in a TempDirectory,
assert `EnumerateHardLinks` returns both full paths.

---

## Performance: parallel hashing for NVMe

**Goal:** DedupEngine currently hashes files sequentially. On NVMe with
queue depth 1 we top out ~400 MB/s. Parallel file reads saturate the
drive — expected 3-5× speedup.

**Approach:**
1. `DedupEngine.AnalyzeAsync` — add `int maxDegreeOfParallelism`
   parameter (default `Environment.ProcessorCount / 2`, clamp to [1, 8]).
2. Use `Parallel.ForEachAsync(fullHashCandidates, new ParallelOptions
   { MaxDegreeOfParallelism = ..., CancellationToken = ct })` for the
   full-hash phase. Keep prefix-hash sequential (fast anyway).
3. Thread-safety: `bytesAccumulated` + `fullProcessed` need `Interlocked`
   updates. The shared `IProgress<ScanProgress>` is thread-safe
   (Progress<T> serializes via sync context).
4. Auto-detect drive type via
   `Win32.GetDriveType` + `STORAGE_PROPERTY_QUERY`
   (IOCTL_STORAGE_QUERY_PROPERTY with StorageDeviceSeekPenaltyProperty):
   0 = SSD, >0 = HDD. On HDD, default parallelism to 1 (avoid
   thrashing). On SSD, use the default.
5. UI: slider in DedupPage next to hash buffer, range 1–8, label
   "Parallel files". Persist per-session in VM (not across restarts
   yet).

**Tests:** new integration test — 8 large identical files, run with
parallelism=1 and parallelism=4, assert both produce the same
DuplicateGroup result.

---

## Dedup safety: undo / manifest log

**Goal:** before executing the dedup plan, write a manifest file
listing every (duplicate, canonical) pair the executor is about to
collapse. Let the user "undo" by restoring each duplicate from its
canonical via file copy.

**Approach:**
1. `DedupExecutor.ExecuteAsync` — before the loop, serialize the plan
   plus timestamp + volume info to
   `%LOCALAPPDATA%\PowerLink\undo\{timestamp}.json`.
2. CLI: `powerlink undo <manifest-or-date>` — reads the manifest, for
   each entry: if duplicate path is still a hardlink to canonical,
   `File.Delete(duplicate)` then `File.Copy(canonical, duplicate)`.
   This gives back an independent copy (the "undo" of the original
   dedup).
3. App: Settings page gains an "Undo log" section listing recent
   manifests with "Undo" buttons.
4. Retention: auto-prune manifests older than 30 days on App start.

**Gotchas:** once the user has modified the "duplicate" path (via any
hardlink), undoing would also propagate those modifications. Document
this: undo restores *the data that was there at dedup time only if the
hardlink is still intact and unmodified*.

---

## Real-world validation pass on `C:\W\models`

Not a code task — just use the tool for real and capture findings:
- How much space does dedup recover on the actual ML model library?
- Is partial-on-Stop useful in practice, or is the engine fast enough
  that it's not needed?
- What's the real throughput? Does buffer size matter in practice on
  the user's NVMe?
- Are there failure modes we haven't seen (permissioning, reparse
  points on model downloads, etc.)?

Write findings here when done; they inform which backlog items get
prioritised.

---

## Pre-PowerToys polish

Before proposing the module upstream:
- README.md with screenshots (GUI main view, settings page, Explorer
  menu).
- CONTRIBUTING.md with build/run instructions.
- `PowerLink.Core` as a NuGet package (no UI deps, pure library).
- Harder tests: paths >260 chars (`\\?\` prefix), locked files,
  cross-volume error surfaces, files with `ReparsePoint` attribute
  (scanner should skip).
- GitHub Actions CI: `dotnet build` + `dotnet test` on windows-latest.
- Align file layout with PowerToys' `src/modules/<name>/` convention so
  the eventual migration is a clean `git mv`.
