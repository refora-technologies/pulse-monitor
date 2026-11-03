// The game's frame rate and 1% low, from its own frames, worked out exactly the way Pulse's
// FpsService works them out from PresentMon, so the in-game overlay and the window overlay never
// disagree about the same game. The rules, with Pulse's reasons, are in Services/FpsService.cs:
//
//   - frames are kept per swap chain, and the busiest chain over the last second is the one being
//     played; another chain takes over only when it is 1.25 times busier for 4 checks in a row
//   - FPS = 1000 / the mean frame time over the last second of that chain, from at least 2 frames
//   - 1% low = the nearest-rank 99th percentile of that chain's frame times over the last 2,000
//     frames or 30 seconds, shown once there are 2,000 frames, or 200 spanning 3 seconds
//   - checked a few times a second, never per frame; no frames for 2 seconds means no reading
#include "internal.h"

#include <algorithm>
#include <cmath>
#include <deque>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace pulse_ingame {
namespace {

constexpr double kFrameWindowMs = 1000;
constexpr double kStaleAfterMs = 2000;
constexpr double kLowWindowMs = 30000;
constexpr size_t kLowMaxSamples = 2000;
constexpr size_t kLowMinSamples = 200;
constexpr double kLowMinSpanMs = 3000;
constexpr int kChainSwitchMargin = 4;
constexpr double kChainSwitchLead = 1.25;
constexpr double kRecomputeEveryMs = 250;

struct Sample { double at; double ms; };
struct Chain { double lastPresent = 0; std::deque<Sample> samples; };

std::mutex g_lock;
std::unordered_map<const void*, Chain> g_chains;
std::deque<Sample> g_low;  // the dominant chain's long history
const void* g_dominant = nullptr;
const void* g_candidate = nullptr;
int g_candidateWins = 0;
double g_lastAnyPresent = 0, g_lastRecompute = 0;
bool g_hasFps = false, g_hasLow = false;
float g_fps = 0, g_low1 = 0;

#ifdef PULSE_INGAME_TESTING
double g_testClock = -1;  // test builds: a clock the tests set, so frame times are exact
#endif

double NowMs() {
#ifdef PULSE_INGAME_TESTING
    if (g_testClock >= 0) return g_testClock;
#endif
    static LARGE_INTEGER frequency = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return double(c.QuadPart) * 1000.0 / double(frequency.QuadPart);
}

void SwitchChain(const void* key) {
    g_dominant = key;
    g_candidate = nullptr;
    g_candidateWins = 0;
    g_low.clear();  // the long history belonged to something else
    g_hasLow = false;
}

void UpdateDominant(const void* busiest, size_t busiestCount) {
    if (!busiest) return;  // nothing presenting: not a switch, the history ages out by itself
    if (!g_dominant) { g_dominant = busiest; g_candidate = nullptr; g_candidateWins = 0; return; }
    if (busiest == g_dominant) { g_candidate = nullptr; g_candidateWins = 0; return; }
    auto incumbent = g_chains.find(g_dominant);
    if (incumbent == g_chains.end() || incumbent->second.samples.empty()) { SwitchChain(busiest); return; }
    if (busiestCount < incumbent->second.samples.size() * kChainSwitchLead) { g_candidate = nullptr; g_candidateWins = 0; return; }
    if (busiest != g_candidate) { g_candidate = busiest; g_candidateWins = 1; return; }
    if (++g_candidateWins >= kChainSwitchMargin) SwitchChain(busiest);
}

void Recompute(double now) {
    const void* busiest = nullptr;
    size_t busiestCount = 0;
    for (auto it = g_chains.begin(); it != g_chains.end();) {
        auto& s = it->second.samples;
        while (!s.empty() && now - s.front().at > kFrameWindowMs) s.pop_front();
        if (s.empty() && now - it->second.lastPresent > kFrameWindowMs) { it = g_chains.erase(it); continue; }
        if (s.size() > busiestCount) { busiest = it->first; busiestCount = s.size(); }
        ++it;
    }
    UpdateDominant(busiest, busiestCount);

    auto chain = g_chains.find(g_dominant);
    if (chain == g_chains.end() || chain->second.samples.size() < 2) {
        g_hasFps = false;
    } else {
        double total = 0;
        for (const Sample& x : chain->second.samples) total += x.ms;
        double mean = total / chain->second.samples.size();
        g_hasFps = mean > 0;
        g_fps = g_hasFps ? float(1000.0 / mean) : 0;
    }

    while (!g_low.empty() && now - g_low.front().at > kLowWindowMs) g_low.pop_front();
    double span = g_low.empty() ? 0 : now - g_low.front().at;
    bool enough = g_low.size() >= kLowMaxSamples || (g_low.size() >= kLowMinSamples && span >= kLowMinSpanMs);
    if (!enough) { g_hasLow = false; return; }

    static std::vector<double> times;  // kept between calls: this runs four times a second
    times.clear();
    for (const Sample& x : g_low) times.push_back(x.ms);
    std::sort(times.begin(), times.end());
    size_t count = times.size();
    // Nearest rank: the 99th percentile is the value at rank ceil(0.99 n), one lower as an index.
    long long rank = (long long)std::ceil(0.99 * double(count)) - 1;
    size_t boundary = (size_t)std::clamp<long long>(rank, 0, (long long)count - 1);
    double boundaryMs = times[boundary];
    g_hasLow = boundaryMs > 0;
    g_low1 = g_hasLow ? float(1000.0 / boundaryMs) : 0;
}

}  // namespace

void OnPresent(const void* chainKey) {
    double now = NowMs();
    std::lock_guard<std::mutex> hold(g_lock);
    if (g_lastAnyPresent && now - g_lastAnyPresent > kStaleAfterMs) {
        // Frames stopped for two seconds: start again rather than average across the gap.
        g_chains.clear(); g_low.clear(); g_dominant = nullptr; g_candidate = nullptr; g_candidateWins = 0;
        g_hasFps = g_hasLow = false;
    }
    g_lastAnyPresent = now;

    Chain& c = g_chains[chainKey];
    if (c.lastPresent > 0) {
        Sample s{ now, now - c.lastPresent };
        c.samples.push_back(s);
        if (chainKey == g_dominant) {
            g_low.push_back(s);
            while (g_low.size() > kLowMaxSamples) g_low.pop_front();
        }
    }
    c.lastPresent = now;

    if (now - g_lastRecompute >= kRecomputeEveryMs) {
        g_lastRecompute = now;
        Recompute(now);
    }
}

#ifdef PULSE_INGAME_TESTING
void TestSetClock(double ms) { g_testClock = ms; }
double TestReadingAgeMs() { std::lock_guard<std::mutex> hold(g_lock); return NowMs() - g_lastRecompute; }
double TestReadingClockMs() { std::lock_guard<std::mutex> hold(g_lock); return g_lastRecompute; }
double TestLowFromClockMs() { std::lock_guard<std::mutex> hold(g_lock); return g_low.empty() ? 0 : g_low.front().at; }
void TestReset() {
    std::lock_guard<std::mutex> hold(g_lock);
    g_chains.clear(); g_low.clear(); g_dominant = g_candidate = nullptr; g_candidateWins = 0;
    g_lastAnyPresent = g_lastRecompute = 0; g_hasFps = g_hasLow = false;
}
#endif

const void* DominantChain() {
    std::lock_guard<std::mutex> hold(g_lock);
    return g_dominant;
}

bool CurrentFps(float& out) {
    std::lock_guard<std::mutex> hold(g_lock);
    if (!g_hasFps || NowMs() - g_lastAnyPresent > kStaleAfterMs) return false;
    out = g_fps;
    return true;
}

bool OnePercentLow(float& out) {
    std::lock_guard<std::mutex> hold(g_lock);
    if (!g_hasLow || NowMs() - g_lastAnyPresent > kStaleAfterMs) return false;
    out = g_low1;
    return true;
}

}  // namespace pulse_ingame
