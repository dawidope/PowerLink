# Changelog

Curated, user-facing release notes. Entries follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
loosely. The release workflow extracts the section matching the pushed tag
(e.g. `v0.4.0` → `## [0.4.0]`) and uses it as the body of the GitHub release.
The auto-generated PR/commit list still appears below it on the release page.

## [0.5.0] — 2026-04-20

First release that handles **directory junctions** end-to-end. Junctions
are NTFS folder pointers — same purpose as symlinks for directories, but
unlike symlinks they need no admin and no Developer Mode. They're the
right tool for "make this folder appear at another path on the same
machine," and they're now first-class citizens in PowerLink.

### Added

- **Junction page** (new sidebar entry between Clone and Inspector).
  Pick a target folder, pick the parent folder where the junction
  appears, hit Create. The link name is auto-derived from the target's
  basename and editable. Optional "allow missing target" toggle for
  intentional dangling junctions (deploy-script pattern).
- **Inspector now lists junctions** alongside hardlinks. Two stacked
  sections — `Hardlinks (N)` and `Junctions (N, X dangling)` — both
  expandable. Each junction row shows the link path, target path, and
  status (OK / dangling). Per-row Repair (pick a new target) and Delete
  (removes only the reparse point — never touches the target).
- **Explorer integration** for junctions across all three menu sites:
  - **Classic right-click** on a folder: "PowerLink: Create junction
    pointing at this folder" (opens the App pre-filled).
  - **Classic right-click** on a folder when something is picked:
    "PowerLink: Drop as junction here" (symmetric with Drop-as-hardlink).
  - **Right-drag** a folder onto another folder: under a single
    "PowerLink" submenu, "Junction here" appears next to "Clone tree
    here (hardlinks)". Right-drag a file: "Hardlink here" alone.
    The submenu groups everything under one parent so the drop menu
    stays uncluttered next to Copy/Move/Create shortcut.
  - **Win11 modern menu** (sparse MSIX): "Drop as junction here" and
    "Create junction pointing at this..." added to the cascade.
- **CLI**: `powerlink junction {create|info|delete|repair}` and
  `powerlink drop-junction <target>`. `info` exits 0 (OK), 2 (dangling),
  1 (error). `delete` refuses non-junctions to prevent rmdir'ing a real
  directory by mistake. `repair` overwrites reparse data atomically.

### Changed

- **Drop-handler menu is now a 'PowerLink' submenu** instead of two
  top-level entries. Single visible "PowerLink >" cascades to the
  applicable actions for the current selection.
- **Inspector page renamed to "Link inspector"** internally — the title
  and intro now cover both hardlinks and junctions. Sidebar entry stays
  "Inspector" so muscle memory still works.
- **Settings → Explorer context menu** copy mentions junctions; the
  drag-and-drop description lists all three drop actions.

### Fixed (safety-critical)

- **`scan` / `clone` / `dedup` continue to skip reparse points by default
  — now locked in by tests.** Previous releases happened to do the right
  thing because `EnumerationOptions.AttributesToSkip = ReparsePoint`
  was already set, but nothing protected against a casual removal of
  that flag. Three regression tests now cover: self-referential junction
  cycles don't deadlock the scan, junctions inside a scanned tree
  aren't silently followed, and clone never hardlinks content reached
  through a junction (which would let a later dedup rewrite the
  junction's target into a hardlink to another copy — silent semantic
  corruption).

### Internal

- **`Win32Junction`** wrapper: DeviceIoControl FSCTL_SET/GET_REPARSE_POINT
  with manual `IO_REPARSE_TAG_MOUNT_POINT` buffer layout, NT-path
  `\??\` prefix for substitute name, dangling-target detection,
  long-path support, UNC-target rejection (junctions can't resolve
  network paths). 19 xUnit tests across happy/edge/error cases.
- **`JunctionScanner`**: manual recursive walk that captures
  reparse-point directories without descending into them. 4 tests
  verify cycle safety + dangling detection.
- **`QueryContextMenuHResult`** helper extracted to ShellExtUtils with
  5 new C++ tests in the framework-free test exe — locks in the
  "max offset + 1, NOT count" contract for the new `CMD_JUNCTION = 2`
  offset in the drop handler.

## [0.4.2] — 2026-04-20

Real fix for the "Clone tree here" issue v0.4.1 missed.

### Fixed

- **"Clone tree here" actually works now.** The 7-Zip dialog was not a v0.4.0
  regression as v0.4.1's revert assumed — it was a pre-existing bug in
  `PowerLinkDropHandler::QueryContextMenu`. The handler returned the *count*
  of items added (per the obvious-but-wrong reading) instead of the
  documented *largest command-id offset used, plus one*. With folder drops
  the only inserted item was at offset 1 (CMD_CLONE), but we returned 1
  instead of 2. Windows then advanced the next chained drop handler's
  `idCmdFirst` by 1 — colliding our id 4097 with the next handler's id
  4097. Visually the user saw "PowerLink: Clone tree here" but the click
  routed to whichever later handler had claimed the same id (7-Zip on the
  reporter's machine). Now we track the actual max offset used and return
  that plus one.

  Hardlink-on-file drops happened to ship working because CMD_HARDLINK
  is offset 0 and `count==1==max+1` for that case — the wrong formula
  produced the right number by coincidence.

### Added (internal)

- DropHandler now writes Debug-build diagnostic traces to
  `%TEMP%\powerlink-menu.log` (the same file ModernMenuCommand uses), with
  a `[drop]` tag. No effect on Release builds — pure no-op.

## [0.4.1] — 2026-04-20

Hotfix.

### Fixed

- **"Clone tree here" no longer pops up an unrelated app dialog.** v0.4.0
  routed this drop verb through `cmd /c start "" /b "<cli>" ...` to add
  process breakaway for an MSIX-surrogate scenario that doesn't actually
  apply to the classic drag-and-drop handler (which is hosted in
  Explorer.exe, not in `dllhost.exe`). On at least one configuration the
  `start` indirection fell through to ShellExecute and surfaced a 7-Zip
  dialog instead of launching `PowerLink.Cli`. Reverted to the direct
  `CreateProcessW` call this verb shipped with through v0.3.x.

## [0.4.0] — 2026-04-20

Cleanup batch driven by a multi-component code review. Most of the work is
internal hardening — what end users will notice is fewer silent failures
when something goes wrong.

### Fixed (safety-critical)

- **Dedup never loses a file when the hardlink step fails.** The previous
  `delete-then-link` sequence permanently removed a duplicate when
  `CreateHardLink` happened to fail (AV holding the file, permission denied,
  etc.). It now stages a rename, creates the hardlink, then deletes the
  stage; on failure the original file is restored automatically. If even
  the restore fails the staged file's path is logged and surfaced so the
  data is recoverable manually.

### Fixed (Explorer integration)

- **Long paths no longer break the modern context-menu actions.** Paths past
  ~316 characters used to silently truncate, dropping the closing quote of
  the launched command and producing a malformed cmd line that the receiving
  exe parsed wrong.
- **"Clone tree here" works on machines using the modern AppX registration.**
  The drag-and-drop CMD_CLONE path was missing the desktop-app breakaway
  attribute, so the spawned `PowerLink.Cli` inherited package identity and
  exited with `0x80070032`. Both menu sites now share the same launcher.
- **`DllCanUnloadNow` no longer reports unloadable while a lock is held.**
  An unbalanced `LockServer(FALSE)` from a defective COM client used to
  drive the global ref count below zero (signed underflow). Decrement is
  now floored at zero.
- **Long install paths are detected.** `GetModuleFileNameW` truncation now
  loops with a growing buffer instead of treating a truncated path as
  success.

### Fixed (CLI)

- **Progress output goes to stderr** — `dedup ... | tee log.txt` no longer
  interleaves hundreds of progress ticks with the structured stdout report.
  Live spinner still appears in the terminal even when stdout is redirected.
- **`scan`, `dedup`, `clone` now print a one-line error** instead of a raw
  stack trace when something escapes the engine. Exit code is `1`, or
  `130` on Ctrl+C.
- **`drop` shows progress when the picked source is a directory.** Previously
  it silently blocked until the entire clone finished.

### Fixed (install / update)

- **The post-install/post-update shell-extension junction setup is robust
  against partial failures.** A pre-existing real (non-junction) directory
  at the stable shell path now produces a descriptive error instead of an
  opaque `IOException` that aborted the Velopack install hook silently.
  Hook exceptions are caught and written to
  `%LocalAppData%\PowerLink\shell-junction-error.log` rather than killing
  the install.
- **Cold starts re-ensure the junction.** A normal launch (not first-install,
  not post-update) used to skip junction setup. If something deleted the
  junction externally the shell extension was silently dead until the next
  update.
- **CTS handle leak fixed.** Repeated scan/execute/run cycles in the App
  used to orphan the previous `CancellationTokenSource`; we now dispose
  the prior instance before allocating a new one.

### Fixed (release pipeline)

- **`vpk download` failures no longer mask shipping a broken delta.** The
  previous "swallow any non-zero exit" pattern would have produced a release
  with a missing or corrupt delta nupkg on auth/network errors. The
  workflow now pre-checks for prior releases via `gh` and treats a vpk
  download failure as a hard error when one is expected to succeed.

### Added (internal)

- **C++ utility test exe** at `tests/PowerLink.ShellExt.Tests`. Runs in CI
  before any other build step; non-zero exit fails the workflow.
- **Curated release notes via this CHANGELOG.md.** The workflow extracts
  the section matching the pushed tag and uses it as the GitHub release
  body.

---

Earlier releases (v0.3.x and below) are auto-generated from commit history
on their respective GitHub release pages.
