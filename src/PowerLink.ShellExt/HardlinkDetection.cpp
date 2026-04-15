#include "pch.h"
#include "HardlinkDetection.h"

namespace PowerLinkShellExt
{
    HardlinkCache& GlobalCache()
    {
        static HardlinkCache cache;
        return cache;
    }

    namespace
    {
        bool IsLocalFixedDrive(PCWSTR path)
        {
            if (path == nullptr) return false;
            if (path[0] == L'\\' && path[1] == L'\\') return false; // UNC
            if (path[0] == L'\0' || path[1] != L':') return false;

            wchar_t root[4] = { path[0], L':', L'\\', L'\0' };
            return GetDriveTypeW(root) == DRIVE_FIXED;
        }
    }

    HRESULT IsHardlink(PCWSTR path, DWORD fileAttributes)
    {
        if (path == nullptr) return S_FALSE;

        if (fileAttributes == INVALID_FILE_ATTRIBUTES)
        {
            fileAttributes = GetFileAttributesW(path);
            if (fileAttributes == INVALID_FILE_ATTRIBUTES) return S_FALSE;
        }

        if (fileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
            return S_FALSE;

        if (!IsLocalFixedDrive(path)) return S_FALSE;

        HANDLE h = CreateFileW(
            path,
            FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            nullptr);
        if (h == INVALID_HANDLE_VALUE) return S_FALSE;

        BY_HANDLE_FILE_INFORMATION info{};
        const BOOL ok = GetFileInformationByHandle(h, &info);
        CloseHandle(h);
        if (!ok) return S_FALSE;

        const HardlinkCacheKey key{
            info.dwVolumeSerialNumber,
            (static_cast<std::uint64_t>(info.nFileIndexHigh) << 32) | info.nFileIndexLow
        };

        bool cached = false;
        if (GlobalCache().TryGet(key, cached))
            return cached ? S_OK : S_FALSE;

        const bool isHardlink = info.nNumberOfLinks > 1;
        GlobalCache().Put(key, isHardlink);
        return isHardlink ? S_OK : S_FALSE;
    }
}
