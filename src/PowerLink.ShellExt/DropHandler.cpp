#include "pch.h"
#include "DropHandler.h"
#include "ShellExtUtils.h"

namespace
{
    constexpr UINT CMD_HARDLINK = 0;
    constexpr UINT CMD_CLONE    = 1;

    // Reused status-bar / accessibility strings.
    constexpr wchar_t HelpHardlink[] = L"Create NTFS hardlinks here — no extra disk space, same volume only.";
    constexpr wchar_t HelpClone[]    = L"Clone folder tree here as hardlinks via PowerLink.Cli.";

    bool EqualVolumes(PCWSTR a, PCWSTR b)
    {
        WCHAR volA[4]{}, volB[4]{};
        if (!GetVolumePathNameW(a, volA, 4)) return false;
        if (!GetVolumePathNameW(b, volB, 4)) return false;
        return _wcsicmp(volA, volB) == 0;
    }

    std::wstring BaseName(PCWSTR full)
    {
        PCWSTR last = full;
        for (PCWSTR p = full; *p; ++p)
            if (*p == L'\\' || *p == L'/') last = p + 1;
        return std::wstring(last);
    }

    std::wstring ResolveCliPath()
    {
        const std::wstring dir = PowerLink::ShellExtUtils::GetModuleDir(g_hModule);
        if (dir.empty()) return L"";
        return dir + L"PowerLink.Cli.exe";
    }

    void ReportError(HWND hwnd, PCWSTR title, PCWSTR body)
    {
        MessageBoxW(hwnd, body, title, MB_OK | MB_ICONWARNING | MB_TASKMODAL);
    }

    // Diagnostic logger — writes to %TEMP%\powerlink-menu.log so it lands in
    // the same file ModernMenuCommand already uses, with a [drop] tag so we
    // can tell the two source paths apart. Compiled into Debug builds only;
    // becomes a no-op in Release so end-user machines see no per-invoke
    // file I/O on every right-click.
#ifdef _DEBUG
    void DebugLog(const std::wstring& msg)
    {
        WCHAR temp[MAX_PATH]{};
        if (GetTempPathW(MAX_PATH, temp) == 0) return;
        const std::wstring path = std::wstring(temp) + L"powerlink-menu.log";
        HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        SetFilePointer(h, 0, nullptr, FILE_END);

        SYSTEMTIME st{};
        GetLocalTime(&st);
        WCHAR prefix[64]{};
        StringCchPrintfW(prefix, ARRAYSIZE(prefix), L"[%02d:%02d:%02d.%03d] [drop] ",
                         st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

        const std::wstring line = std::wstring(prefix) + msg + L"\r\n";
        const int cb = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(),
                                           nullptr, 0, nullptr, nullptr);
        std::vector<char> utf8((size_t)cb);
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), utf8.data(), cb, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(h, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
        CloseHandle(h);
    }
#else
    inline void DebugLog(const std::wstring&) {}
#endif
}

PowerLinkDropHandler::PowerLinkDropHandler() : _refCount(1)
{
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

IFACEMETHODIMP PowerLinkDropHandler::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IShellExtInit)
        *ppv = static_cast<IShellExtInit*>(this);
    else if (riid == IID_IContextMenu)
        *ppv = static_cast<IContextMenu*>(this);
    else
        return E_NOINTERFACE;

    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) PowerLinkDropHandler::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

IFACEMETHODIMP_(ULONG) PowerLinkDropHandler::Release()
{
    const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (r == 0)
    {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        delete this;
    }
    return r;
}

IFACEMETHODIMP PowerLinkDropHandler::Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pDataObj, HKEY /*hKeyProgID*/)
{
    DebugLog(L"Initialize entered");
    _targetFolder.clear();
    _sourceFiles.clear();
    _sourceDirs.clear();

    if (pidlFolder == nullptr || pDataObj == nullptr)
    {
        DebugLog(L"  -> null pidl or dataObj, E_INVALIDARG");
        return E_INVALIDARG;
    }

    WCHAR folder[MAX_PATH]{};
    if (!SHGetPathFromIDListW(pidlFolder, folder))
    {
        DebugLog(L"  -> SHGetPathFromIDListW failed, E_FAIL");
        return E_FAIL;
    }
    _targetFolder = folder;
    DebugLog(L"  target=" + _targetFolder);

    FORMATETC fe = { CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL };
    STGMEDIUM sm{};
    HRESULT hr = pDataObj->GetData(&fe, &sm);
    if (FAILED(hr)) return hr;

    HDROP hDrop = static_cast<HDROP>(GlobalLock(sm.hGlobal));
    if (hDrop == nullptr)
    {
        ReleaseStgMedium(&sm);
        return E_FAIL;
    }

    // emplace_back into std::wstring vectors can throw bad_alloc. A
    // propagating C++ exception out of Initialize would unwind through
    // explorer.exe's COM plumbing and crash the shell, and would also leak
    // the GlobalLock reference permanently. Catch-all + explicit cleanup
    // contains both failure modes.
    HRESULT result = S_OK;
    try
    {
        const UINT count = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
        for (UINT i = 0; i < count; ++i)
        {
            WCHAR path[MAX_PATH]{};
            if (DragQueryFileW(hDrop, i, path, MAX_PATH) == 0) continue;

            const DWORD attr = GetFileAttributesW(path);
            if (attr == INVALID_FILE_ATTRIBUTES) continue;

            if (attr & FILE_ATTRIBUTE_DIRECTORY)
                _sourceDirs.emplace_back(path);
            else
                _sourceFiles.emplace_back(path);
        }
    }
    catch (...)
    {
        _sourceFiles.clear();
        _sourceDirs.clear();
        result = E_OUTOFMEMORY;
    }

    GlobalUnlock(sm.hGlobal);
    ReleaseStgMedium(&sm);

    DebugLog(L"  files=" + std::to_wstring(_sourceFiles.size()) +
             L" dirs=" + std::to_wstring(_sourceDirs.size()));
    for (const auto& f : _sourceFiles) DebugLog(L"    file: " + f);
    for (const auto& d : _sourceDirs)  DebugLog(L"    dir:  " + d);

    if (FAILED(result)) { DebugLog(L"  -> failed result"); return result; }
    if (_sourceFiles.empty() && _sourceDirs.empty())
    {
        DebugLog(L"  -> no sources, E_INVALIDARG");
        return E_INVALIDARG;
    }
    return S_OK;
}

IFACEMETHODIMP PowerLinkDropHandler::QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags)
{
    DebugLog(L"QueryContextMenu uFlags=0x" + std::to_wstring(uFlags) +
             L" idCmdFirst=" + std::to_wstring(idCmdFirst) +
             L" idCmdLast=" + std::to_wstring(idCmdLast));
    if (uFlags & CMF_DEFAULTONLY)
    {
        DebugLog(L"  -> CMF_DEFAULTONLY, returning 0 added");
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
    }

    // Per MSDN, the return value reports the largest *offset* we used relative
    // to idCmdFirst, plus one — NOT the count of items added. The previous
    // "return added" pattern accidentally worked when we only added
    // CMD_HARDLINK (offset 0, count 1 == max_offset+1) but silently broke for
    // folder drops where we add only CMD_CLONE (offset 1, count 1, but the
    // contract requires us to return 2). The wrong return made Windows
    // advance the next handler's idCmdFirst by 1 instead of 2, producing an
    // ID collision: the next handler (7-Zip on the test machine) reused
    // id 4097 and Windows routed clicks on our visible "Clone tree here"
    // label to 7-Zip's InvokeCommand.
    UINT maxUsedOffsetPlusOne = 0;
    UINT pos = indexMenu;

    auto markUsed = [&](UINT offset) {
        const UINT plusOne = offset + 1;
        if (plusOne > maxUsedOffsetPlusOne) maxUsedOffsetPlusOne = plusOne;
    };

    if (!_sourceFiles.empty() && idCmdFirst + CMD_HARDLINK <= idCmdLast)
    {
        WCHAR label[64];
        if (_sourceFiles.size() == 1)
            StringCchCopyW(label, 64, L"PowerLink: Hardlink here");
        else
            StringCchPrintfW(label, 64, L"PowerLink: Hardlink %zu files here", _sourceFiles.size());

        InsertMenuW(hMenu, pos++, MF_BYPOSITION | MF_STRING, idCmdFirst + CMD_HARDLINK, label);
        markUsed(CMD_HARDLINK);
        DebugLog(L"  inserted CMD_HARDLINK at id=" + std::to_wstring(idCmdFirst + CMD_HARDLINK));
    }

    if (!_sourceDirs.empty() && idCmdFirst + CMD_CLONE <= idCmdLast)
    {
        WCHAR label[64];
        if (_sourceDirs.size() == 1)
            StringCchCopyW(label, 64, L"PowerLink: Clone tree here");
        else
            StringCchPrintfW(label, 64, L"PowerLink: Clone %zu trees here", _sourceDirs.size());

        InsertMenuW(hMenu, pos++, MF_BYPOSITION | MF_STRING, idCmdFirst + CMD_CLONE, label);
        markUsed(CMD_CLONE);
        DebugLog(L"  inserted CMD_CLONE at id=" + std::to_wstring(idCmdFirst + CMD_CLONE));
    }

    DebugLog(L"  -> maxUsedOffsetPlusOne=" + std::to_wstring(maxUsedOffsetPlusOne));
    return MAKE_HRESULT(SEVERITY_SUCCESS, 0, maxUsedOffsetPlusOne);
}

IFACEMETHODIMP PowerLinkDropHandler::InvokeCommand(CMINVOKECOMMANDINFO* pici)
{
    DebugLog(L"InvokeCommand entered");
    if (pici == nullptr) { DebugLog(L"  -> null pici"); return E_INVALIDARG; }

    if (HIWORD(pici->lpVerb) != 0)
    {
        DebugLog(L"  -> string verb (HIWORD non-zero), E_INVALIDARG");
        return E_INVALIDARG;
    }
    const UINT cmd = LOWORD(pici->lpVerb);
    HWND hwnd = pici->hwnd;
    DebugLog(L"  cmd=" + std::to_wstring(cmd));

    if (cmd == CMD_HARDLINK)
    {
        if (_sourceFiles.empty())
        {
            ReportError(hwnd, L"PowerLink: Hardlink here", L"No files in selection.");
            return S_OK;
        }

        size_t ok = 0;
        std::wstring failures;
        for (const auto& src : _sourceFiles)
        {
            if (!EqualVolumes(src.c_str(), _targetFolder.c_str()))
            {
                failures += L"\n  " + src + L"  (different volume — hardlinks can't cross volumes)";
                continue;
            }

            std::wstring linkPath = _targetFolder;
            if (!linkPath.empty() && linkPath.back() != L'\\') linkPath += L'\\';
            linkPath += BaseName(src.c_str());

            if (GetFileAttributesW(linkPath.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                failures += L"\n  " + linkPath + L"  (already exists)";
                continue;
            }

            if (CreateHardLinkW(linkPath.c_str(), src.c_str(), nullptr))
            {
                ok++;
            }
            else
            {
                WCHAR msg[256];
                StringCchPrintfW(msg, 256, L"\n  %s  (error %lu)", src.c_str(), GetLastError());
                failures += msg;
            }
        }

        if (!failures.empty())
        {
            std::wstring body = L"Hardlinks created: " + std::to_wstring(ok) + L"\nFailed:" + failures;
            ReportError(hwnd, L"PowerLink: Hardlink here", body.c_str());
        }
        // no message on full success — shell-level operation should be quiet like Copy/Move
        return S_OK;
    }

    if (cmd == CMD_CLONE)
    {
        DebugLog(L"  CMD_CLONE branch");
        if (_sourceDirs.empty())
        {
            DebugLog(L"    no source dirs");
            ReportError(hwnd, L"PowerLink: Clone tree here", L"No folders in selection.");
            return S_OK;
        }

        const std::wstring cli = ResolveCliPath();
        DebugLog(L"    resolved cli path: '" + cli + L"'");
        const DWORD cliAttrs = cli.empty() ? INVALID_FILE_ATTRIBUTES : GetFileAttributesW(cli.c_str());
        DebugLog(L"    cli GetFileAttributesW=0x" + std::to_wstring(cliAttrs));
        if (cli.empty() || cliAttrs == INVALID_FILE_ATTRIBUTES)
        {
            DebugLog(L"    -> cli not found, returning");
            ReportError(hwnd, L"PowerLink: Clone tree here",
                L"PowerLink.Cli.exe not found next to the shell extension DLL.");
            return S_OK;
        }

        // Direct CreateProcessW — DropHandler is registered for HKCU drop
        // verbs and runs inside Explorer.exe, NOT in the packaged dllhost
        // surrogate that hosts our IExplorerCommand.
        size_t launched = 0;
        for (const auto& src : _sourceDirs)
        {
            std::wstring dest = _targetFolder;
            if (!dest.empty() && dest.back() != L'\\') dest += L'\\';
            dest += BaseName(src.c_str());

            std::wstring cmdline = L"\"" + cli + L"\" clone \"" + src + L"\" \"" + dest + L"\"";
            DebugLog(L"    spawning: " + cmdline);

            STARTUPINFOW si{ sizeof(si) };
            PROCESS_INFORMATION pi{};
            std::vector<wchar_t> buf(cmdline.begin(), cmdline.end());
            buf.push_back(L'\0');

            const BOOL ok = CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE,
                                           CREATE_NEW_CONSOLE, nullptr, nullptr, &si, &pi);
            const DWORD lastErr = GetLastError();
            DebugLog(L"      CreateProcessW ok=" + std::to_wstring(ok) +
                     L" GetLastError=" + std::to_wstring(lastErr));
            if (ok)
            {
                DebugLog(L"      pid=" + std::to_wstring(pi.dwProcessId));
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                launched++;
            }
        }

        DebugLog(L"    launched=" + std::to_wstring(launched));
        if (launched == 0)
            ReportError(hwnd, L"PowerLink: Clone tree here", L"Failed to launch PowerLink.Cli.");
        return S_OK;
    }

    DebugLog(L"  -> unknown cmd, E_INVALIDARG");
    return E_INVALIDARG;
}

IFACEMETHODIMP PowerLinkDropHandler::GetCommandString(UINT_PTR idCmd, UINT uType, UINT* /*pReserved*/, CHAR* pszName, UINT cchMax)
{
    if (uType != GCS_HELPTEXTW && uType != GCS_HELPTEXTA) return E_NOTIMPL;

    PCWSTR help = nullptr;
    if (idCmd == CMD_HARDLINK)      help = HelpHardlink;
    else if (idCmd == CMD_CLONE)    help = HelpClone;
    else return E_INVALIDARG;

    if (uType == GCS_HELPTEXTW)
        return StringCchCopyW(reinterpret_cast<PWSTR>(pszName), cchMax, help);

    // ANSI fallback.
    return StringCchPrintfA(pszName, cchMax, "%ls", help);
}
