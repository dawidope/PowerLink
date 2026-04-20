// Minimal test harness for PowerLink.ShellExt utility helpers. No xUnit / no
// GoogleTest — keeping it framework-free keeps the build matrix simple. Each
// test prints PASS/FAIL with a short reason; the exe returns the count of
// failures so CI can fail the step on a non-zero exit.

#include "../../src/PowerLink.ShellExt/ShellExtUtils.h"

#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <string>

namespace
{
    int g_failures = 0;
    int g_passes = 0;

    void Check(bool condition, const char* name, const std::string& details = "")
    {
        if (condition)
        {
            std::printf("[PASS] %s\n", name);
            ++g_passes;
        }
        else
        {
            std::printf("[FAIL] %s%s%s\n", name,
                        details.empty() ? "" : " — ",
                        details.c_str());
            ++g_failures;
        }
    }

    std::string Narrow(const std::wstring& w)
    {
        std::string s;
        s.reserve(w.size());
        for (wchar_t c : w)
            s.push_back(c < 128 ? static_cast<char>(c) : '?');
        return s;
    }

    // ===== P0 #2 — FormatArgs truncation =====

    void Test_FormatArgs_ShortPath_NoTruncation()
    {
        const std::wstring out = PowerLink::ShellExtUtils::FormatArgs(
            L"pick \"%s\"", L"C:\\short\\path.txt");
        Check(out == L"pick \"C:\\short\\path.txt\"",
              "FormatArgs short path produces expected literal",
              "got: " + Narrow(out));
    }

    void Test_FormatArgs_LongPath_PreservesClosingQuote()
    {
        // Build a path long enough to overflow the original 324-WCHAR
        // stack buffer. 400 chars + the 8-char template overhead = ~408,
        // well past 324.
        std::wstring longPath(400, L'a');
        longPath = L"C:\\" + longPath;

        const std::wstring out = PowerLink::ShellExtUtils::FormatArgs(
            L"pick \"%s\"", longPath.c_str());

        const bool startsWithPick = out.rfind(L"pick \"", 0) == 0;
        const bool endsWithCloseQuote = !out.empty() && out.back() == L'"';
        const bool containsFullPath = out.find(longPath) != std::wstring::npos;

        Check(startsWithPick, "FormatArgs long path starts with 'pick \"'");
        Check(containsFullPath,
              "FormatArgs long path contains the full path verbatim",
              "len(out)=" + std::to_string(out.size()) +
              " expected len>=" + std::to_string(longPath.size() + 7));
        Check(endsWithCloseQuote,
              "FormatArgs long path ends with closing quote (no truncation)",
              "got len=" + std::to_string(out.size()) +
              ", last char code=" + std::to_string(out.empty() ? 0 : (int)out.back()));
    }

    // ===== P0 #3 — LockServer underflow =====

    void Test_SafeDecrement_FromPositive_DecrementsOne()
    {
        std::atomic<LONG> counter{ 5 };
        const LONG result = PowerLink::ShellExtUtils::SafeDecrement(counter);
        Check(result == 4 && counter.load() == 4,
              "SafeDecrement from positive returns N-1",
              "got result=" + std::to_string(result) +
              ", counter=" + std::to_string(counter.load()));
    }

    void Test_SafeDecrement_FromZero_StaysAtZero()
    {
        std::atomic<LONG> counter{ 0 };
        const LONG result = PowerLink::ShellExtUtils::SafeDecrement(counter);
        Check(result == 0 && counter.load() == 0,
              "SafeDecrement from zero must not go negative",
              "got result=" + std::to_string(result) +
              ", counter=" + std::to_string(counter.load()));
    }

    void Test_SafeDecrement_FromOne_LandsAtZero()
    {
        std::atomic<LONG> counter{ 1 };
        const LONG result = PowerLink::ShellExtUtils::SafeDecrement(counter);
        Check(result == 0 && counter.load() == 0,
              "SafeDecrement from 1 lands at 0",
              "got result=" + std::to_string(result) +
              ", counter=" + std::to_string(counter.load()));
    }

    // ===== P1 #5 — Skip cap =====

    void Test_ClampedSkip_WithinRange_ReturnsSOk()
    {
        size_t index = 0;
        const HRESULT hr = PowerLink::ShellExtUtils::ClampedSkip(index, /*total*/ 6, /*celt*/ 3);
        Check(hr == S_OK && index == 3,
              "ClampedSkip within range returns S_OK and advances",
              "hr=0x" + std::to_string(hr) + ", index=" + std::to_string(index));
    }

    void Test_ClampedSkip_ToExactEnd_ReturnsSOk()
    {
        size_t index = 2;
        const HRESULT hr = PowerLink::ShellExtUtils::ClampedSkip(index, /*total*/ 6, /*celt*/ 4);
        Check(hr == S_OK && index == 6,
              "ClampedSkip to exact end returns S_OK and parks at total",
              "hr=0x" + std::to_string(hr) + ", index=" + std::to_string(index));
    }

    void Test_ClampedSkip_OvershootsEnd_ReturnsSFalseAndClamps()
    {
        size_t index = 4;
        const HRESULT hr = PowerLink::ShellExtUtils::ClampedSkip(index, /*total*/ 6, /*celt*/ 10);
        Check(hr == S_FALSE && index == 6,
              "ClampedSkip overshoot returns S_FALSE and clamps to total",
              "hr=0x" + std::to_string(hr) + ", index=" + std::to_string(index));
    }

    void Test_ClampedSkip_HugeCelt_DoesNotWrap()
    {
        size_t index = 1;
        // MAXULONG cast to size_t — on x64, size_t is 64-bit so no overflow,
        // but the check still verifies we clamp instead of leaving a giant
        // index value.
        const HRESULT hr = PowerLink::ShellExtUtils::ClampedSkip(
            index, /*total*/ 6, /*celt*/ static_cast<size_t>(-1));
        Check(hr == S_FALSE && index == 6,
              "ClampedSkip with size_t::max() celt clamps to total",
              "hr=0x" + std::to_string(hr) + ", index=" + std::to_string(index));
    }

    // ===== P1 #3 — GetModuleDir truncation =====

    void Test_GetModuleDir_NullModule_ReturnsExeDir()
    {
        // GetModuleFileNameW(nullptr, ...) returns the running .exe path —
        // for the test harness that's our own test exe. Useful smoke test
        // that the function works at all and yields a directory ending
        // with a separator.
        const std::wstring dir = PowerLink::ShellExtUtils::GetModuleDir(nullptr);
        const bool nonEmpty = !dir.empty();
        const bool endsWithSep = !dir.empty() && (dir.back() == L'\\' || dir.back() == L'/');
        Check(nonEmpty, "GetModuleDir(nullptr) returns non-empty",
              "len=" + std::to_string(dir.size()));
        Check(endsWithSep, "GetModuleDir(nullptr) ends with directory separator",
              "got: " + Narrow(dir));
    }

    // No reliable cross-machine way to force GetModuleFileNameW truncation
    // from a unit test (we can't choose where the test exe was built or
    // how long its install path is). We DO assert that the implementation
    // either returns a complete path or an empty string — never a silently
    // truncated one. This is checked indirectly by validating that the
    // returned path resolves to an existing file when non-empty.
    void Test_GetModuleDir_NonEmptyResult_PointsToExistingDir()
    {
        const std::wstring dir = PowerLink::ShellExtUtils::GetModuleDir(nullptr);
        if (dir.empty())
        {
            // Acceptable per contract — function may return empty on failure.
            Check(true, "GetModuleDir empty result acceptable per contract");
            return;
        }
        const DWORD attrs = GetFileAttributesW(dir.c_str());
        const bool exists = attrs != INVALID_FILE_ATTRIBUTES;
        const bool isDir = exists && (attrs & FILE_ATTRIBUTE_DIRECTORY);
        Check(isDir, "GetModuleDir result resolves to an existing directory",
              "got: " + Narrow(dir) +
              ", attrs=0x" + std::to_string(attrs));
    }
}

int main()
{
    std::printf("PowerLink.ShellExt utility tests\n");
    std::printf("================================\n\n");

    Test_FormatArgs_ShortPath_NoTruncation();
    Test_FormatArgs_LongPath_PreservesClosingQuote();

    Test_SafeDecrement_FromPositive_DecrementsOne();
    Test_SafeDecrement_FromZero_StaysAtZero();
    Test_SafeDecrement_FromOne_LandsAtZero();

    Test_ClampedSkip_WithinRange_ReturnsSOk();
    Test_ClampedSkip_ToExactEnd_ReturnsSOk();
    Test_ClampedSkip_OvershootsEnd_ReturnsSFalseAndClamps();
    Test_ClampedSkip_HugeCelt_DoesNotWrap();

    Test_GetModuleDir_NullModule_ReturnsExeDir();
    Test_GetModuleDir_NonEmptyResult_PointsToExistingDir();

    std::printf("\n--------------------------------\n");
    std::printf("Result: %d passed, %d failed\n", g_passes, g_failures);
    return g_failures;
}
