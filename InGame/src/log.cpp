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

DWORD WINAPI Writer(void*) {
    // PULSE_INGAME_LOG if the program was started with it, otherwise beside this DLL.
    char path[MAX_PATH];
    if (!GetEnvironmentVariableA("PULSE_INGAME_LOG", path, MAX_PATH)) {
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(&Writer), &self);
        DWORD n = GetModuleFileNameA(self, path, MAX_PATH);
        if (n == 0 || n >= MAX_PATH - 8) return 0;
        char* dot = std::strrchr(path, '.');
        if (!dot) return 0;
        strcpy_s(dot, MAX_PATH - (dot - path), ".log");
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
    int n = std::snprintf(line, sizeof line, "%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
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
