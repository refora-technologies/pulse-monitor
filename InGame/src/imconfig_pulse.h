// ImGui settings for the in-game overlay.
//
// A failed ImGui check must never stop the game. By default one calls assert(), which shows a
// dialog and ends the process: the game, not just the overlay. Throwing instead is no better, as
// some checks sit in code that may not throw (destructors), where a throw also ends the process.
// So a failed check is logged and switches the overlay off for the session; the game carries on.
// A crash that follows inside ImGui is still caught around the drawing (the DLL is built /EHa).
#pragma once

namespace pulse_ingame { void ImGuiCheckFailed(const char* what, const char* file, int line); }
#define IM_ASSERT(_EXPR) do { if (!(_EXPR)) ::pulse_ingame::ImGuiCheckFailed(#_EXPR, __FILE__, __LINE__); } while (0)

// Nothing the overlay does needs these, and leaving them out is less code inside someone's game.
#define IMGUI_DISABLE_DEMO_WINDOWS
#define IMGUI_DISABLE_DEBUG_TOOLS
