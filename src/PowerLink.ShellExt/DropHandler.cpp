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
    _targetFolder.clear();
    _sourceFiles.clear();
    _sourceDirs.clear();

    if (pidlFolder == nullptr || pDataObj == nullptr) return E_INVALIDARG;

    WCHAR folder[MAX_PATH]{};
    if (!SHGetPathFromIDListW(pidlFolder, folder)) return E_FAIL;
    _targetFolder = folder;

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

    if (FAILED(result)) return result;
    return (_sourceFiles.empty() && _sourceDirs.empty()) ? E_INVALIDARG : S_OK;
}

IFACEMETHODIMP PowerLinkDropHandler::QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags)
{
    if (uFlags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);

    UINT added = 0;
    UINT pos = indexMenu;

    if (!_sourceFiles.empty() && idCmdFirst + CMD_HARDLINK <= idCmdLast)
    {
        WCHAR label[64];
        if (_sourceFiles.size() == 1)
            StringCchCopyW(label, 64, L"PowerLink: Hardlink here");
        else
            StringCchPrintfW(label, 64, L"PowerLink: Hardlink %zu files here", _sourceFiles.size());

        InsertMenuW(hMenu, pos++, MF_BYPOSITION | MF_STRING, idCmdFirst + CMD_HARDLINK, label);
        added++;
    }

    if (!_sourceDirs.empty() && idCmdFirst + CMD_CLONE <= idCmdLast)
    {
        WCHAR label[64];
        if (_sourceDirs.size() == 1)
            StringCchCopyW(label, 64, L"PowerLink: Clone tree here");
        else
            StringCchPrintfW(label, 64, L"PowerLink: Clone %zu trees here", _sourceDirs.size());

        InsertMenuW(hMenu, pos++, MF_BYPOSITION | MF_STRING, idCmdFirst + CMD_CLONE, label);
        added++;
    }

    return MAKE_HRESULT(SEVERITY_SUCCESS, 0, added);
}

IFACEMETHODIMP PowerLinkDropHandler::InvokeCommand(CMINVOKECOMMANDINFO* pici)
{
    if (pici == nullptr) return E_INVALIDARG;

    if (HIWORD(pici->lpVerb) != 0) return E_INVALIDARG; // we only handle intresource verbs
    const UINT cmd = LOWORD(pici->lpVerb);
    HWND hwnd = pici->hwnd;

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
        if (_sourceDirs.empty())
        {
            ReportError(hwnd, L"PowerLink: Clone tree here", L"No folders in selection.");
            return S_OK;
        }

        const std::wstring cli = ResolveCliPath();
        if (cli.empty() || GetFileAttributesW(cli.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            ReportError(hwnd, L"PowerLink: Clone tree here",
                L"PowerLink.Cli.exe not found next to the shell extension DLL.");
            return S_OK;
        }

        // Direct CreateProcessW — DropHandler is registered for HKCU drop
        // verbs and runs inside Explorer.exe, NOT in the packaged dllhost
        // surrogate that hosts our IExplorerCommand. Routing through
        // `cmd /c start "" /b ...` here (the v0.4.0 attempt at unifying with
        // ModernMenuCommand's launcher) caused `start` to fall through to
        // ShellExecute, which on at least one test machine surfaced a 7-Zip
        // dialog instead of running the Cli. The original direct spawn
        // worked here because Explorer is unpackaged.
        size_t launched = 0;
        for (const auto& src : _sourceDirs)
        {
            std::wstring dest = _targetFolder;
            if (!dest.empty() && dest.back() != L'\\') dest += L'\\';
            dest += BaseName(src.c_str());

            std::wstring cmdline = L"\"" + cli + L"\" clone \"" + src + L"\" \"" + dest + L"\"";

            STARTUPINFOW si{ sizeof(si) };
            PROCESS_INFORMATION pi{};
            std::vector<wchar_t> buf(cmdline.begin(), cmdline.end());
            buf.push_back(L'\0');

            if (CreateProcessW(nullptr, buf.data(), nullptr, nullptr, FALSE,
                               CREATE_NEW_CONSOLE, nullptr, nullptr, &si, &pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                launched++;
            }
        }

        if (launched == 0)
            ReportError(hwnd, L"PowerLink: Clone tree here", L"Failed to launch PowerLink.Cli.");
        return S_OK;
    }

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
