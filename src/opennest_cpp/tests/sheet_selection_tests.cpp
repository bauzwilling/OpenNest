// sheet_selection_tests — checks nfp_nest_v2 (sheet types, quantities, prices, selection modes)
// against docs/sheet_selection.md. Built with -DNFP_BUILD_TESTS=ON; exits non-zero on failure.
//
// Scenarios use plain rectangles so the expected sheet choice can be worked out by hand:
// a 200x100 part (area 20 000) fills a 1000x500 sheet 25 to a sheet (utilization 1.0) and a
// 450x320 sheet 6 to a sheet (utilization 0.83).

#include "capi/nfp_nest_capi.h"
#include "HelperTypes.h"

#include <cmath>
#include <cstdio>
#include <map>
#include <string>
#include <vector>

namespace {

int g_failures = 0;
#define CHECK(cond, ...)                                                        \
    do {                                                                        \
        if (!(cond)) {                                                          \
            g_failures++;                                                       \
            std::printf("  FAIL %s:%d: %s — ", __FILE__, __LINE__, #cond);      \
            std::printf(__VA_ARGS__);                                           \
            std::printf("\n");                                                  \
        }                                                                       \
    } while (0)

struct SheetType {
    double w, h;
    int quantity;       // -1 = unlimited
    double price = 0;   // 0 = none
};

struct Result {
    int rc = 0;
    int placed = 0;
    int nSheets = 0;
    std::vector<int> sheetId, sheetType, partIndex;
    std::vector<double> tx, ty, angle;
    double totalArea = 0;     // sum of opened sheet areas
    double totalPrice = 0;    // sum of opened sheet prices
    std::map<int, int> perType;
};

// Serial evaluation by default: it is bit-for-bit reproducible. In parallel evaluation the threads race to
// fill the shared NFP cache, and a cached fit differs from a fresh one in the last floating-point digits, so
// two identical parallel runs can end in different (equally valid) layouts.
NfpParams params(int seed = 5, int mode = 1, int parallel = 0) {
    NfpParams p{};
    p.placementType = 1;      // gravity
    p.rotations = 4;
    p.mutationRate = 10;
    p.populationSize = 6;
    p.seed = seed;
    p.curveTolerance = 0.72;
    p.clipperScale = 1e7;
    p.rotationLimit = 360;
    p.clipByRects = 1;
    p.mode = mode;
    p.generations = 3;
    p.numSeeds = 2;
    p.useParallel = parallel;
    p.edgeSamples = -1;
    p.compactionPasses = -1;
    p.tryAllRotations = -1;
    return p;
}

void rect(std::vector<double>& xy, double w, double h) {
    double r[] = {0, 0, w, 0, w, h, 0, h};
    xy.insert(xy.end(), r, r + 8);
}

Result runV2(int partCount, const std::vector<SheetType>& types, int selection, const NfpParams& p,
             double partW = 200, double partH = 100, double tieTol = 0.01) {
    std::vector<int> pvc{4}, pqty{partCount}, phc{0};
    std::vector<double> pxy;
    rect(pxy, partW, partH);

    std::vector<int> svc, shc, qty;
    std::vector<double> sxy, price;
    bool anyPrice = false;
    for (auto& t : types) {
        svc.push_back(4);
        rect(sxy, t.w, t.h);
        shc.push_back(0);
        qty.push_back(t.quantity);
        price.push_back(t.price);
        anyPrice |= t.price > 0;
    }

    Result r;
    r.tx.resize(partCount); r.ty.resize(partCount); r.angle.resize(partCount);
    r.sheetId.resize(partCount); r.partIndex.resize(partCount); r.sheetType.assign(partCount, -1);
    double fitness = 0;
    r.rc = nfp_nest_v2(1, pvc.data(), pxy.data(), pqty.data(), nullptr, phc.data(), nullptr, nullptr,
                       static_cast<int>(types.size()), svc.data(), sxy.data(), shc.data(), nullptr, nullptr,
                       qty.data(), anyPrice ? price.data() : nullptr, selection, tieTol, &p,
                       r.tx.data(), r.ty.data(), r.angle.data(), r.sheetId.data(), r.partIndex.data(),
                       r.sheetType.data(), &r.nSheets, &fitness);
    r.placed = r.rc;
    if (r.rc >= 0) {
        for (int s = 0; s < r.nSheets; s++) {
            int t = r.sheetType[s];
            if (t < 0 || t >= static_cast<int>(types.size())) continue;
            r.perType[t]++;
            r.totalArea += types[t].w * types[t].h;
            r.totalPrice += types[t].price;
        }
    }
    return r;
}

// Same job through the v1 entry, with `copies[i]` sheets of size (w[i], h[i]) in that order.
Result runV1(int partCount, const std::vector<std::pair<double, double>>& sheets, const NfpParams& p) {
    std::vector<int> pvc{4}, pqty{partCount}, phc{0};
    std::vector<double> pxy;
    rect(pxy, 200, 100);
    std::vector<int> svc, shc;
    std::vector<double> sxy;
    for (auto& s : sheets) { svc.push_back(4); rect(sxy, s.first, s.second); shc.push_back(0); }
    Result r;
    r.tx.resize(partCount); r.ty.resize(partCount); r.angle.resize(partCount);
    r.sheetId.resize(partCount); r.partIndex.resize(partCount);
    double fitness = 0;
    r.rc = nfp_nest(1, pvc.data(), pxy.data(), pqty.data(), nullptr, phc.data(), nullptr, nullptr,
                    static_cast<int>(sheets.size()), svc.data(), sxy.data(), shc.data(), nullptr, nullptr,
                    &p, r.tx.data(), r.ty.data(), r.angle.data(), r.sheetId.data(), r.partIndex.data(),
                    &r.nSheets, &fitness);
    r.placed = r.rc;
    return r;
}

bool sameLayout(const Result& a, const Result& b) {
    if (a.placed != b.placed || a.nSheets != b.nSheets) return false;
    for (size_t k = 0; k < a.tx.size(); k++) {
        if (a.sheetId[k] != b.sheetId[k] || a.tx[k] != b.tx[k] || a.ty[k] != b.ty[k] || a.angle[k] != b.angle[k])
            return false;
    }
    return true;
}

// Every placed instance points at an opened sheet 0..n-1, and every opened sheet has a valid type.
void checkOutputs(const Result& r, int typeCount, const char* what) {
    for (size_t k = 0; k < r.sheetId.size(); k++) {
        CHECK(r.sheetId[k] >= -1 && r.sheetId[k] < r.nSheets, "%s: instance %zu sheet %d of %d", what, k,
              r.sheetId[k], r.nSheets);
    }
    for (int s = 0; s < r.nSheets; s++) {
        CHECK(r.sheetType[s] >= 0 && r.sheetType[s] < typeCount, "%s: opened sheet %d has type %d", what, s,
              r.sheetType[s]);
    }
}

void test_v1_parity() {
    std::printf("v1 parity\n");
    NfpParams p = params();
    // Each sheet its own type, quantity 1, list order == nfp_nest with the same sheets.
    std::vector<std::pair<double, double>> v1Sheets{{1000, 500}, {450, 320}, {1000, 500}, {450, 320}};
    std::vector<SheetType> types;
    for (auto& s : v1Sheets) types.push_back({s.first, s.second, 1});
    Result a = runV1(40, v1Sheets, p);
    Result b = runV2(40, types, 0, p);
    CHECK(a.rc >= 0 && b.rc >= 0, "rc v1=%d v2=%d", a.rc, b.rc);
    // v1 numbers sheets by input index; v2 by opening order — in list order they coincide.
    CHECK(sameLayout(a, b), "one-type-per-sheet v2 differs from v1 (placed %d vs %d, sheets %d vs %d)",
          a.placed, b.placed, a.nSheets, b.nSheets);

    // One type with quantity 3 vs three identical v1 sheets. The copies of a type share one inner-fit
    // cache entry, and a cached inner fit differs from a fresh one in the last floating-point digits, so
    // in the default modes the GA can drift to a different (equally valid) layout. Faithful mode is exact;
    // the default mode must still place as many parts on as many sheets.
    for (int mode : {0, 1}) {
        NfpParams pm = params(5, mode);
        Result c = runV1(60, {{1000, 500}, {1000, 500}, {1000, 500}}, pm);
        Result d = runV2(60, {{1000, 500, 3}}, 0, pm);
        if (mode == 0) CHECK(sameLayout(c, d), "faithful: quantity-3 type differs from 3 v1 sheets");
        CHECK(c.placed == d.placed && c.nSheets == d.nSheets, "mode %d: placed %d vs %d, sheets %d vs %d", mode,
              c.placed, d.placed, c.nSheets, d.nSheets);
    }
}

void test_min_area_right_sizes_last_sheet() {
    std::printf("min area: right-sizes the last sheet\n");
    NfpParams p = params();
    std::vector<SheetType> types{{1000, 500, -1}, {450, 320, -1}};
    // 55 parts: 2 full big sheets (50) + 5 left; 5 fit the small sheet (util 0.69 vs 0.2 on a big one).
    Result order = runV2(55, types, 0, p);
    Result area = runV2(55, types, 1, p);
    CHECK(order.placed == 55 && area.placed == 55, "placed order=%d area=%d", order.placed, area.placed);
    checkOutputs(area, 2, "min area");
    CHECK(area.totalArea < order.totalArea, "min-area total %.0f not below list-order total %.0f",
          area.totalArea, order.totalArea);
    CHECK(area.nSheets > 0 && area.sheetType[area.nSheets - 1] == 1, "last opened sheet is type %d, want 1 (small)",
          area.nSheets > 0 ? area.sheetType[area.nSheets - 1] : -1);
    CHECK(order.perType[1] == 0, "list order should never reach the small type, used %d", order.perType[1]);
}

void test_limited_stock_is_respected() {
    std::printf("limited stock\n");
    NfpParams p = params();
    // A perfect-fit offcut (400x300: 6 parts, util 1.0, smaller than the big sheet) limited to 2,
    // then unlimited big sheets.
    std::vector<SheetType> types{{1000, 500, -1}, {400, 300, 2}};
    Result r = runV2(60, types, 1, p);
    CHECK(r.placed == 60, "placed %d of 60", r.placed);
    checkOutputs(r, 2, "limited");
    CHECK(r.perType[1] <= 2, "offcut used %d times, stock is 2", r.perType[1]);
    CHECK(r.perType[1] >= 1, "the better-fitting offcut was never used");

    // Everything limited and too little stock: the rest is unplaced, never more sheets than stock.
    Result s = runV2(60, {{1000, 500, 1}, {400, 300, 1}}, 1, p);
    CHECK(s.perType[0] <= 1 && s.perType[1] <= 1, "used %d big and %d offcuts, stock 1 each", s.perType[0],
          s.perType[1]);
    CHECK(s.placed < 60, "60 parts cannot fit 1 big + 1 offcut, placed %d", s.placed);

    // List order honors limits too.
    Result t = runV2(60, {{1000, 500, 1}, {1000, 500, -1}}, 0, p);
    CHECK(t.perType[0] <= 1, "list order used type 0 %d times, stock 1", t.perType[0]);
    CHECK(t.nSheets > 0 && t.sheetType[0] == 0, "list order should open type 0 first");
}

void test_min_price() {
    std::printf("min price\n");
    NfpParams p = params();
    // Same size; the second is cheaper -> only the cheap one is used.
    Result r = runV2(60, {{1000, 500, -1, 100}, {1000, 500, -1, 80}}, 2, p);
    CHECK(r.placed == 60, "placed %d", r.placed);
    checkOutputs(r, 2, "min price");
    CHECK(r.perType[0] == 0 && r.perType[1] > 0, "used %d expensive and %d cheap sheets", r.perType[0], r.perType[1]);

    // Cheap one limited to 1: one cheap sheet, the rest expensive.
    Result s = runV2(60, {{1000, 500, -1, 100}, {1000, 500, 1, 80}}, 2, p);
    CHECK(s.perType[1] == 1, "cheap limited sheet used %d times, want 1", s.perType[1]);
    CHECK(s.placed == 60, "placed %d", s.placed);

    // A small cheap sheet loses to a big one with a lower price per part area: at full packing the big
    // 1000x500 at 120 holds 25 parts (4.8 per part) and the small 450x320 at 60 holds 6 (10 per part).
    // The short test run packs neither fully, but the ratio stays far apart, so the small one never wins.
    Result t = runV2(50, {{1000, 500, -1, 120}, {450, 320, -1, 60}}, 2, p);
    CHECK(t.placed == 50, "placed %d", t.placed);
    CHECK(t.perType[0] > 0 && t.perType[1] == 0, "used %d big and %d small, want only big", t.perType[0],
          t.perType[1]);
}

void test_errors() {
    std::printf("errors\n");
    NfpParams p = params();
    CHECK(runV2(5, {{1000, 500, 0}}, 1, p).rc == NFP_ERR_INVALID_QUANTITY, "quantity 0");
    CHECK(runV2(5, {{1000, 500, -2}}, 1, p).rc == NFP_ERR_INVALID_QUANTITY, "quantity -2");
    CHECK(runV2(5, {{1000, 500, -1}}, 3, p).rc == NFP_ERR_INVALID_SELECTION, "selection 3");
    CHECK(runV2(5, {{1000, 500, -1}}, 2, p).rc == NFP_ERR_INVALID_PRICE, "selection 2 without prices");
    CHECK(runV2(5, {{1000, 500, -1, 10}, {450, 320, -1, 0}}, 2, p).rc == NFP_ERR_INVALID_PRICE, "price 0");
    CHECK(runV2(5, {{1000, 500, -1}}, 1, p, 200, 100, -0.1).rc == NFP_ERR_INVALID_TOLERANCE, "negative tolerance");
}

void test_determinism_and_faithful() {
    std::printf("determinism / faithful\n");
    std::vector<SheetType> types{{1000, 500, -1}, {450, 320, -1}};
    Result a = runV2(55, types, 1, params(9));
    Result b = runV2(55, types, 1, params(9));
    CHECK(sameLayout(a, b), "same seed, different layouts");
    // Parallel evaluation: same outcome counts (the exact layout may differ, see params()).
    Result c = runV2(55, types, 1, params(9, 1, 1));
    CHECK(c.rc >= 0 && c.placed == a.placed, "parallel placed %d, serial %d", c.placed, a.placed);
    checkOutputs(c, 2, "parallel");

    // Faithful mode (params.mode 0) ignores selection: same as list order.
    Result f0 = runV2(55, types, 0, params(9, 0, 0));
    Result f1 = runV2(55, types, 1, params(9, 0, 0));
    CHECK(sameLayout(f0, f1), "faithful mode should ignore sheet selection");
}

void test_ties_prefer_the_larger_sheet() {
    std::printf("ties: larger sheet, near-ties, fewer sheets\n");
    using nest::SheetCandidate;
    using nest::pickSheetCandidate;
    // Exact tie on utilization: the larger sheet wins, regardless of order.
    CHECK(pickSheetCandidate({{-1.0, 100}, {-1.0, 200}}, 0.0) == 1, "exact tie should pick the larger sheet");
    CHECK(pickSheetCandidate({{-1.0, 200}, {-1.0, 100}}, 0.0) == 0, "exact tie should pick the larger sheet");
    // Near-tie: 0.995 vs 1.0 utilization is within 1%, so the larger sheet wins; with 0 tolerance it doesn't.
    CHECK(pickSheetCandidate({{-0.995, 200}, {-1.0, 100}}, 0.01) == 0, "near-tie within 1% should pick the larger sheet");
    CHECK(pickSheetCandidate({{-0.995, 200}, {-1.0, 100}}, 0.0) == 1, "with 0 tolerance the better score wins");
    // Clearly better (0.9 vs 1.0): the better score wins even though the other sheet is larger.
    CHECK(pickSheetCandidate({{-0.9, 200}, {-1.0, 100}}, 0.01) == 1, "a clearly better score wins");
    // Min price (positive scores): 40.2 vs 40 per unit is within 1% -> larger sheet.
    CHECK(pickSheetCandidate({{40.0, 100}, {40.2, 300}}, 0.01) == 1, "price near-tie should pick the larger sheet");
    CHECK(pickSheetCandidate({}, 0.01) == -1, "no candidates");

    // Whole layouts: equal (within tolerance) total cost -> fewer sheets is better.
    std::optional<double> fit = 1.0, unpl = 0.0;
    CHECK(nest::placementLess(fit, unpl, fit, unpl, 100.0, 100.5, 15, 30, 0.01), "fewer sheets should win a near-tie on cost");
    CHECK(!nest::placementLess(fit, unpl, fit, unpl, 100.0, 100.5, 30, 15, 0.01), "more sheets should lose a near-tie on cost");
    CHECK(nest::placementLess(fit, unpl, fit, unpl, 100.0, 110.0, 30, 15, 0.01), "a clearly lower cost still wins");

    // The 30-small vs 15-big case: a 500x250 part fills a 500x500 sheet 2 to a sheet and a 1000x500 sheet
    // 4 to a sheet, both at utilization 1.0. 60 parts: 30 small or 15 big, same area -> 15 big.
    Result r = runV2(60, {{500, 500, -1}, {1000, 500, -1}}, 1, params(), 500, 250);
    CHECK(r.placed == 60, "placed %d of 60", r.placed);
    CHECK(r.perType[0] == 0, "used %d small and %d big sheets, want only big", r.perType[0], r.perType[1]);
}

} // namespace

int main() {
    test_v1_parity();
    test_min_area_right_sizes_last_sheet();
    test_limited_stock_is_respected();
    test_min_price();
    test_errors();
    test_ties_prefer_the_larger_sheet();
    test_determinism_and_faithful();
    if (g_failures) {
        std::printf("%d check(s) FAILED\n", g_failures);
        return 1;
    }
    std::printf("all sheet selection tests passed\n");
    return 0;
}
