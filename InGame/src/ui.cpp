// The overlay's look: Pulse's compact HUD (Views/OverlayWindow.xaml, CompactPanel) with the same
// colours and font, laid out as a list or a straight bar, drawn with ImGui's draw list. In games it
// is always the compact one, without the PULSE badge (owner, 3 Nov 2026). Sizes are Pulse's in
// device-independent pixels, multiplied by the scale worked out for this frame. Nothing takes input.
#include "internal.h"

#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "imgui.h"

namespace pulse_ingame {
namespace {

// Pulse's colours (ViewModels/OverlayViewModel.cs and the overlay's XAML).
constexpr ImU32 Rgb(uint32_t rgb, float alpha = 1.0f) {
    return IM_COL32((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, (int)(alpha * 255.0f + 0.5f));
}
// The compact HUD's colours (OverlayWindow.xaml's CompactPanel and OverlayViewModel.cs).
constexpr uint32_t kAccent = 0x8B5CF6, kPanel = 0x0C0A18, kDivider = 0x2A2548;
constexpr uint32_t kLabel = 0x9692B8, kUnit = 0x8E8AB0;
constexpr uint32_t kNeutral = 0xF4F2FC, kNormal = 0x3DDC97, kWarn = 0xFFB454, kDanger = 0xFF5C6C;

uint32_t StateColor(uint8_t state) {
    switch (state) { case Normal: return kNormal; case Warn: return kWarn; case Danger: return kDanger; default: return kNeutral; }
}

ImFont* g_medium = nullptr; ImFont* g_bold = nullptr;

// Everything this frame draws with: the scale, and the two opacities Pulse's settings give.
struct Style {
    float s = 1;          // device-independent pixels to buffer pixels
    float overlay = 1;    // the whole overlay
    float panel = 1;      // the panel, its outline and dividers: never the readings
    ImU32 Col(uint32_t rgb, float alpha = 1) const { return Rgb(rgb, std::clamp(alpha * overlay, 0.0f, 1.0f)); }
    ImU32 Bg(uint32_t rgb, float alpha = 1) const { return Col(rgb, alpha * panel); }
};

ImVec2 Measure(ImFont* font, float size, const char* text) {
    return font->CalcTextSizeA(size, FLT_MAX, 0.0f, text);
}

void Text(ImDrawList* d, ImFont* font, float size, ImVec2 at, ImU32 color, const char* text) {
    d->AddText(font, size, at, color, text);
}

struct Row { const Tile* tile; std::string value; uint8_t state; };

// The tiles to show: Pulse's, with the frame rate and 1% low filled in from the game's own frames.
std::vector<Row> Rows(const Block& b) {
    std::vector<Row> rows;
    for (uint32_t i = 0; i < b.tileCount; ++i) {
        const Tile& t = b.tiles[i];
        char v[24];
        std::snprintf(v, sizeof v, "%.23s", t.value);
        uint8_t state = t.state;
        float x;
        if (!std::strncmp(t.id, "fps", sizeof t.id)) {
            // Pulse formats every frame rate "F0" and gives FPS no thresholds, so it is neutral.
            if (CurrentFps(x)) std::snprintf(v, sizeof v, "%.0f", x); else std::snprintf(v, sizeof v, "--");
            state = Neutral;
        } else if (!std::strncmp(t.id, "fps_1low", sizeof t.id)) {
            if (OnePercentLow(x)) std::snprintf(v, sizeof v, "%.0f", x); else std::snprintf(v, sizeof v, "--");
            state = Neutral;
        }
        rows.push_back({ &t, v, state });
    }
    return rows;
}

// Which screen edges the panel sits against: anchored all the way to a side (a corner button, or a
// slider at 0% or 100%). Those sides are drawn flush and flat, like a laptop's camera notch coming
// out of the display (owner, 4 Nov 2026).
struct Edges { bool top = false, right = false, bottom = false, left = false; };

Edges Touching(float ax, float ay) {
    Edges e;
    e.left = ax <= 0.0f; e.right = ax >= 1.0f; e.top = ay <= 0.0f; e.bottom = ay >= 1.0f;
    return e;
}

// Where a panel of this size sits: anchored the way the window overlay is, 2 px from the edge, or
// right against it on a side it is flush with.
ImVec2 Place(float W, float H, float w, float h, float ax, float ay, float s, const Edges& e) {
    float left = e.left ? 0 : 2 * s, right = e.right ? 0 : 2 * s, top = e.top ? 0 : 2 * s, bottom = e.bottom ? 0 : 2 * s;
    return ImVec2(left + ax * (W - w - left - right), top + ay * (H - h - top - bottom));
}

// Appends a quarter circle around c from point a to point b, the short way round.
void Quarter(std::vector<ImVec2>& out, ImVec2 c, ImVec2 a, ImVec2 b, float r) {
    const float pi = 3.14159265f;
    float a0 = std::atan2(a.y - c.y, a.x - c.x), a1 = std::atan2(b.y - c.y, b.x - c.x);
    float delta = a1 - a0;
    while (delta > pi) delta -= 2 * pi;
    while (delta < -pi) delta += 2 * pi;
    int steps = std::clamp((int)(r * 0.8f), 6, 24);
    for (int i = 0; i <= steps; ++i) {
        float t = a0 + delta * i / steps;
        out.push_back(ImVec2(c.x + std::cos(t) * r, c.y + std::sin(t) * r));
    }
}

// The panel: Pulse's rounded rectangle, except on a side flush with the screen's edge, which is
// flat, square at its corners, and flares into the edge with a small inward curve at each end
// where it meets a rounded side. Filled as one shape; the outline follows it everywhere but along
// the screen's edge itself.
void Panel(ImDrawList* d, ImVec2 p, ImVec2 q, const Style& st, const Edges& e) {
    const float s = st.s, R = 12 * s, ear = 8 * s;
    // Clockwise from the top left. Each corner: where it is, the way the side coming into it
    // runs, the way the side leaving it runs, and whether each of those sides is flush.
    struct Corner { ImVec2 k, in, out; bool inFlush, outFlush; };
    const Corner corners[4] = {
        { p,                  ImVec2(0, -1), ImVec2(1, 0),  e.left,   e.top },
        { ImVec2(q.x, p.y),   ImVec2(1, 0),  ImVec2(0, 1),  e.top,    e.right },
        { q,                  ImVec2(0, 1),  ImVec2(-1, 0), e.right,  e.bottom },
        { ImVec2(p.x, q.y),   ImVec2(-1, 0), ImVec2(0, -1), e.bottom, e.left },
    };
    auto at = [](ImVec2 k, ImVec2 a, float x, ImVec2 b, float y) { return ImVec2(k.x + a.x * x + b.x * y, k.y + a.y * x + b.y * y); };

    std::vector<ImVec2> pts;      // the whole outline
    std::vector<bool> drawn;      // whether the stroke reaches each point from the one before
    auto add = [&](const std::vector<ImVec2>& more, bool firstDrawn) {
        for (size_t i = 0; i < more.size(); ++i) { pts.push_back(more[i]); drawn.push_back(i == 0 ? firstDrawn : true); }
    };
    for (int i = 0; i < 4; ++i) {
        const Corner& c = corners[i];
        std::vector<ImVec2> arc;
        if (!c.inFlush && !c.outFlush) {
            // Rounded, as Pulse draws it.
            Quarter(arc, at(c.k, c.in, -R, c.out, R), at(c.k, c.in, -R, c.out, 0), at(c.k, c.in, 0, c.out, R), R);
            add(arc, true);
        } else if (c.inFlush && !c.outFlush) {
            // Leaving the edge: the flat side runs on past the corner, then curves in to meet this one.
            Quarter(arc, at(c.k, c.in, ear, c.out, ear), at(c.k, c.in, ear, c.out, 0), at(c.k, c.in, 0, c.out, ear), ear);
            add(arc, false);
        } else if (!c.inFlush && c.outFlush) {
            // Reaching the edge: this side curves out into the flat one.
            Quarter(arc, at(c.k, c.in, -ear, c.out, -ear), at(c.k, c.in, -ear, c.out, 0), at(c.k, c.in, 0, c.out, -ear), ear);
            add(arc, true);
        } else {
            add({ c.k }, false);  // both sides flush: a square corner in the screen's corner
        }
    }

    d->AddConcavePolyFilled(pts.data(), (int)pts.size(), st.Bg(kPanel));

    // The outline, in runs: a run ends where the outline would go along the screen's edge.
    const ImU32 line = st.Bg(kAccent, 0.18f);
    std::vector<ImVec2> run;
    size_t n = pts.size(), start = 0;
    for (size_t i = 0; i < n; ++i) if (!drawn[i]) { start = i; break; }   // begin after a gap, if there is one
    for (size_t j = 0; j <= n; ++j) {
        size_t i = (start + j) % n;
        if (j > 0 && !drawn[i]) {
            if (run.size() > 1) d->AddPolyline(run.data(), (int)run.size(), line, ImDrawFlags_None, 1 * s);
            run.clear();
        }
        if (j < n || drawn[i]) run.push_back(pts[i]);
    }
    if (run.size() > 1) {
        bool closed = std::all_of(drawn.begin(), drawn.end(), [](bool b) { return b; });
        if (closed) run.pop_back();
        d->AddPolyline(run.data(), (int)run.size(), line, closed ? ImDrawFlags_Closed : ImDrawFlags_None, 1 * s);
    }
}

// List: Pulse's compact HUD (OverlayWindow.xaml, CompactPanel) without its PULSE badge (owner,
// 3 Nov 2026): radius 12, margin 14,10,14,9; one row per tile, each its own grid: the label in a
// stretching column (at least 80 wide), then the value (10 before, 4 after) and the unit (4 before)
// at the right edge.
void DrawList(ImDrawList* d, const std::vector<Row>& rows, const Style& st, float W, float H, float ax, float ay) {
    const float s = st.s;
    float labelH = Measure(g_medium, 11 * s, "Ag").y, valueH = Measure(g_bold, 12 * s, "0").y, unitH = Measure(g_medium, 10 * s, "Ag").y;
    float rowH = std::max(labelH, valueH) + 5 * s;  // 2.5 above and below each row
    float contentW = 0;
    for (const Row& r : rows) {
        float w = std::max(80 * s, Measure(g_medium, 11 * s, r.tile->label).x) + 10 * s +
                  Measure(g_bold, 12 * s, r.value.c_str()).x + 4 * s + 4 * s + Measure(g_medium, 10 * s, r.tile->unit).x;
        contentW = std::max(contentW, w);
    }
    float panelW = std::max(172 * s, 28 * s + contentW);
    float panelH = 10 * s + rows.size() * rowH + 9 * s;
    Edges e = Touching(ax, ay);
    ImVec2 p = Place(W, H, panelW, panelH, ax, ay, s, e), q(p.x + panelW, p.y + panelH);
    Panel(d, p, q, st, e);

    float x = p.x + 14 * s, right = q.x - 14 * s, y = p.y + 10 * s;
    for (const Row& r : rows) {
        float top = y + 2.5f * s, inner = rowH - 5 * s;
        float uw = Measure(g_medium, 10 * s, r.tile->unit).x;
        float vw = Measure(g_bold, 12 * s, r.value.c_str()).x;
        float unitX = right - uw, valueX = unitX - 4 * s - 4 * s - vw;
        Text(d, g_medium, 11 * s, ImVec2(x, top + (inner - labelH) * 0.5f), st.Col(kLabel), r.tile->label);
        Text(d, g_bold, 12 * s, ImVec2(valueX, top + (inner - valueH) * 0.5f), st.Col(StateColor(r.state)), r.value.c_str());
        Text(d, g_medium, 10 * s, ImVec2(unitX, top + inner - unitH - 1 * s), st.Col(kUnit), r.tile->unit);  // bottom, 1 up
        y += rowH;
    }
}

// Bar: the same readings on one straight line, the owner's "straight way". Same panel, outline,
// fonts and colours as the list; each reading is label, value and unit, with a thin divider between.
void DrawBar(ImDrawList* d, const std::vector<Row>& rows, const Style& st, float W, float H, float ax, float ay) {
    const float s = st.s;
    float labelH = Measure(g_medium, 11 * s, "Ag").y, valueH = Measure(g_bold, 12 * s, "0").y, unitH = Measure(g_medium, 10 * s, "Ag").y;
    float lineH = std::max(labelH, valueH);
    const float labelGap = 6 * s, unitGap = 3 * s, between = 11 * s;  // between a reading and a divider
    float contentW = 0;
    for (const Row& r : rows)
        contentW += Measure(g_medium, 11 * s, r.tile->label).x + labelGap + Measure(g_bold, 12 * s, r.value.c_str()).x +
                    unitGap + Measure(g_medium, 10 * s, r.tile->unit).x;
    if (rows.size() > 1) contentW += (rows.size() - 1) * (2 * between + 1 * s);
    float panelW = 28 * s + contentW, panelH = 8 * s + lineH + 8 * s;
    Edges e = Touching(ax, ay);
    ImVec2 p = Place(W, H, panelW, panelH, ax, ay, s, e), q(p.x + panelW, p.y + panelH);
    Panel(d, p, q, st, e);

    float x = p.x + 14 * s, top = p.y + 8 * s;
    for (size_t i = 0; i < rows.size(); ++i) {
        const Row& r = rows[i];
        if (i > 0) {
            x += between;
            d->AddRectFilled(ImVec2(x, top + lineH * 0.15f), ImVec2(x + 1 * s, top + lineH * 0.85f), st.Bg(kDivider));
            x += 1 * s + between;
        }
        Text(d, g_medium, 11 * s, ImVec2(x, top + (lineH - labelH) * 0.5f), st.Col(kLabel), r.tile->label);
        x += Measure(g_medium, 11 * s, r.tile->label).x + labelGap;
        Text(d, g_bold, 12 * s, ImVec2(x, top + (lineH - valueH) * 0.5f), st.Col(StateColor(r.state)), r.value.c_str());
        x += Measure(g_bold, 12 * s, r.value.c_str()).x + unitGap;
        Text(d, g_medium, 10 * s, ImVec2(x, top + lineH - unitH - 1 * s), st.Col(kUnit), r.tile->unit);
        x += Measure(g_medium, 10 * s, r.tile->unit).x;
    }
}

}  // namespace

// Plus Jakarta Sans, from the "fonts" folder beside this DLL (OFL, shipped with Pulse). Without
// it the overlay still works, in ImGui's own font, and says so in the log.
bool LoadFonts() {
    ImGuiIO& io = ImGui::GetIO();
    char dir[MAX_PATH];
    HMODULE self = nullptr;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCSTR>(&LoadFonts), &self);
    DWORD n = GetModuleFileNameA(self, dir, MAX_PATH);
    char* slash = n ? std::strrchr(dir, '\\') : nullptr;
    if (slash) *slash = 0;
    auto load = [&](const char* name) -> ImFont* {
        char path[MAX_PATH];
        std::snprintf(path, sizeof path, "%s\\fonts\\%s", dir, name);
        if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) return nullptr;
        return io.Fonts->AddFontFromFileTTF(path, 16.0f);
    };
    // The two weights the compact HUD uses: Medium for labels and units, Bold for values.
    g_medium = load("PlusJakartaSans-Medium.ttf");
    g_bold = load("PlusJakartaSans-Bold.ttf");
    bool all = g_medium && g_bold;
    if (!all) {
        ImFont* fallback = io.Fonts->AddFontDefault();
        if (!g_medium) g_medium = fallback;
        if (!g_bold) g_bold = fallback;
        Log("Plus Jakarta Sans not found beside the DLL; using ImGui's own font");
    }
    return all;
}

void BuildOverlay(float width, float height, float pixelsPerDip) {
    ImGuiIO& io = ImGui::GetIO();
    io.DisplaySize = ImVec2(width, height);
    io.DeltaTime = 1.0f / 60.0f;  // nothing animates; ImGui only needs a positive step
    ImGui::NewFrame();

    // Nothing is drawn without Pulse's readings, or while the player has it turned off or hidden:
    // a player should never see stale numbers or a message meant for us. Only in the game Pulse
    // names, the one in front: a game in the background, or one the player turned it off for,
    // leaves the window overlay to show instead, so there are never two.
    Block b;
    bool draw = ReadBlock(b) && b.enabled && b.tileCount > 0 &&
                (b.targetPid == 0 || b.targetPid == GetCurrentProcessId());
    ReportStatus(draw ? Drawing : Hidden);
    if (draw) {
        Style st;
        // The same size on screen as the window overlay: its scale, times the screen's scaling,
        // times any stretch from a game rendering at a different resolution than its window.
        st.s = std::clamp(b.scale, 0.5f, 3.0f) * std::clamp(pixelsPerDip, 0.5f, 6.0f);
        st.overlay = std::clamp(b.overlayOpacity, 0.0f, 1.0f);
        st.panel = std::clamp(b.panelOpacity, 0.0f, 1.0f);
        float ax = std::clamp(b.anchorX, 0.0f, 1.0f), ay = std::clamp(b.anchorY, 0.0f, 1.0f);
        std::vector<Row> rows = Rows(b);
        if (b.layout == Bar) DrawBar(ImGui::GetForegroundDrawList(), rows, st, width, height, ax, ay);
        else DrawList(ImGui::GetForegroundDrawList(), rows, st, width, height, ax, ay);
    }

    ImGui::Render();
}

// Device-independent pixels to buffer pixels for this game: the scaling Windows applies on the
// game's screen (what sizes the window overlay), times the stretch when the game renders at a
// different resolution than its window shows. Looked up once a second; it rarely changes.
float PixelsPerDip(IDXGISwapChain* chain, float bufferWidth) {
    static ULONGLONG nextLook = 0;
    static float cached = 1.0f;
    ULONGLONG now = GetTickCount64();
    if (now < nextLook) return cached;
    nextLook = now + 1000;
    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(chain->GetDesc(&desc)) || !desc.OutputWindow) return cached;
    UINT dpi = GetDpiForWindow(desc.OutputWindow);
    RECT client{};
    GetClientRect(desc.OutputWindow, &client);
    float stretch = client.right > 0 ? bufferWidth / float(client.right) : 1.0f;
    cached = (dpi ? dpi / 96.0f : 1.0f) * stretch;
    return cached;
}

// ImGui for this overlay: no files written, no mouse, no keyboard, Pulse's fonts.
void SetUpImGui() {
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NoMouse | ImGuiConfigFlags_NoKeyboard;
    io.MouseDrawCursor = false;
    LoadFonts();
}

void ShutDownImGui() {
    if (ImGui::GetCurrentContext()) ImGui::DestroyContext();
    g_medium = g_bold = nullptr;
}

}  // namespace pulse_ingame
