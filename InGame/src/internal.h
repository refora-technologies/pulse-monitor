// What the parts of the in-game overlay share with each other. Nothing here is visible outside
// the DLL.
#pragma once
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <atomic>
#include <cstdint>

#include "pulse_ingame_shared.h"

namespace pulse_ingame {

// log.cpp: lines are queued and written by a thread of their own, never inside a frame.
void StartLog();
void Log(const char* format, ...);

// link.cpp: a copy of Pulse's block, if Pulse has written in the last three seconds.
bool ReadBlock(Block& out);
void ReportStatus(uint32_t state);               // StatusState, for Pulse's status line

// frame_stats.cpp: the game's frame rate and 1% low, worked out exactly as Pulse does.
void OnPresent(const void* chain);               // every present, on the presenting thread
const void* DominantChain();                     // the chain being played, or null
bool CurrentFps(float& out);                     // false: no reading ("--")
bool OnePercentLow(float& out);                  // false: not enough frames yet ("--")
#ifdef PULSE_INGAME_TESTING
void TestSetClock(double ms);                    // tests only: exact frame times
void TestReset();
double TestReadingAgeMs();                       // how long ago the readings were worked out
double TestReadingClockMs();                     // when, on the performance counter in ms
double TestLowFromClockMs();                     // the oldest frame behind the 1% low
#endif

// ui.cpp: builds one frame of the overlay with ImGui; the renderer has begun the backend frame.
void SetUpImGui();                               // context, settings and fonts
void ShutDownImGui();
void BuildOverlay(float width, float height, float pixelsPerDip);
bool ShouldDraw();                                // anything to draw this frame; asked before any drawing work
float PixelsPerDip(IDXGISwapChain* chain, float bufferWidth);  // the screen's scaling, times any stretch

// render_dx11.cpp and render_dx12.cpp. Each Init takes the chain the game presents with, and
// each draws onto the frame about to be shown.
bool Dx11Init(IDXGISwapChain* chain, ID3D11Device* device);
void Dx11Draw(IDXGISwapChain* chain);
void Dx11Shutdown();

bool Dx12Init(IDXGISwapChain* chain, ID3D12Device* device);  // false while the queue is unknown
void Dx12Draw(IDXGISwapChain* chain);
void Dx12BeforeResize();
void Dx12Shutdown();
bool Dx12GaveUp();                               // no queue found after many frames
bool Dx12StillMatches(IDXGISwapChain* chain);    // same buffer count and format as the setup

// hooks.cpp: the queue a DirectX 12 program last submitted direct work to.
ID3D12CommandQueue* FindDirectQueue(ID3D12Device* device);
using ExecuteFn = void(STDMETHODCALLTYPE*)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
extern ExecuteFn g_originalExecute;

extern std::atomic<bool> g_disabled;             // set once, on any error: the overlay steps aside

}  // namespace pulse_ingame
