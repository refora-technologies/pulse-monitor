// How the overlay gets into the frame: the swap chain's function table.
//
// Every DXGI swap chain of a kind shares one table of functions. Replacing the table's Present,
// Present1, ResizeBuffers and ResizeBuffers1 entries with ours puts the overlay in every frame,
// and each of ours calls the entry that was there before, so it chains with other overlays in
// either order. Chosen over hooking the functions' code (Detours) on 3 Nov 2026: RivaTuner
// Statistics Server, on its default settings, puts the original code back within half a second,
// so a code hook silently never runs on many gamers' PCs. It leaves tables alone.
//
// A watchdog checks every few seconds that our entries are still ours. If another program
// replaced one, it says so in the log and steps aside: it never puts ours back.
#include "internal.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>

namespace pulse_ingame {

std::atomic<bool> g_disabled{ false };
ExecuteFn g_originalExecute = nullptr;

namespace {

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using Resize1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);

constexpr int kPresent = 8, kResizeBuffers = 13, kPresent1 = 22, kResizeBuffers1 = 39, kExecuteCommandLists = 10;

struct Table {
    void** table = nullptr;
    PresentFn present = nullptr; Present1Fn present1 = nullptr;
    ResizeFn resize = nullptr; Resize1Fn resize1 = nullptr;
};
Table g_tables[4];
void** g_queueTable = nullptr;

Table* TableOf(void* object) {
    void** table = *reinterpret_cast<void***>(object);
    for (Table& t : g_tables) if (t.table == table) return &t;
    return nullptr;
}

// ---- the queue a DirectX 12 game shows its frames with ------------------------------------

thread_local ID3D12CommandQueue* t_lastDirectQueue = nullptr;
std::atomic<ID3D12CommandQueue*> g_lastDirectQueue{ nullptr };

void STDMETHODCALLTYPE HookedExecuteCommandLists(ID3D12CommandQueue* queue, UINT count, ID3D12CommandList* const* lists) {
    if (queue != t_lastDirectQueue && queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT) {
        t_lastDirectQueue = queue;
        g_lastDirectQueue = queue;
    }
    g_originalExecute(queue, count, lists);
}

// ---- which chain is drawn on, with what ------------------------------------------------------

enum class Api { None, Dx11, Dx12 };
Api g_api = Api::None;
const void* g_bound = nullptr;      // the chain the renderer is set up for
bool g_rebind = false;              // set by a resize of the bound DirectX 12 chain
const void* g_refused = nullptr;    // a chain no renderer could take; not retried every frame
std::atomic<long long> g_presentsSeen{ 0 };

#ifdef PULSE_INGAME_TESTING
long long g_faultAfter = -1;        // test builds only: force an error after this many draws
long long g_draws = 0;
double g_drawMsTotal = 0;      // the overlay's own time in frames since the last report
long long g_drawCount = 0;
#endif

void Unbind() {
    if (g_api == Api::Dx11) Dx11Shutdown();
    if (g_api == Api::Dx12) Dx12Shutdown();
    g_api = Api::None;
    g_bound = nullptr;
}

bool Bind(IDXGISwapChain* chain) {
    ID3D11Device* d11 = nullptr; ID3D12Device* d12 = nullptr;
    if (SUCCEEDED(chain->GetDevice(IID_PPV_ARGS(&d11)))) {
        if (!Dx11Init(chain, d11)) { Dx11Shutdown(); return false; }
        g_api = Api::Dx11;
    } else if (SUCCEEDED(chain->GetDevice(IID_PPV_ARGS(&d12)))) {
        if (!Dx12Init(chain, d12)) {
            Dx12Shutdown();
            // Waiting for the queue is retried next frame; anything else is not retried.
            if (Dx12GaveUp() || FindDirectQueue(nullptr)) g_refused = chain;
            return false;
        }
        g_api = Api::Dx12;
    } else {
        Log("a swap chain that is neither DirectX 11 nor 12; not drawn on");
        ReportStatus(NotDirectX);
        g_refused = chain;
        return false;
    }
    g_bound = chain;
    return true;
}

void Draw(IDXGISwapChain* chain) {
    // Only the chain being played, decided the way Pulse decides it: not a launcher, splash
    // screen or second window that happened to present first.
    const void* played = DominantChain();
    if (!played || chain != played || chain == g_refused) return;

    if (g_bound != chain || g_rebind) {
        if (g_bound) Log(g_rebind ? "the swap chain changed buffer count or format; setting up again" : "the game moved to another swap chain; following it");
        Unbind();
        g_rebind = false;
        if (!Bind(chain)) return;
    }
#ifdef PULSE_INGAME_TESTING
    if (g_faultAfter >= 0 && ++g_draws > g_faultAfter) throw 1;
    // Test builds report what the overlay shows every two seconds, so tests can compare it.
    static ULONGLONG nextReport = 0;
    ULONGLONG now = GetTickCount64();
    if (now >= nextReport) {
        nextReport = now + 2000;
        float fps = -1, low = -1;
        CurrentFps(fps); OnePercentLow(low);
        Log("test: drawing on chain %p, fps %.2f, 1%% low %.2f, worked out %.0f ms ago, at clock %.3f, low from %.3f, overlay %.4f ms per frame",
            chain, fps, low, TestReadingAgeMs(), TestReadingClockMs(), TestLowFromClockMs(), g_drawCount ? g_drawMsTotal / g_drawCount : 0.0);
        g_drawMsTotal = 0; g_drawCount = 0;
    }
#endif
#ifdef PULSE_INGAME_TESTING
    // Test builds time the overlay's own work in every frame: its true cost, which comparing
    // whole runs cannot resolve on a busy PC.
    LARGE_INTEGER t0, t1, f; QueryPerformanceCounter(&t0);
#endif
    if (g_api == Api::Dx11) Dx11Draw(chain); else Dx12Draw(chain);
#ifdef PULSE_INGAME_TESTING
    QueryPerformanceCounter(&t1); QueryPerformanceFrequency(&f);
    g_drawMsTotal += double(t1.QuadPart - t0.QuadPart) * 1000.0 / double(f.QuadPart);
    ++g_drawCount;
#endif
}

thread_local int t_depth = 0;  // DXGI may pass one present through the other: count and draw once

void OnFrame(IDXGISwapChain* chain, UINT flags) {
    if (t_depth != 1 || (flags & DXGI_PRESENT_TEST)) return;
    ++g_presentsSeen;
    OnPresent(chain);
    if (g_disabled) { ReportStatus(SwitchedOff); return; }
    try {
        Draw(chain);
    } catch (...) {
        g_disabled = true;
        Log("an error inside the overlay; switched off for this session, the game carries on");
    }
}

HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* chain, UINT sync, UINT flags) {
    ++t_depth;
    OnFrame(chain, flags);
    HRESULT hr = TableOf(chain)->present(chain, sync, flags);
    --t_depth;
    return hr;
}

HRESULT STDMETHODCALLTYPE HookedPresent1(IDXGISwapChain1* chain, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params) {
    ++t_depth;
    OnFrame(chain, flags);
    HRESULT hr = TableOf(chain)->present1(chain, sync, flags, params);
    --t_depth;
    return hr;
}

void BeforeResize(IDXGISwapChain* chain) {
    if (chain == g_bound && g_api == Api::Dx12) Dx12BeforeResize();
}

void AfterResize(IDXGISwapChain* chain, HRESULT hr) {
    // DirectX 12's setup depends on the buffer count and format, which a resize can change. Only
    // then is it set up again: a game switching to windowed was seen resizing 14 times in four
    // seconds (007 First Light, 3 Nov 2026), and rebuilding on each was 15 ms of waste every time.
    if (SUCCEEDED(hr) && chain == g_bound && g_api == Api::Dx12 && !Dx12StillMatches(chain)) g_rebind = true;
}

HRESULT STDMETHODCALLTYPE HookedResizeBuffers(IDXGISwapChain* chain, UINT count, UINT w, UINT h, DXGI_FORMAT format, UINT flags) {
    BeforeResize(chain);
    HRESULT hr = TableOf(chain)->resize(chain, count, w, h, format, flags);
    AfterResize(chain, hr);
    return hr;
}

HRESULT STDMETHODCALLTYPE HookedResizeBuffers1(IDXGISwapChain3* chain, UINT count, UINT w, UINT h, DXGI_FORMAT format, UINT flags,
                                              const UINT* nodes, IUnknown* const* queues) {
    BeforeResize(chain);
    HRESULT hr = TableOf(chain)->resize1(chain, count, w, h, format, flags, nodes, queues);
    AfterResize(chain, hr);
    return hr;
}

// ---- putting the entries in, and watching them -----------------------------------------------

bool PatchSlot(void** table, int slot, void* hook, void** original) {
    if (table[slot] == hook) return true;  // already ours
    DWORD old;
    if (!VirtualProtect(&table[slot], sizeof(void*), PAGE_READWRITE, &old)) return false;
    *original = table[slot];
    table[slot] = hook;
    VirtualProtect(&table[slot], sizeof(void*), old, &old);
    return true;
}

bool HookTable(IDXGISwapChain* chain, bool has1, bool has3) {
    void** table = *reinterpret_cast<void***>(chain);
    for (Table& t : g_tables) if (t.table == table) return true;  // shared with a kind already done
    for (Table& t : g_tables) {
        if (t.table) continue;
        t.table = table;
        bool ok = PatchSlot(table, kPresent, reinterpret_cast<void*>(&HookedPresent), reinterpret_cast<void**>(&t.present)) &&
                  PatchSlot(table, kResizeBuffers, reinterpret_cast<void*>(&HookedResizeBuffers), reinterpret_cast<void**>(&t.resize));
        if (ok && has1) ok = PatchSlot(table, kPresent1, reinterpret_cast<void*>(&HookedPresent1), reinterpret_cast<void**>(&t.present1));
        if (ok && has3) ok = PatchSlot(table, kResizeBuffers1, reinterpret_cast<void*>(&HookedResizeBuffers1), reinterpret_cast<void**>(&t.resize1));
        return ok;
    }
    Log("more kinds of swap chain than expected; one is not hooked");
    return false;
}

const char* ModuleOf(void* address, char* name, DWORD size) {
    HMODULE m = nullptr;
    std::snprintf(name, size, "unknown code");
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCSTR>(address), &m) && m) {
        char path[MAX_PATH];
        if (GetModuleFileNameA(m, path, MAX_PATH)) {
            const char* slash = std::strrchr(path, '\\');
            std::snprintf(name, size, "%s", slash ? slash + 1 : path);
        }
    }
    return name;
}

DWORD WINAPI Watchdog(void*) {
    struct Watched { void** table; int slot; void* ours; bool replaced; };
    Watched watched[4 * 4 + 1] = {};
    int n = 0;
    for (Table& t : g_tables) {
        if (!t.table) continue;
        watched[n++] = { t.table, kPresent, reinterpret_cast<void*>(&HookedPresent), false };
        watched[n++] = { t.table, kResizeBuffers, reinterpret_cast<void*>(&HookedResizeBuffers), false };
        if (t.present1) watched[n++] = { t.table, kPresent1, reinterpret_cast<void*>(&HookedPresent1), false };
        if (t.resize1) watched[n++] = { t.table, kResizeBuffers1, reinterpret_cast<void*>(&HookedResizeBuffers1), false };
    }
    if (g_queueTable) watched[n++] = { g_queueTable, kExecuteCommandLists, reinterpret_cast<void*>(&HookedExecuteCommandLists), false };

    long long lastPresents = g_presentsSeen;
    for (;;) {
        Sleep(3000);
        long long presents = g_presentsSeen;
        for (int i = 0; i < n; ++i) {
            Watched& w = watched[i];
            bool replaced = w.table[w.slot] != w.ours;
            if (replaced == w.replaced) continue;
            w.replaced = replaced;
            char name[MAX_PATH];
            if (replaced)
                Log("entry %d was replaced by %s; %s. Stepping aside, not putting ours back.", w.slot,
                    ModuleOf(w.table[w.slot], name, sizeof name),
                    presents > lastPresents ? "frames still reach the overlay through it" : "no frames reach the overlay now");
            else
                Log("entry %d is ours again", w.slot);
        }
        lastPresents = presents;
    }
}

}  // namespace

// The queue a DirectX 12 game last submitted direct work to on the presenting thread, or on any
// thread, as long as it belongs to the given device. With a null device: any queue seen at all.
ID3D12CommandQueue* FindDirectQueue(ID3D12Device* device) {
    for (ID3D12CommandQueue* q : { t_lastDirectQueue, g_lastDirectQueue.load() }) {
        if (!q) continue;
        if (!device) return q;
        ID3D12Device* owner = nullptr;
        if (SUCCEEDED(q->GetDevice(IID_PPV_ARGS(&owner)))) {
            bool same = owner == device;
            owner->Release();
            if (same) return q;
        }
    }
    return nullptr;
}

namespace {

// Finds the tables with throwaway swap chains on a hidden window, using Windows' software renderer
// (WARP), so loading the overlay never wakes a sleeping graphics card; then patches them.
DWORD WINAPI Install(void*) {
#ifdef PULSE_INGAME_TESTING
    char fault[32];
    if (GetEnvironmentVariableA("PULSE_INGAME_FAULT_AFTER", fault, sizeof fault)) {
        g_faultAfter = std::atoll(fault);
        Log("test build: an error will be forced after %lld draws", g_faultAfter);
    }
#endif
    WNDCLASSW wc{}; wc.lpfnWndProc = DefWindowProcW; wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"PulseIngameProbe";
    RegisterClassW(&wc);
    HWND probe = CreateWindowW(wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr, wc.hInstance, nullptr);
    bool ok11 = false, ok12 = false;

    {
        DXGI_SWAP_CHAIN_DESC sd{};
        sd.BufferCount = 2; sd.BufferDesc.Width = 64; sd.BufferDesc.Height = 64;
        sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.OutputWindow = probe; sd.SampleDesc.Count = 1; sd.Windowed = TRUE; sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr; IDXGISwapChain* sc = nullptr;
        if (SUCCEEDED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &sd, &sc, &dev, nullptr, &ctx))) {
            IDXGISwapChain3* sc3 = nullptr;
            bool has3 = SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&sc3)));
            if (sc3) sc3->Release();
            ok11 = HookTable(sc, true, has3);
            sc->Release(); ctx->Release(); dev->Release();
        }
    }
    {
        IDXGIFactory4* factory = nullptr; IDXGIAdapter* warp = nullptr; ID3D12Device* dev = nullptr; ID3D12CommandQueue* queue = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) &&
            SUCCEEDED(D3D12CreateDevice(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev)))) {
            D3D12_COMMAND_QUEUE_DESC qd{}; qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            if (SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)))) {
                g_queueTable = *reinterpret_cast<void***>(queue);
                bool okQueue = PatchSlot(g_queueTable, kExecuteCommandLists, reinterpret_cast<void*>(&HookedExecuteCommandLists),
                                         reinterpret_cast<void**>(&g_originalExecute));
                DXGI_SWAP_CHAIN_DESC1 sd{}; sd.Width = 64; sd.Height = 64; sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 2; sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; sd.SampleDesc.Count = 1;
                IDXGISwapChain1* sc = nullptr;
                if (okQueue && SUCCEEDED(factory->CreateSwapChainForHwnd(queue, probe, &sd, nullptr, nullptr, &sc))) {
                    ok12 = HookTable(sc, true, true);
                    sc->Release();
                }
                queue->Release();
            }
        }
        if (dev) dev->Release();
        if (warp) warp->Release();
        if (factory) factory->Release();
    }
    DestroyWindow(probe);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);

    int tables = 0;
    for (Table& t : g_tables) tables += t.table != nullptr;
    Log("attached: DirectX 11 %s, DirectX 12 %s; %d swap chain table(s)", ok11 ? "yes" : "no", ok12 ? "yes" : "no", tables);
    if (!ok11 && !ok12) { Log("could not attach at all; overlay stays off"); return 0; }

    HANDLE watchdog = CreateThread(nullptr, 0, Watchdog, nullptr, 0, nullptr);
    if (watchdog) CloseHandle(watchdog);
    return 0;
}

}  // namespace
}  // namespace pulse_ingame

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // Nothing heavy under the loader lock: the work happens on threads of their own.
        pulse_ingame::StartLog();
        HANDLE thread = CreateThread(nullptr, 0, pulse_ingame::Install, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
