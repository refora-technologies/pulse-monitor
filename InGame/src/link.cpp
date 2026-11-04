// Pulse's readings, through the block Pulse writes. Opened read only, and looked for at most once
// a second while Pulse is not there, so a game without Pulse pays nothing for it.
#include "internal.h"

#include <cstring>
#include <cwchar>

namespace pulse_ingame {
namespace {

HANDLE g_mapping = nullptr;
const Block* g_view = nullptr;
ULONGLONG g_nextLook = 0;

void CloseBlock() {
    if (g_view) { UnmapViewOfFile(g_view); g_view = nullptr; }
    if (g_mapping) { CloseHandle(g_mapping); g_mapping = nullptr; }
}

}  // namespace

bool ReadBlock(Block& out) {
    ULONGLONG now = GetTickCount64();
    if (!g_view) {
        if (now < g_nextLook) return false;
        g_nextLook = now + 1000;
        g_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kBlockName);
        if (!g_mapping) return false;
        g_view = static_cast<const Block*>(MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, sizeof(Block)));
        if (!g_view) { CloseBlock(); return false; }
        Log("found Pulse's readings");
    }
    for (int attempt = 0; attempt < 3; ++attempt) {
        uint32_t before = g_view->sequence;
        if (before & 1) continue;  // Pulse is writing right now
        MemoryBarrier();
        std::memcpy(&out, g_view, sizeof out);
        MemoryBarrier();
        if (g_view->sequence != before) continue;  // changed while copying
        if (out.magic != kMagic || out.version != kVersion) return false;
        if (out.tileCount > (uint32_t)kMaxTiles) out.tileCount = kMaxTiles;
        if (now - out.writtenAtMs > 3000) {
            // Pulse stopped writing: let the block go at once, so a Pulse started again can make
            // its own (it will not use one that already exists), and look again once a second.
            CloseBlock();
            Log("Pulse's readings stopped");
            return false;
        }
        return true;
    }
    return false;
}

}  // namespace pulse_ingame

namespace pulse_ingame {
namespace {
Status* g_status = nullptr;
bool g_statusTried = false;
}  // namespace

// Tells Pulse what the in-game part is doing in this game. The block is created on first use,
// named for this process, with Windows' default protection; every frame then writes two values.
void ReportStatus(uint32_t state) {
    if (!g_status) {
        if (g_statusTried) return;
        g_statusTried = true;
        wchar_t name[64];
        std::swprintf(name, 64, L"%ls%lu", kStatusPrefix, GetCurrentProcessId());
        HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Status), name);
        if (!mapping) { Log("could not create the status block (%lu)", GetLastError()); return; }
        g_status = static_cast<Status*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(Status)));
        if (!g_status) { CloseHandle(mapping); Log("could not map the status block"); return; }
        g_status->magic = kStatusMagic;
        // The handle stays open for the life of the game, as the view does.
    }
    g_status->state = state;
    g_status->updatedMs = GetTickCount64();
}

}  // namespace pulse_ingame
