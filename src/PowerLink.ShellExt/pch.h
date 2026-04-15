#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <objbase.h>
#include <unknwn.h>
#include <strsafe.h>

#include <atomic>
#include <mutex>
#include <list>
#include <unordered_map>
#include <cstdint>
