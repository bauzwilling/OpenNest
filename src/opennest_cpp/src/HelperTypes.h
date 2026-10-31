#pragma once

#include <vector>
#include <optional>
#include <memory>
#include <unordered_map>
#include <mutex>
#include <shared_mutex>
#include <cstdint>
#include <limits>
#include <cmath>
#include <algorithm>

#include "Point.h"
#include "NestConfig.h"
#include "clipper2/clipper.h"

namespace nest {

class NFP;

// ---------- DbCacheKey ----------
struct DbCacheKey {
    std::optional<int> A;
    std::optional<int> B;
    float ARotation = 0;
    float BRotation = 0;
    std::vector<std::shared_ptr<NFP>> nfp;   // owned copies (cloned on insert)
    int Type = 0;   // 0 = outer pair NFP, 1 = inner-fit entry (sheet/part source spaces overlap)
};

// ---------- NfpPair ----------
struct NfpPair {
    NFP* A = nullptr;
    NFP* B = nullptr;
    std::shared_ptr<NFP> nfp;
    float ARotation = 0;
    float BRotation = 0;
    int Asource = 0;
    int Bsource = 0;
};

// ---------- PlacementItem ----------
struct PlacementItem {
    int id = 0;
    float rotation = 0;
    double x = 0;
    double y = 0;
    int source = 0;
};

// ---------- SheetPlacementItem ----------
struct SheetPlacementItem {
    int sheetId = 0;
    int sheetSource = 0;
    std::vector<PlacementItem> sheetplacements;
    std::vector<PlacementItem> placements;
};

// ---------- PopulationItem ----------
struct PopulationItem {
    bool processing = false;  // C# uses 'object processing = null'; we use bool flag
    std::optional<double> fitness;
    // Total area of parts that could not be placed. Lexicographic primary objective in default mode
    // (see placementLess); left empty in faithful/parity mode so ordering falls back to fitness alone.
    std::optional<double> unplacedArea;
    // Total sheet cost (sheet selection modes 1/2 only): sum of opened sheet areas (1) or prices (2).
    // Secondary key between unplacedArea and fitness (see placementLess); sheetCount breaks near-ties in it.
    std::optional<double> totalCost;
    std::optional<int> sheetCount;
    std::vector<float> Rotation;
    std::vector<std::shared_ptr<NFP>> placements;
};

// ---------- SheetPlacement ----------
struct SheetPlacement {
    std::optional<double> fitness;
    // Total area of parts that could not be placed (default mode only; see PopulationItem::unplacedArea).
    std::optional<double> unplacedArea;
    // Total sheet cost and opened sheet count (sheet selection modes 1/2 only; see PopulationItem::totalCost).
    std::optional<double> totalCost;
    std::optional<int> sheetCount;
    std::vector<std::vector<SheetPlacementItem>> placements;
    int index = 0;
};

// Lexicographic placement-quality ordering used by the GA and best-nest selection:
//   primary key   = unplaced area  (fewer / smaller unplaced parts rank first)
//   cost key      = total sheet cost (sheet selection modes 1/2 only: total area or total price); costs
//                   within costTol (relative) of each other count as equal and the FEWER sheets wins
//   secondary key = fitness        (tighter packing ranks first)
// unplacedArea is populated only in default mode. When it is absent on either side the ordering falls
// back to fitness alone, which keeps faithful/parity mode byte-identical to the canonical C# behavior.
//
// Why a separate primary key instead of one blended scalar: the old default fitness folded an ~1e8
// unplaced penalty into the same number as the packing terms, so once any part was unplaceable the
// penalty swamped the tightness signal and the GA could no longer improve the layout with more
// iterations. Splitting the objectives lets it minimize unplaced area first, then optimize tightness
// within that tier — so a forced-unplaced part (too big for any sheet) no longer degrades the rest.
inline bool placementLess(const std::optional<double>& fitA, const std::optional<double>& unplacedA,
                          const std::optional<double>& fitB, const std::optional<double>& unplacedB,
                          const std::optional<double>& costA = std::nullopt,
                          const std::optional<double>& costB = std::nullopt,
                          const std::optional<int>& countA = std::nullopt,
                          const std::optional<int>& countB = std::nullopt,
                          double costTol = 0.0) {
    if (unplacedA.has_value() && unplacedB.has_value()) {
        double ua = unplacedA.value(), ub = unplacedB.value();
        double tol = 1e-6 * std::max(1.0, std::max(std::fabs(ua), std::fabs(ub)));
        if (std::fabs(ua - ub) > tol) return ua < ub;
    }
    // Sheet-selection cost tier (absent outside modes 1/2, so ordering there is unchanged).
    if (costA.has_value() && costB.has_value()) {
        double ca = costA.value(), cb = costB.value();
        double tol = std::max(1e-9, costTol) * std::max(1.0, std::max(std::fabs(ca), std::fabs(cb)));
        if (std::fabs(ca - cb) > tol) return ca < cb;
        if (countA.has_value() && countB.has_value() && countA.value() != countB.value())
            return countA.value() < countB.value();
    }
    double fa = fitA.has_value() ? fitA.value() : std::numeric_limits<double>::max();
    double fb = fitB.has_value() ? fitB.value() : std::numeric_limits<double>::max();
    return fa < fb;
}

// ---------- Sheet selection: choosing the next sheet ----------
// One candidate sheet type, filled on trial: lower score is better (min area: -utilization; min price: price
// per placed part area), `area` is the sheet's area.
struct SheetCandidate {
    double score;
    double area;
};

// Pick the sheet to open: every candidate whose score is within `tol` (relative to the best score) of the
// best counts as tied, and among those the LARGEST sheet wins (fewer sheets for the same material or money),
// then the earliest candidate. Returns the index into `c`, or -1 when empty.
inline int pickSheetCandidate(const std::vector<SheetCandidate>& c, double tol) {
    if (c.empty()) return -1;
    double best = c[0].score;
    for (auto& x : c) best = std::min(best, x.score);
    double band = std::max(1e-12, std::max(0.0, tol) * std::fabs(best));
    int pick = -1;
    for (int i = 0; i < static_cast<int>(c.size()); i++) {
        if (c[i].score - best > band) continue;
        if (pick < 0 || c[i].area > c[pick].area * (1 + 1e-12)) pick = i;
    }
    return pick;
}

// ---------- NestItem ----------
struct NestItem {
    std::shared_ptr<NFP> Polygon;
    int Quantity = 0;
    bool IsSheet = false;
};

// ---------- DataInfo ----------
struct DataInfo {
    int index = 0;
    std::vector<std::shared_ptr<NFP>> sheets;
    std::vector<int> sheetids;
    std::vector<int> sheetsources;
    std::vector<std::vector<std::shared_ptr<NFP>>> sheetchildren;
    PopulationItem individual;
    NestConfig config;
    std::vector<int> ids;
    std::vector<int> sources;
    std::vector<std::vector<std::shared_ptr<NFP>>> children;
};

// ---------- ClipCacheItem ----------
struct ClipCacheItem {
    int index = 0;
    Clipper2Lib::Paths64 nfpp;
};

// ---------- NfpCache / dbCache ----------
// Forward declare for circular reference
class dbCache;

class NfpCache {
public:
    NfpCache();

    // Move constructor/assignment must update dbCache's back-pointer
    NfpCache(NfpCache&& other) noexcept;
    NfpCache& operator=(NfpCache&& other) noexcept;

    // No copy
    NfpCache(const NfpCache&) = delete;
    NfpCache& operator=(const NfpCache&) = delete;

    std::unordered_map<uint64_t, std::vector<std::shared_ptr<NFP>>> nfpCache;
    std::unique_ptr<dbCache> db;
};

class dbCache {
public:
    explicit dbCache(NfpCache* w) : window(w) {}

    void setWindow(NfpCache* w) { window = w; }

    bool has(const DbCacheKey& obj);
    void insert(const DbCacheKey& obj, bool inner = false);
    std::vector<std::shared_ptr<NFP>> find(const DbCacheKey& obj, bool inner = false);

private:
    uint64_t getKey(const DbCacheKey& obj) const;

    NfpCache* window;
    // shared_mutex: concurrent has()/find() (the placeParts hot path) take a shared lock and run in
    // parallel; only insert() takes an exclusive lock. Critical for the parallel-population place phase.
    std::shared_mutex lockobj;
};

} // namespace nest
