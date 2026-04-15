#pragma once

// {8E62D9DE-27D3-4C1E-8A55-CADF97D3EB20}
// Keep in sync with OverlayInstaller.cs.
constexpr GUID CLSID_HardlinkOverlayHandler =
    { 0x8E62D9DE, 0x27D3, 0x4C1E, { 0x8A, 0x55, 0xCA, 0xDF, 0x97, 0xD3, 0xEB, 0x20 } };

class HardlinkOverlayHandler : public IShellIconOverlayIdentifier
{
public:
    HardlinkOverlayHandler();

    // IUnknown
    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IShellIconOverlayIdentifier
    IFACEMETHODIMP IsMemberOf(PCWSTR pwszPath, DWORD dwAttrib) override;
    IFACEMETHODIMP GetOverlayInfo(PWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags) override;
    IFACEMETHODIMP GetPriority(int* pPriority) override;

private:
    ~HardlinkOverlayHandler() = default;
    std::atomic<ULONG> _refCount;
};
