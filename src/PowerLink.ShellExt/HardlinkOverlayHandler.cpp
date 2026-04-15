#include "pch.h"
#include "HardlinkOverlayHandler.h"
#include "HardlinkDetection.h"

HardlinkOverlayHandler::HardlinkOverlayHandler() : _refCount(1)
{
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

IFACEMETHODIMP HardlinkOverlayHandler::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IShellIconOverlayIdentifier)
    {
        *ppv = static_cast<IShellIconOverlayIdentifier*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) HardlinkOverlayHandler::AddRef()
{
    return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
}

IFACEMETHODIMP_(ULONG) HardlinkOverlayHandler::Release()
{
    const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (r == 0)
    {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        delete this;
    }
    return r;
}

IFACEMETHODIMP HardlinkOverlayHandler::IsMemberOf(PCWSTR pwszPath, DWORD dwAttrib)
{
    // Shell ext development rule #1: never let an exception leave IsMemberOf.
    __try
    {
        return PowerLinkShellExt::IsHardlink(pwszPath, dwAttrib);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return S_FALSE;
    }
}

IFACEMETHODIMP HardlinkOverlayHandler::GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags)
{
    if (pwszIconFile == nullptr || pIndex == nullptr || pdwFlags == nullptr)
        return E_POINTER;

    // TODO: replace with embedded chain-link ICO resource in this DLL
    // (requires Resource.rc + assets/hardlink-overlay.ico).
    // For now: use a Windows built-in link overlay from imageres.dll — ugly,
    // but visible and proves the plumbing works.
    wchar_t system32[MAX_PATH] = {};
    if (GetSystemDirectoryW(system32, MAX_PATH) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    if (FAILED(StringCchCopyW(pwszIconFile, cchMax, system32)))
        return STRSAFE_E_INSUFFICIENT_BUFFER;
    if (FAILED(StringCchCatW(pwszIconFile, cchMax, L"\\imageres.dll")))
        return STRSAFE_E_INSUFFICIENT_BUFFER;

    *pIndex   = 154; // small link-arrow overlay
    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

IFACEMETHODIMP HardlinkOverlayHandler::GetPriority(int* pPriority)
{
    if (pPriority == nullptr) return E_POINTER;
    *pPriority = 50; // low: other overlays (OneDrive sync, etc.) win over us
    return S_OK;
}
