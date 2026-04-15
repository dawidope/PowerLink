#include "pch.h"
#include "HardlinkOverlayHandler.h"
#include "HardlinkDetection.h"
#include "resource.h"

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

    // Icon is embedded in this DLL (Resource.rc -> ../PowerLink.App/Assets/Icon.ico).
    // Negative index = -ResourceID; ExtractIcon convention.
    if (GetModuleFileNameW(g_hModule, pwszIconFile, static_cast<DWORD>(cchMax)) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    *pIndex   = -IDI_HARDLINK_OVERLAY;
    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

IFACEMETHODIMP HardlinkOverlayHandler::GetPriority(int* pPriority)
{
    if (pPriority == nullptr) return E_POINTER;
    *pPriority = 50; // low: other overlays (OneDrive sync, etc.) win over us
    return S_OK;
}
