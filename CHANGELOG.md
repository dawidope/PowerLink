# Changelog

Curated, user-facing release notes. Entries follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
loosely. The release workflow extracts the section matching the pushed tag
(e.g. `v0.4.0` → `## [0.4.0]`) and uses it as the body of the GitHub release.
The auto-generated PR/commit list still appears below it on the release page.

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
