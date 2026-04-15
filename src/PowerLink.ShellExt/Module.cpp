#include "pch.h"
#include "HardlinkOverlayHandler.h"
#include "DropHandler.h"
#include "ModernMenuCommand.h"

namespace
{
    using FactoryFn = IUnknown* (*)();

    IUnknown* CreateOverlay()     { return static_cast<IShellIconOverlayIdentifier*>(new (std::nothrow) HardlinkOverlayHandler()); }
    IUnknown* CreateDrop()        { return static_cast<IShellExtInit*>(new (std::nothrow) PowerLinkDropHandler()); }
    IUnknown* CreateModernRoot()  { return static_cast<IExplorerCommand*>(new (std::nothrow) ModernRootCommand()); }

    class ClassFactory : public IClassFactory
    {
    public:
        explicit ClassFactory(FactoryFn create) : _create(create), _refCount(1)
        {
            g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            return _refCount.fetch_add(1, std::memory_order_relaxed) + 1;
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG r = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
            if (r == 0)
            {
                g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
                delete this;
            }
            return r;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            *ppv = nullptr;
            if (outer != nullptr) return CLASS_E_NOAGGREGATION;

            IUnknown* instance = _create();
            if (instance == nullptr) return E_OUTOFMEMORY;

            const HRESULT hr = instance->QueryInterface(riid, ppv);
            instance->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
                g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
            else
                g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
            return S_OK;
        }

    private:
        ~ClassFactory() = default;
        FactoryFn _create;
        std::atomic<ULONG> _refCount;
    };
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;

    FactoryFn create = nullptr;
    if (rclsid == CLSID_HardlinkOverlayHandler)      create = CreateOverlay;
    else if (rclsid == CLSID_PowerLinkDropHandler)   create = CreateDrop;
    else if (rclsid == CLSID_PowerLinkModernMenu)    create = CreateModernRoot;
    else                                             return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) ClassFactory(create);
    if (factory == nullptr) return E_OUTOFMEMORY;

    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_dllRefCount.load(std::memory_order_relaxed) == 0 ? S_OK : S_FALSE;
}
