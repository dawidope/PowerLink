#include "ShellExtUtils.h"
#include <vector>

// Pull in process-creation headers only here so the test exe linking against
// ShellExtUtils.cpp doesn't need the wider Win32 surface.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

namespace PowerLink::ShellExtUtils
{
    std::wstring FormatArgs(PCWSTR tmpl, PCWSTR path)
    {
        // Single-arg %s template, but we'd rather not parse printf format
        // strings ourselves. Walk the template, copy literal chars, and
        // substitute the path at the first %s. Avoids the previous fixed-
        // size stack buffer that silently truncated long paths and dropped
        // the template's closing quote.
        std::wstring out;
        if (tmpl == nullptr) return out;
        const size_t pathLen = path ? std::char_traits<wchar_t>::length(path) : 0;
        out.reserve(std::char_traits<wchar_t>::length(tmpl) + pathLen);

        for (PCWSTR p = tmpl; *p; ++p)
        {
            if (*p == L'%' && *(p + 1) == L's')
            {
                if (path) out.append(path, pathLen);
                ++p; // skip 's'
                continue;
            }
            out.push_back(*p);
        }
        return out;
    }

    std::wstring GetModuleDir(HMODULE module)
    {
        // Loop with a growing buffer. GetModuleFileNameW signals truncation
        // by returning exactly nSize and setting ERROR_INSUFFICIENT_BUFFER;
        // the original implementation treated any non-zero return as success
        // and produced silently-truncated paths on long install locations.
        std::vector<wchar_t> buf(MAX_PATH);
        for (;;)
        {
            SetLastError(ERROR_SUCCESS);
            const DWORD copied = GetModuleFileNameW(module, buf.data(), static_cast<DWORD>(buf.size()));
            if (copied == 0) return L"";

            if (copied < buf.size() - 1 || GetLastError() != ERROR_INSUFFICIENT_BUFFER)
            {
                std::wstring path(buf.data(), copied);
                const size_t sep = path.find_last_of(L"\\/");
                if (sep == std::wstring::npos) return L"";
                return path.substr(0, sep + 1);
            }

            // Truncated — grow and retry. Cap at 64K to bound runaway loops
            // on unexpected API failures.
            if (buf.size() >= 65536) return L"";
            buf.resize(buf.size() * 2);
        }
    }

    LONG SafeDecrement(std::atomic<LONG>& counter)
    {
        // Compare-exchange loop so we never drive the counter below zero.
        // A defective COM client unbalancing LockServer can no longer wrap
        // the count or trigger signed-underflow UB — the spurious
        // decrement is a no-op.
        LONG current = counter.load(std::memory_order_acquire);
        while (current > 0)
        {
            if (counter.compare_exchange_weak(
                    current, current - 1,
                    std::memory_order_acq_rel,
                    std::memory_order_acquire))
            {
                return current - 1;
            }
        }
        return 0;
    }

    HRESULT ClampedSkip(size_t& index, size_t total, size_t celt)
    {
        // OLE IEnumXxx::Skip contract: S_OK if the full skip succeeded,
        // S_FALSE if we ran out before completing it. Clamp `index` to
        // `total` either way so the next Next() call sees a sane state.
        if (index >= total)
        {
            index = total;
            return celt == 0 ? S_OK : S_FALSE;
        }
        const size_t remaining = total - index;
        if (celt > remaining)
        {
            index = total;
            return S_FALSE;
        }
        index += celt;
        return S_OK;
    }

    bool LaunchProcessWithBreakaway(
        const std::wstring& exe,
        const std::wstring& args,
        const std::wstring& workDir)
    {
        WCHAR systemDir[MAX_PATH]{};
        GetSystemDirectoryW(systemDir, MAX_PATH);
        std::wstring cmdExe = std::wstring(systemDir) + L"\\cmd.exe";

        // `cmd /c start "" /b "<exe>" <args>` — empty title placeholder
        // (otherwise `start` consumes the first quoted arg as its title),
        // /b suppresses a new console window.
        std::wstring cmdline = L"cmd.exe /c start \"\" /b \"" + exe + L"\" " + args;
        std::vector<wchar_t> cmdBuf(cmdline.begin(), cmdline.end());
        cmdBuf.push_back(L'\0');

        SIZE_T attrSize = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attrSize);
        std::vector<BYTE> attrBuf(attrSize);
        auto* attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrBuf.data());

        bool attrsReady = false;
        DWORD policy = PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE;
        if (InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize) &&
            UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY,
                                      &policy, sizeof(policy), nullptr, nullptr))
        {
            attrsReady = true;
        }

        STARTUPINFOEXW siex{};
        siex.StartupInfo.cb = sizeof(siex);
        if (attrsReady) siex.lpAttributeList = attrs;

        DWORD flags = CREATE_NO_WINDOW;
        if (attrsReady) flags |= EXTENDED_STARTUPINFO_PRESENT;

        PROCESS_INFORMATION pi{};
        const BOOL ok = CreateProcessW(
            cmdExe.c_str(), cmdBuf.data(), nullptr, nullptr, FALSE,
            flags, nullptr,
            workDir.empty() ? nullptr : workDir.c_str(),
            reinterpret_cast<LPSTARTUPINFOW>(&siex), &pi);

        if (attrsReady) DeleteProcThreadAttributeList(attrs);

        if (!ok) return false;

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }

    HRESULT QueryContextMenuHResult(UINT maxOffsetUsed, bool anyInserted)
    {
        return anyInserted
            ? MAKE_HRESULT(SEVERITY_SUCCESS, 0, maxOffsetUsed + 1)
            : MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
    }
}
