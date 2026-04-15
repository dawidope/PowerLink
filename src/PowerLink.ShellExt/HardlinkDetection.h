#pragma once

#include "HardlinkCache.h"

namespace PowerLinkShellExt
{
    HardlinkCache& GlobalCache();

    // Returns S_OK if `path` is a hardlinked file (nNumberOfLinks > 1),
    // S_FALSE if regular/directory/UNC/reparse/etc.
    // Never throws; any failure => S_FALSE.
    HRESULT IsHardlink(PCWSTR path, DWORD fileAttributes);
}
