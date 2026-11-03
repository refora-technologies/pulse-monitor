// Drawing into a DirectX 11 game's frame.
//
// The back buffer is taken each frame and let go straight after drawing. Holding on to it between
// frames is a classic overlay bug: a game that releases and recreates its swap chain (some do on a
// mode change) then cannot, because our reference keeps the old one alive.
#include "internal.h"

#include "imgui.h"
#include "backends/imgui_impl_dx11.h"

namespace pulse_ingame {
namespace {

ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_context = nullptr;

}  // namespace

bool Dx11Init(IDXGISwapChain*, ID3D11Device* device) {
    g_device = device;  // the reference GetDevice gave us
    g_device->GetImmediateContext(&g_context);
    SetUpImGui();
    if (!ImGui_ImplDX11_Init(g_device, g_context)) {
        Log("ImGui DirectX 11 setup failed; overlay stays off");
        return false;
    }
    Log("ready: drawing into this program's DirectX 11 frames");
    return true;
}

void Dx11Draw(IDXGISwapChain* chain) {
    ID3D11Texture2D* back = nullptr;
    if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&back)))) return;
    ID3D11RenderTargetView* rtv = nullptr;
    HRESULT hr = g_device->CreateRenderTargetView(back, nullptr, &rtv);
    D3D11_TEXTURE2D_DESC desc{};
    back->GetDesc(&desc);
    back->Release();
    if (FAILED(hr) || !rtv) return;

    ImGui_ImplDX11_NewFrame();
    BuildOverlay((float)desc.Width, (float)desc.Height, PixelsPerDip(chain, (float)desc.Width));

    // Draw onto the frame, then put the game's own render targets back as they were.
    ID3D11RenderTargetView* savedRtv = nullptr; ID3D11DepthStencilView* savedDsv = nullptr;
    g_context->OMGetRenderTargets(1, &savedRtv, &savedDsv);
    g_context->OMSetRenderTargets(1, &rtv, nullptr);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    g_context->OMSetRenderTargets(1, &savedRtv, savedDsv);
    if (savedRtv) savedRtv->Release();
    if (savedDsv) savedDsv->Release();
    rtv->Release();
}

void Dx11Shutdown() {
    // Only what was set up: a setup that failed halfway leaves some of it undone.
    if (ImGui::GetCurrentContext() && ImGui::GetIO().BackendRendererUserData) ImGui_ImplDX11_Shutdown();
    ShutDownImGui();
    if (g_context) { g_context->Release(); g_context = nullptr; }
    if (g_device) { g_device->Release(); g_device = nullptr; }
}

}  // namespace pulse_ingame
