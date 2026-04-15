#pragma once

// {E8A4F2B1-9D3C-4E7F-B5A8-1F2D3C4E5B6A}
// Root IExplorerCommand for Windows 11 top-section ("modern") menu.
// Keep in sync with <com:Class Id="..."> in AppxManifest.xml.
constexpr GUID CLSID_PowerLinkModernMenu =
    { 0xE8A4F2B1, 0x9D3C, 0x4E7F, { 0xB5, 0xA8, 0x1F, 0x2D, 0x3C, 0x4E, 0x5B, 0x6A } };

enum class ModernAction
{
    Pick,
    Drop,
    ShowLinks,
    Inspect,
    Dedup,
    Clone,
};

class ModernSubCommand : public IExplorerCommand
{
public:
    explicit ModernSubCommand(ModernAction action);

    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* ppszName) override;
    IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* ppszIcon) override;
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, LPWSTR* ppszInfotip) override;
    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override;
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL fOkToBeSlow, EXPCMDSTATE* pCmdState) override;
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* pbc) override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override;

private:
    ~ModernSubCommand() = default;
    std::atomic<ULONG> _refCount;
    ModernAction _action;
};

class ModernSubCommandEnum : public IEnumExplorerCommand
{
public:
    ModernSubCommandEnum();

    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** rgelt, ULONG* pceltFetched) override;
    IFACEMETHODIMP Skip(ULONG celt) override;
    IFACEMETHODIMP Reset() override;
    IFACEMETHODIMP Clone(IEnumExplorerCommand** ppenum) override;

private:
    ~ModernSubCommandEnum() = default;
    std::atomic<ULONG> _refCount;
    size_t _index;
};

class ModernRootCommand : public IExplorerCommand
{
public:
    ModernRootCommand();

    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* ppszName) override;
    IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* ppszIcon) override;
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, LPWSTR* ppszInfotip) override;
    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override;
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL fOkToBeSlow, EXPCMDSTATE* pCmdState) override;
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* pbc) override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override;

private:
    ~ModernRootCommand() = default;
    std::atomic<ULONG> _refCount;
};
