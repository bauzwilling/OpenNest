#pragma once

#include <vector>
#include <memory>
#include <optional>
#include <cmath>
#include <algorithm>

#include "Point.h"
#include "NFP.h"
#include "HelperTypes.h"
#include "NestConfig.h"
#include "GeometryUtil.h"
#include "NfpWorker.h"
#include "GeneticAlgorithm.h"
#include "Simplify.h"
#include "ClipperUtil.h"
#include "clipper2/clipper.h"

namespace nest {

class NestingEngine {
public:
    static NestConfig Config;    // shared config (set once for static helpers)
    NestConfig config;            // per-instance config (for GA seed, placement)

    NestingEngine() = default;

    // --- Polygon processing ---
    static NFP simplifyFunction(const NFP& polygon, bool inside, const NestConfig& config);
    static void offsetTree(NFP& t, double offset, const NestConfig& config, std::optional<bool> inside = std::nullopt);
    static std::vector<NFP> polygonOffsetDeepNest(const NFP& polygon, double offset);
    static NFP cleanPolygon2(const NFP& polygon);
    static NFP cloneTree(const NFP& tree);

    // --- Clipper conversion ---
    static Clipper2Lib::Path64 svgToClipper(const NFP& polygon);
    static NFP clipperToSvg(const Clipper2Lib::Path64& polygon);

    // --- Worker management ---
    void launchWorkers(const std::vector<NestItem>& parts);
    // Evaluate ALL pending candidates of the current GA generation concurrently (shared NFP cache).
    // One call advances a whole generation; intended to be invoked once per generation. Equivalent
    // result to calling launchWorkers populationSize times, but far faster for exact mode.
    void launchWorkersParallel(const std::vector<NestItem>& parts);
    void ResponseProcessor(SheetPlacement payload);

    // --- Instance state ---
    NfpWorker background;
    std::unique_ptr<GeneticAlgorithm> ga;
    std::vector<SheetPlacement> nests;

    // Sheet clones handed to every evaluation, built ONCE per nest by buildSheets(). They are read-only
    // during placement (the serial path re-assigns Id/source/children to the same values), so rebuilding
    // them every step only cost time, which grows with the sheet count (one copy per sheet; nfp_nest_v2
    // expands an unlimited sheet type to one copy per part). Cleared by NestingContext::StartNest.
    std::vector<std::shared_ptr<NFP>> sheetCache;
    std::vector<int> sheetIdCache;
    std::vector<int> sheetSourceCache;
    std::vector<std::vector<std::shared_ptr<NFP>>> sheetChildrenCache;
    void buildSheets(const std::vector<NestItem>& parts);
};

} // namespace nest
