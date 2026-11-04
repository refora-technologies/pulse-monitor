// The one thing Pulse and its in-game part share: a small block of memory that Pulse writes and
// the in-game part only reads. Everything arrives decided: values formatted, colours chosen as a
// state, bars as a fraction, the layout as Pulse's settings have it. The in-game part draws what
// it is given, the way the window overlay would, and makes no decisions of its own; the frame
// rate and 1% low are the only numbers it works out, from the game's own frames.
//
// Pulse runs as administrator and games do not, so Pulse creates the block with an explicit
// security descriptor: the signed-in user may read it, nobody but Pulse may write it.
//
// Torn reads are prevented with a sequence number: Pulse makes it odd before writing and even
// after, and a reader keeps a copy only if it saw the same even number before and after copying.
#pragma once
#include <cstddef>
#include <cstdint>

namespace pulse_ingame {

constexpr wchar_t kBlockName[] = L"Local\\PulseIngame-v3";
constexpr uint32_t kMagic = 0x504C5345;  // "PLSE"
constexpr uint32_t kVersion = 3;
constexpr int kMaxTiles = 24;

// A value's colour, as the window overlay picks it.
enum State : uint8_t { Neutral = 0, Normal = 1, Warn = 2, Danger = 3 };

// How the readings are laid out: one per line, or all on one straight line.
enum Layout : uint8_t { List = 0, Bar = 1 };

struct Tile {
    char id[16];        // "cpu_temp", "fps", ...: the in-game part fills fps and fps_1low itself
    char label[24];     // "CPU Temp", UTF-8
    char value[24];     // "62", already formatted; "--" or "asleep" when there is no reading
    char unit[12];      // "°C"
    uint8_t state;      // State
    uint8_t hasBar;     // 1: draw a bar
    uint8_t reserved[2];
    float barFraction;  // 0..1
};

struct Block {
    uint32_t magic;
    uint32_t version;
    volatile uint32_t sequence;  // odd while Pulse is writing
    uint32_t tileCount;
    uint64_t writtenAtMs;        // GetTickCount64 when written; stale readings are not shown
    uint8_t enabled;             // 0: draw nothing (the player turned it off, or hid the overlay)
    uint8_t layout;              // Layout
    uint8_t reserved4[2];
    float scale;                 // the overlay's size, 1.0 normal
    float anchorX, anchorY;      // where the panel sits: 0 left/top, 1 right/bottom, between = between
    float panelOpacity;          // the background's opacity (readings stay solid)
    float overlayOpacity;        // the whole overlay's opacity
    uint32_t targetPid;          // the one game that may draw: the game in front. 0: any (test tools only)
    uint32_t reserved[5];
    Tile tiles[kMaxTiles];
};

// The layout Pulse writes byte for byte (Services/InGame/InGameBlockWriter.cs). Checked here so a
// change on either side cannot pass unnoticed.
static_assert(sizeof(Tile) == 84, "Tile layout changed: update Pulse's writer");
static_assert(offsetof(Block, enabled) == 24 && offsetof(Block, scale) == 28 && offsetof(Block, targetPid) == 48 &&
              offsetof(Block, tiles) == 72,
              "Block layout changed: update Pulse's writer");
static_assert(sizeof(Block) == 2088, "Block layout changed: update Pulse's writer");

// What the in-game part tells Pulse about itself, per game: whether it is drawing, and if not, why.
// The game writes this one, so Pulse treats it as a hint: it decides only whether to hide the
// window overlay and what the status line says, never anything about the game.
constexpr wchar_t kStatusPrefix[] = L"Local\\PulseIngameStatus-";  // + the game's process id
constexpr uint32_t kStatusMagic = 0x50535453;  // "PSTS"

enum StatusState : uint32_t {
    Starting = 0,     // loaded, not drawn yet
    Drawing = 1,      // drawing Pulse's readings in this game
    Hidden = 2,       // working, but nothing to draw (turned off, overlay hidden, or no readings)
    NotDirectX = 3,   // the game shows its frames some other way (not DirectX 11 or 12)
    SwitchedOff = 4,  // an error inside the overlay; it stepped aside for this session
};

struct Status {
    uint32_t magic;
    uint32_t state;      // StatusState
    uint64_t updatedMs;  // GetTickCount64; Pulse treats an old one as "not running"
};
static_assert(sizeof(Status) == 16, "Status layout changed: update Pulse's reader");

}  // namespace pulse_ingame
