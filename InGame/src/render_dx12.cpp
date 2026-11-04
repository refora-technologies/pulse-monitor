// Drawing into a DirectX 12 game's frame.
//
// DirectX 12 has no "draw now": the overlay records its own short list of drawing work and
// submits it on the game's own queue, after the game's frame and before it is shown. The queue is
// the one the presenting thread last submitted direct work to (hooks.cpp).
//
// The back buffer is taken each frame and let go once the work is submitted; the swap chain keeps
// it alive while the GPU uses it. Holding it between frames stops a game resizing or recreating
// its swap chain. Each buffer slot's previous overlay must have finished on the GPU before its
// allocator is reused, which a fence per slot tells us.
#include "internal.h"

#include <utility>
#include <vector>

#include "imgui.h"
#include "backends/imgui_impl_dx12.h"

namespace pulse_ingame {
namespace {

constexpr UINT kMaxBuffers = 8;
constexpr UINT kSrvCount = 64;

struct Slot { ID3D12CommandAllocator* allocator = nullptr; UINT64 fence = 0; };

ID3D12Device* g_device = nullptr;
ID3D12CommandQueue* g_queue = nullptr;
UINT g_count = 0;
DXGI_FORMAT g_format = DXGI_FORMAT_UNKNOWN;
ID3D12DescriptorHeap* g_rtvHeap = nullptr;
ID3D12DescriptorHeap* g_srvHeap = nullptr;
UINT g_rtvSize = 0, g_srvSize = 0;
std::vector<UINT> g_freeSrv;
Slot g_slots[kMaxBuffers];
ID3D12GraphicsCommandList* g_list = nullptr;
ID3D12Fence* g_fence = nullptr;
HANDLE g_event = nullptr;
UINT64 g_fenceValue = 0;
int g_framesWithoutQueue = 0;
bool g_gaveUp = false;

void WaitFence(UINT64 value) {
    if (g_fence && g_fence->GetCompletedValue() < value) {
        g_fence->SetEventOnCompletion(value, g_event);
        WaitForSingleObject(g_event, 2000);
    }
}

void SrvAlloc(ImGui_ImplDX12_InitInfo*, D3D12_CPU_DESCRIPTOR_HANDLE* cpu, D3D12_GPU_DESCRIPTOR_HANDLE* gpu) {
    if (g_freeSrv.empty()) { cpu->ptr = 0; gpu->ptr = 0; Log("out of texture slots"); return; }
    UINT i = g_freeSrv.back(); g_freeSrv.pop_back();
    *cpu = g_srvHeap->GetCPUDescriptorHandleForHeapStart(); cpu->ptr += (SIZE_T)i * g_srvSize;
    *gpu = g_srvHeap->GetGPUDescriptorHandleForHeapStart(); gpu->ptr += (UINT64)i * g_srvSize;
}

void SrvFree(ImGui_ImplDX12_InitInfo*, D3D12_CPU_DESCRIPTOR_HANDLE cpu, D3D12_GPU_DESCRIPTOR_HANDLE) {
    g_freeSrv.push_back((UINT)((cpu.ptr - g_srvHeap->GetCPUDescriptorHandleForHeapStart().ptr) / g_srvSize));
}

template <typename T> void Drop(T*& p) { if (p) { p->Release(); p = nullptr; } }

}  // namespace

bool Dx12GaveUp() { return g_gaveUp; }

// Whether the chain still matches the setup: only its buffer count and format matter, as the
// buffers themselves are taken fresh every frame. A plain size change needs nothing at all.
bool Dx12StillMatches(IDXGISwapChain* chain) {
    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(chain->GetDesc(&desc))) return false;
    return desc.BufferCount == g_count && desc.BufferDesc.Format == g_format;
}

bool Dx12Init(IDXGISwapChain* chain, ID3D12Device* device) {
    ID3D12CommandQueue* queue = FindDirectQueue(device);
    if (!queue) {
        // The game has not submitted work we could see yet; try again on the next frames.
        if (++g_framesWithoutQueue > 600) { g_gaveUp = true; Log("no DirectX 12 queue found in 600 frames; overlay stays off"); }
        device->Release();
        return false;
    }
    g_device = device;  // the reference GetDevice gave us
    g_queue = queue; g_queue->AddRef();

    DXGI_SWAP_CHAIN_DESC desc{};
    chain->GetDesc(&desc);
    g_count = desc.BufferCount;
    g_format = desc.BufferDesc.Format;
    if (g_count == 0 || g_count > kMaxBuffers) { Log("unexpected buffer count %u; overlay stays off", g_count); return false; }

    D3D12_DESCRIPTOR_HEAP_DESC rtv{}; rtv.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; rtv.NumDescriptors = kMaxBuffers;
    D3D12_DESCRIPTOR_HEAP_DESC srv{}; srv.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; srv.NumDescriptors = kSrvCount;
    srv.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    HRESULT hr = device->CreateDescriptorHeap(&rtv, IID_PPV_ARGS(&g_rtvHeap));
    if (SUCCEEDED(hr)) hr = device->CreateDescriptorHeap(&srv, IID_PPV_ARGS(&g_srvHeap));
    if (FAILED(hr)) {
        // Said with the code, and whether the game's device itself is gone, so a failure in a game
        // we cannot see explains itself.
        Log("descriptor heaps failed (0x%08lx; device removed reason 0x%08lx); overlay stays off",
            (unsigned long)hr, (unsigned long)device->GetDeviceRemovedReason());
        return false;
    }
    g_rtvSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    g_srvSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    g_freeSrv.clear();
    for (UINT i = kSrvCount; i-- > 0;) g_freeSrv.push_back(i);

    for (UINT i = 0; i < g_count; ++i)
        if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_slots[i].allocator)))) { Log("allocator failed; overlay stays off"); return false; }
    if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_slots[0].allocator, nullptr, IID_PPV_ARGS(&g_list))) ||
        FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)))) { Log("command list or fence failed; overlay stays off"); return false; }
    g_list->Close();
    g_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    SetUpImGui();
    ImGui_ImplDX12_InitInfo info;
    info.Device = device;
    info.CommandQueue = queue;
    info.NumFramesInFlight = (int)g_count;
    info.RTVFormat = g_format;
    info.DSVFormat = DXGI_FORMAT_UNKNOWN;
    info.SrvDescriptorHeap = g_srvHeap;
    info.SrvDescriptorAllocFn = SrvAlloc;
    info.SrvDescriptorFreeFn = SrvFree;
    if (!ImGui_ImplDX12_Init(&info)) { Log("ImGui DirectX 12 setup failed; overlay stays off"); return false; }
    Log("ready: drawing into this program's DirectX 12 frames (%u buffers, format %d)", g_count, (int)g_format);
    return true;
}

void Dx12Draw(IDXGISwapChain* chain) {
    if (!ShouldDraw()) return;
    IDXGISwapChain3* chain3 = nullptr;
    if (FAILED(chain->QueryInterface(IID_PPV_ARGS(&chain3)))) return;
    UINT index = chain3->GetCurrentBackBufferIndex();
    chain3->Release();
    if (index >= g_count) return;

    ID3D12Resource* buffer = nullptr;
    if (FAILED(chain->GetBuffer(index, IID_PPV_ARGS(&buffer)))) return;
    D3D12_RESOURCE_DESC rd = buffer->GetDesc();
    if (rd.Format != g_format && rd.Format != DXGI_FORMAT_UNKNOWN) {
        // The game changed format on a resize; ImGui's pipeline was built for the old one.
        Log("buffer format changed from %d to %d; overlay stays off", (int)g_format, (int)rd.Format);
        buffer->Release();
        g_disabled = true;
        return;
    }
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
    rtv.ptr += (SIZE_T)index * g_rtvSize;
    g_device->CreateRenderTargetView(buffer, nullptr, rtv);

    ImGui_ImplDX12_NewFrame();
    BuildOverlay((float)rd.Width, (float)rd.Height, PixelsPerDip(chain, (float)rd.Width));

    Slot& slot = g_slots[index];
    WaitFence(slot.fence);  // the GPU must be done with this slot's last overlay before reuse
    slot.allocator->Reset();
    g_list->Reset(slot.allocator, nullptr);

    D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = buffer; b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT; b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    g_list->ResourceBarrier(1, &b);
    g_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
    g_list->SetDescriptorHeaps(1, &g_srvHeap);
    ImGui_ImplDX12_RenderDrawData(ImGui::GetDrawData(), g_list);
    std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
    g_list->ResourceBarrier(1, &b);
    g_list->Close();

    ID3D12CommandList* lists[] = { g_list };
    g_originalExecute(g_queue, 1, lists);
    g_queue->Signal(g_fence, ++g_fenceValue);
    slot.fence = g_fenceValue;
    buffer->Release();  // the swap chain keeps it alive while the GPU uses it
}

// A resize needs every reference to the buffers gone and the queue idle; ours are already gone,
// so only our last submissions need to finish.
void Dx12BeforeResize() {
    if (!g_queue || !g_fence) return;
    g_queue->Signal(g_fence, ++g_fenceValue);
    WaitFence(g_fenceValue);
}

void Dx12Shutdown() {
    Dx12BeforeResize();
    // Only what was set up: a setup that failed halfway leaves some of it undone.
    if (ImGui::GetCurrentContext() && ImGui::GetIO().BackendRendererUserData) ImGui_ImplDX12_Shutdown();
    ShutDownImGui();
    for (Slot& s : g_slots) { Drop(s.allocator); s.fence = 0; }
    Drop(g_list); Drop(g_fence); Drop(g_rtvHeap); Drop(g_srvHeap); Drop(g_queue); Drop(g_device);
    if (g_event) { CloseHandle(g_event); g_event = nullptr; }
    g_count = 0; g_fenceValue = 0; g_framesWithoutQueue = 0;
}

}  // namespace pulse_ingame
