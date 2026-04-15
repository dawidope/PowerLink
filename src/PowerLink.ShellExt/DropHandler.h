#pragma once

#include <string>
#include <vector>

// {4BA9E8F3-7D1A-4E6C-9E3B-8F2D7C4A1B59}
// Keep in sync with ShellExtensionService.DropClsid.
constexpr GUID CLSID_PowerLinkDropHandler =
    { 0x4BA9E8F3, 0x7D1A, 0x4E6C, { 0x9E, 0x3B, 0x8F, 0x2D, 0x7C, 0x4A, 0x1B, 0x59 } };

class PowerLinkDropHandler :
    public IShellExtInit,
    public IContextMenu
{
public:
    PowerLinkDropHandler();

    // IUnknown
    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IShellExtInit
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pDataObj, HKEY hKeyProgID) override;

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU hMenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) override;
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* pici) override;
    IFACEMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uType, UINT* pReserved, CHAR* pszName, UINT cchMax) override;

private:
    ~PowerLinkDropHandler() = default;

    std::atomic<ULONG>       _refCount;
    std::wstring             _targetFolder;
    std::vector<std::wstring> _sourceFiles;
    std::vector<std::wstring> _sourceDirs;
};
