#include "pch.h"
#include "ModernMenuCommand.h"
#include "ShellExtUtils.h"

namespace
{
    enum class ActionTargets { AnyFileOrFolder, FileOnly, FolderOnly };

    struct ActionInfo
    {
        PCWSTR title;
        PCWSTR tooltip;
        bool   useCli;         // true → PowerLink.Cli.exe, false → PowerLink.App.exe
        PCWSTR argTemplate;    // printf-style, %s = single quoted path
        bool   perItem;        // spawn once per selected path (Pick), else once for the first path
        ActionTargets targets; // hides the sub-command when the selection type doesn't fit
    };

    constexpr ModernAction kAllActions[] = {
        ModernAction::Pick,
        ModernAction::Drop,
        ModernAction::DropJunction,
        ModernAction::ShowLinks,
        ModernAction::Inspect,
        ModernAction::Dedup,
        ModernAction::Clone,
        ModernAction::Junction,
    };

    const ActionInfo& Info(ModernAction a)
    {
        static const ActionInfo infos[] = {
            { L"Pick as link source",      L"Remember this path as the source for a later Drop",                true,  L"pick \"%s\"",          true,  ActionTargets::AnyFileOrFolder },
            { L"Drop as hardlink here",    L"Make a hardlink (file) or a tree of hardlinks (folder) from the picked source", true,  L"drop \"%s\"",          false, ActionTargets::FolderOnly },
            { L"Drop as junction here",    L"Create a junction here pointing at the picked folder",             true,  L"drop-junction \"%s\"", false, ActionTargets::FolderOnly },
            { L"Show hardlinks",           L"List every path on this volume sharing this file's data",          true,  L"show-links \"%s\"",    false, ActionTargets::FileOnly },
            { L"Inspect for hardlinks",    L"Open the Inspector with this folder",                              false, L"--inspect \"%s\"",     false, ActionTargets::FolderOnly },
            { L"Deduplicate folder",       L"Open the Deduplicate page with this folder added",                 false, L"--dedup \"%s\"",       false, ActionTargets::FolderOnly },
            { L"Clone folder (hardlinks)", L"Mirror folder tree as hardlinks — same volume only",               false, L"--clone \"%s\"",       false, ActionTargets::FolderOnly },
            { L"Create junction pointing at this...", L"Open the Junction page with this folder pre-filled as the target", false, L"--junction \"%s\"",    false, ActionTargets::FolderOnly },
        };
        return infos[static_cast<size_t>(a)];
    }

    HRESULT AllocString(PCWSTR src, LPWSTR* out)
    {
        if (out == nullptr) return E_POINTER;
        *out = nullptr;
        return SHStrDupW(src, out);
    }

    std::wstring DllDir()
    {
        return PowerLink::ShellExtUtils::GetModuleDir(g_hModule);
    }

    bool SelectionPaths(IShellItemArray* arr, std::vector<std::wstring>& out)
    {
        if (arr == nullptr) return false;
        DWORD count = 0;
        if (FAILED(arr->GetCount(&count)) || count == 0) return false;

        for (DWORD i = 0; i < count; ++i)
        {
            IShellItem* item = nullptr;
            if (FAILED(arr->GetItemAt(i, &item)) || item == nullptr) continue;
            LPWSTR p = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p != nullptr)
            {
                out.emplace_back(p);
                CoTaskMemFree(p);
            }
            item->Release();
        }
        return !out.empty();
    }

    // Append a line to %TEMP%\powerlink-menu.log. Only compiled in Debug builds
    // (PowerLink.ShellExt.vcxproj defines _DEBUG there) — in Release this
    // collapses to an empty inline function so there's no per-invoke file I/O
    // or context-menu overhead on end-user machines.
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
        StringCchPrintfW(prefix, ARRAYSIZE(prefix), L"[%02d:%02d:%02d.%03d] ",
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

    // Our DLL is hosted in dllhost.exe with our AppX package identity (via
    // com:SurrogateServer in the manifest). Any child spawned with a plain
    // CreateProcessW inherits that identity — fine for our console Cli, fatal
    // for PowerLink.App.exe (unpackaged WinUI) because the activation path for
    // a packaged WinUI app is wildly different and the process silently exits.
    //
    // PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = DESKTOP_APP_BREAKAWAY tells
    // Windows to strip package identity from the child and its entire tree,
    // producing a normal desktop process. This is Microsoft's documented fix
    // for "unpackaged app spawned from packaged host".
    // Thin wrapper around the shared spawn helper that adds DebugLog-only
    // tracing. The actual breakaway / CreateProcess plumbing lives in
    // ShellExtUtils so DropHandler can use the same code path (it has the
    // same packaged-surrogate identity-leak problem when starting CLI).
    bool LaunchExe(const std::wstring& exe, const std::wstring& args, const std::wstring& workDir)
    {
        const bool ok = PowerLink::ShellExtUtils::LaunchProcessWithBreakaway(exe, args, workDir);
        if (!ok)
        {
            WCHAR b[256]{};
            StringCchPrintfW(b, ARRAYSIZE(b),
                L"LaunchProcessWithBreakaway FAILED: exe=%s err=%lu", exe.c_str(), GetLastError());
            DebugLog(b);
        }
        return ok;
    }

    std::wstring FormatArgs(PCWSTR tmpl, PCWSTR path)
    {
        return PowerLink::ShellExtUtils::FormatArgs(tmpl, path);
    }

    // Shared icon for the root + every sub-command. Lives next to the DLL
    // (Assets\Icon.ico is copied there by the App build). Returning it via
    // GetIcon instead of E_NOTIMPL fills in the blank menu slot.
    HRESULT AllocIconPath(LPWSTR* out)
    {
        if (out == nullptr) return E_POINTER;
        *out = nullptr;
        const std::wstring dir = DllDir();
        if (dir.empty()) return E_FAIL;
        const std::wstring ico = dir + L"Assets\\Icon.ico";
        if (GetFileAttributesW(ico.c_str()) == INVALID_FILE_ATTRIBUTES) return E_FAIL;
        return SHStrDupW(ico.c_str(), out);
    }
}

// ================= ModernSubCommand =================

ModernSubCommand::ModernSubCommand(ModernAction action) : _refCount(1), _action(action)
{
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

IFACEMETHODIMP ModernSubCommand::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        *ppv = static_cast<IExplorerCommand*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) ModernSubCommand::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

IFACEMETHODIMP_(ULONG) ModernSubCommand::Release()
{
    const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (r == 0)
    {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        delete this;
    }
    return r;
}

IFACEMETHODIMP ModernSubCommand::GetTitle(IShellItemArray*, LPWSTR* ppszName)
{
    return AllocString(Info(_action).title, ppszName);
}

IFACEMETHODIMP ModernSubCommand::GetIcon(IShellItemArray*, LPWSTR* ppszIcon)
{
    return AllocIconPath(ppszIcon);
}

IFACEMETHODIMP ModernSubCommand::GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip)
{
    return AllocString(Info(_action).tooltip, ppszInfotip);
}

IFACEMETHODIMP ModernSubCommand::GetCanonicalName(GUID* pguidCommandName)
{
    if (pguidCommandName == nullptr) return E_POINTER;
    *pguidCommandName = GUID_NULL;
    return S_OK;
}

IFACEMETHODIMP ModernSubCommand::GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* pCmdState)
{
    if (pCmdState == nullptr) return E_POINTER;
    *pCmdState = ECS_ENABLED;

    const ActionTargets want = Info(_action).targets;
    if (want == ActionTargets::AnyFileOrFolder) return S_OK;

    // Background click on a folder passes an IShellItemArray containing the
    // folder itself, so it naturally counts as FolderOnly-eligible. An empty
    // or null array (rare) — default-hide for file-only, default-show for
    // folder-only, matching intuition.
    DWORD count = 0;
    if (items == nullptr || FAILED(items->GetCount(&count)) || count == 0)
    {
        if (want == ActionTargets::FileOnly) *pCmdState = ECS_HIDDEN;
        return S_OK;
    }

    IShellItem* first = nullptr;
    if (FAILED(items->GetItemAt(0, &first)) || first == nullptr) return S_OK;

    SFGAOF attribs = 0;
    const HRESULT hr = first->GetAttributes(SFGAO_FOLDER, &attribs);
    first->Release();
    if (FAILED(hr)) return S_OK;

    const bool isFolder = (attribs & SFGAO_FOLDER) != 0;
    if (want == ActionTargets::FolderOnly && !isFolder) *pCmdState = ECS_HIDDEN;
    if (want == ActionTargets::FileOnly && isFolder)    *pCmdState = ECS_HIDDEN;
    return S_OK;
}

IFACEMETHODIMP ModernSubCommand::Invoke(IShellItemArray* items, IBindCtx*)
{
    const ActionInfo& info = Info(_action);

    std::vector<std::wstring> paths;
    const bool gotPaths = SelectionPaths(items, paths);

    {
        WCHAR hdr[256]{};
        StringCchPrintfW(hdr, ARRAYSIZE(hdr), L"Invoke action=%s itemsPtr=%p paths=%zu",
                         info.title, items, paths.size());
        DebugLog(hdr);
        for (const auto& p : paths) DebugLog(L"  path: " + p);
    }

    if (!gotPaths)
    {
        DebugLog(L"  -> no paths, returning S_OK");
        return S_OK;
    }

    const std::wstring dir = DllDir();
    if (dir.empty()) { DebugLog(L"  -> DllDir empty, E_FAIL"); return E_FAIL; }

    const std::wstring exe = dir + (info.useCli ? L"PowerLink.Cli.exe" : L"PowerLink.App.exe");
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        DebugLog(L"  -> exe missing: " + exe);
        return E_FAIL;
    }

    DebugLog(L"  spawn: " + exe);
    if (info.perItem)
    {
        for (const auto& p : paths)
            LaunchExe(exe, FormatArgs(info.argTemplate, p.c_str()), dir);
    }
    else
    {
        LaunchExe(exe, FormatArgs(info.argTemplate, paths.front().c_str()), dir);
    }
    return S_OK;
}

IFACEMETHODIMP ModernSubCommand::GetFlags(EXPCMDFLAGS* pFlags)
{
    if (pFlags == nullptr) return E_POINTER;
    *pFlags = ECF_DEFAULT;
    return S_OK;
}

IFACEMETHODIMP ModernSubCommand::EnumSubCommands(IEnumExplorerCommand** ppEnum)
{
    if (ppEnum == nullptr) return E_POINTER;
    *ppEnum = nullptr;
    return E_NOTIMPL;
}

// ================= ModernSubCommandEnum =================

ModernSubCommandEnum::ModernSubCommandEnum() : _refCount(1), _index(0)
{
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

IFACEMETHODIMP ModernSubCommandEnum::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IEnumExplorerCommand)
        *ppv = static_cast<IEnumExplorerCommand*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) ModernSubCommandEnum::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

IFACEMETHODIMP_(ULONG) ModernSubCommandEnum::Release()
{
    const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (r == 0)
    {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        delete this;
    }
    return r;
}

IFACEMETHODIMP ModernSubCommandEnum::Next(ULONG celt, IExplorerCommand** rgelt, ULONG* pceltFetched)
{
    if (rgelt == nullptr) return E_POINTER;
    constexpr size_t total = ARRAYSIZE(kAllActions);
    ULONG fetched = 0;
    for (ULONG i = 0; i < celt && _index < total; ++i, ++_index)
    {
        auto* sub = new (std::nothrow) ModernSubCommand(kAllActions[_index]);
        if (sub == nullptr) break;
        rgelt[fetched++] = static_cast<IExplorerCommand*>(sub);
    }
    if (pceltFetched) *pceltFetched = fetched;
    return (fetched == celt) ? S_OK : S_FALSE;
}

IFACEMETHODIMP ModernSubCommandEnum::Skip(ULONG celt)
{
    constexpr size_t total = ARRAYSIZE(kAllActions);
    return PowerLink::ShellExtUtils::ClampedSkip(_index, total, celt);
}

IFACEMETHODIMP ModernSubCommandEnum::Reset()
{
    _index = 0;
    return S_OK;
}

IFACEMETHODIMP ModernSubCommandEnum::Clone(IEnumExplorerCommand** ppenum)
{
    if (ppenum == nullptr) return E_POINTER;
    *ppenum = nullptr;
    return E_NOTIMPL;
}

// ================= ModernRootCommand =================

ModernRootCommand::ModernRootCommand() : _refCount(1)
{
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

IFACEMETHODIMP ModernRootCommand::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        *ppv = static_cast<IExplorerCommand*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) ModernRootCommand::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

IFACEMETHODIMP_(ULONG) ModernRootCommand::Release()
{
    const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (r == 0)
    {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        delete this;
    }
    return r;
}

IFACEMETHODIMP ModernRootCommand::GetTitle(IShellItemArray*, LPWSTR* ppszName)
{
    return AllocString(L"PowerLink", ppszName);
}

IFACEMETHODIMP ModernRootCommand::GetIcon(IShellItemArray*, LPWSTR* ppszIcon)
{
    return AllocIconPath(ppszIcon);
}

IFACEMETHODIMP ModernRootCommand::GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip)
{
    if (ppszInfotip == nullptr) return E_POINTER;
    *ppszInfotip = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP ModernRootCommand::GetCanonicalName(GUID* pguidCommandName)
{
    if (pguidCommandName == nullptr) return E_POINTER;
    *pguidCommandName = GUID_NULL;
    return S_OK;
}

IFACEMETHODIMP ModernRootCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState)
{
    if (pCmdState == nullptr) return E_POINTER;
    *pCmdState = ECS_ENABLED;
    return S_OK;
}

IFACEMETHODIMP ModernRootCommand::Invoke(IShellItemArray*, IBindCtx*)
{
    // Container command — the shell opens the submenu without invoking us.
    return S_OK;
}

IFACEMETHODIMP ModernRootCommand::GetFlags(EXPCMDFLAGS* pFlags)
{
    if (pFlags == nullptr) return E_POINTER;
    *pFlags = ECF_HASSUBCOMMANDS;
    return S_OK;
}

IFACEMETHODIMP ModernRootCommand::EnumSubCommands(IEnumExplorerCommand** ppEnum)
{
    if (ppEnum == nullptr) return E_POINTER;
    auto* e = new (std::nothrow) ModernSubCommandEnum();
    if (e == nullptr) { *ppEnum = nullptr; return E_OUTOFMEMORY; }
    *ppEnum = static_cast<IEnumExplorerCommand*>(e);
    return S_OK;
}
