// Logging. Lines are kept in memory and written by a thread of their own, never inside the
// program's frame: a frame must not wait on the disk, and a file another program holds for a
// moment (an antivirus scanning it, seen 3 Nov 2026) must not lose a line. A failed write is
// retried on the next round.
#include "internal.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace pulse_ingame {
namespace {

std::mutex g_lock;
std::vector<std::string> g_pending;
HANDLE g_wake = nullptr;
char g_who[96] = "";  // "[pid 1234]  " in the shared log, so its lines say which game they are from

DWORD WINAPI Writer(void*) {
    // PULSE_INGAME_LOG if the program was started with it (the tests), otherwise ingame.log beside
    // Pulse's own logs, where its "Save diagnostics" finds it. Not beside this DLL: that is in
    // Program Files, which a game, not running as administrator, cannot write to.
    char path[MAX_PATH];
    if (!GetEnvironmentVariableA("PULSE_INGAME_LOG", path, MAX_PATH)) {
        DWORD n = ExpandEnvironmentStringsA("%APPDATA%\\Refora\\Pulse\\logs\\ingame.log", path, MAX_PATH);
        if (n == 0 || n > MAX_PATH || std::strchr(path, '%')) return 0;
        // Started again from empty past 1 MB; the one before is kept as ingame.1.log.
        WIN32_FILE_ATTRIBUTE_DATA info;
        if (GetFileAttributesExA(path, GetFileExInfoStandard, &info) && (info.nFileSizeHigh || info.nFileSizeLow > 1024 * 1024)) {
            char older[MAX_PATH];
            strcpy_s(older, path);
            char* dot = std::strrchr(older, '.');
            if (dot && dot - older < MAX_PATH - 8) {
                strcpy_s(dot, MAX_PATH - (dot - older), ".1.log");
                MoveFileExA(path, older, MOVEFILE_REPLACE_EXISTING);
            }
        }
        // The game by its process number, never its name: Pulse's privacy policy promises its logs
        // cannot show what you were running.
        std::snprintf(g_who, sizeof g_who, "[pid %lu]  ", GetCurrentProcessId());
    }
    for (;;) {
        WaitForSingleObject(g_wake, 1000);
        std::vector<std::string> batch;
        {
            std::lock_guard<std::mutex> hold(g_lock);
            batch.swap(g_pending);
        }
        if (batch.empty()) continue;
        FILE* f = nullptr;
        for (int attempt = 0; attempt < 20 && (fopen_s(&f, path, "a") != 0 || !f); ++attempt) Sleep(50);
        if (!f) {
            std::lock_guard<std::mutex> hold(g_lock);
            g_pending.insert(g_pending.begin(), batch.begin(), batch.end());
            continue;
        }
        for (const std::string& line : batch) std::fprintf(f, "%s\n", line.c_str());
        std::fclose(f);
    }
}

}  // namespace

void StartLog() {
    g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE t = CreateThread(nullptr, 0, Writer, nullptr, 0, nullptr);
    if (t) CloseHandle(t);
}

void Log(const char* format, ...) {
    char line[512];
    SYSTEMTIME t; GetLocalTime(&t);
    int n = std::snprintf(line, sizeof line, "%02d:%02d:%02d.%03d  %s", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, g_who);
    if (n < 0 || n >= (int)sizeof line) n = 0;
    va_list args; va_start(args, format);
    std::vsnprintf(line + n, sizeof line - n, format, args);
    va_end(args);
    {
        std::lock_guard<std::mutex> hold(g_lock);
        if (g_pending.size() < 1000) g_pending.emplace_back(line);
    }
    if (g_wake) SetEvent(g_wake);
}

// Called by ImGui's checks (imconfig_pulse.h): logged once, and the overlay steps aside.
void ImGuiCheckFailed(const char* what, const char* file, int line) {
    bool first = !g_disabled.exchange(true);
    if (first) {
        const char* name = std::strrchr(file, '\\');
        Log("an ImGui check failed (%s, %s:%d); overlay switched off for this session, the game carries on",
            what, name ? name + 1 : file, line);
    }
}

}  // namespace pulse_ingame
