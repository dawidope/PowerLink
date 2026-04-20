#pragma once

// Pure utility functions extracted from the COM source files so they can be
// exercised from a small standalone test exe (tests/PowerLink.ShellExt.Tests).
// Nothing in here may take a dependency on the DLL's COM globals or HMODULE
// state — pass everything in by argument.
//
// Header is intentionally light on includes so the test exe doesn't have to
// drag in the full pch.

#include <windows.h>
#include <atomic>
#include <string>

namespace PowerLink::ShellExtUtils
{
    // Format a single-string argument into a printf-style template (e.g.
    // L"pick \"%s\""). Used by the modern context-menu Invoke handler to
    // build the cmd line passed to PowerLink.Cli / PowerLink.App.
    std::wstring FormatArgs(PCWSTR tmpl, PCWSTR path);

    // Returns the directory containing the DLL (with a trailing separator),
    // or an empty string on failure. Replaces the per-call stack buffer
    // pattern in DllDir() / ResolveCliPath().
    std::wstring GetModuleDir(HMODULE module);

    // Decrement an atomic ref-count safely. Returns the new value. If the
    // counter was already at zero (no matching increment), this is a no-op
    // and returns 0 — a defective COM client unbalancing LockServer must
    // not be able to drive the count negative or wrap around.
    LONG SafeDecrement(std::atomic<LONG>& counter);

    // IEnumXxx::Skip semantics: advance `index` by up to `celt` positions
    // toward `total`. Returns S_OK if the full skip succeeded, S_FALSE if
    // we hit the end before completing it. Always clamps `index` to
    // `total`.
    HRESULT ClampedSkip(size_t& index, size_t total, size_t celt);

    // Spawn `exe args` via cmd.exe /c start with PROCESS_CREATION_DESKTOP_
    // APP_BREAKAWAY_ENABLE_PROCESS_TREE. Required when the calling DLL is
    // hosted in a packaged MSIX surrogate (dllhost.exe with our AppX
    // identity): a plain CreateProcessW would inherit the package identity
    // and unpackaged children (PowerLink.App.exe, PowerLink.Cli.exe) would
    // fail with 0x80070032. Returns true on successful spawn (does not
    // wait for the child to exit).
    bool LaunchProcessWithBreakaway(
        const std::wstring& exe,
        const std::wstring& args,
        const std::wstring& workDir);
}
