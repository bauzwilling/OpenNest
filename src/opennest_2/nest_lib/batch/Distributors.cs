using System;
using System.Collections.Generic;
using System.Linq;

namespace nest_lib.batch
{
    // All available distribution methods, keyed for the GH dropdown. Adding a method = register it here
    // once; the component's dropdown is built from All() so it needs no per-method edit. ("Drag-n-drop"
    // intent: methods are interchangeable strategies selected by the user.)
    public static class DistributorRegistry
    {
        private static readonly List<IPartDistributor> _all = new List<IPartDistributor>
        {
            new AreaNormalDistributor(),
            new SequentialDistributor(),
        };

        public static IReadOnlyList<IPartDistributor> All() => _all;

        public static IPartDistributor ByKey(string key)
        {
            if (!string.IsNullOrEmpty(key))
                foreach (var d in _all)
                    if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) return d;
            return _all[0];
        }

        public static IPartDistributor ByIndex(int i) => (i >= 0 && i < _all.Count) ? _all[i] : _all[0];
        public static int IndexOf(string key)
        {
            for (int i = 0; i < _all.Count; i++)
                if (string.Equals(_all[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
    }

    // Helper: number of batches needed so no batch exceeds batchSize.
    internal static class BatchMath
    {
        public static int BatchCount(int n, int batchSize) => Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, batchSize)));
    }

    // AREA-BALANCED: split into K = ceil(N/batchSize) batches with nearly EQUAL TOTAL AREA, so every batch
    // needs a similar number of sheets and the leftover consolidation stays even. Uses the LPT (Longest
    // Processing Time) heuristic: sort parts largest-area first, then drop each into the batch with the
    // least total area so far that still has room (< batchSize). Placing the big parts first keeps the
    // final area imbalance small (LPT's classic guarantee); the batchSize cap keeps every batch within the
    // engine's comfort zone. Deterministic for a given input (ties broken by Id).
    public sealed class AreaNormalDistributor : IPartDistributor
    {
        public string Key => "area_normal";                 // kept for saved-file compatibility (dropdown is index-based)
        public string Label => "Area Balanced";
        public string Description => "Balance total part area across batches (largest-first greedy / LPT), each batch <= batch size.";

        public List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize)
        {
            int n = parts.Count;
            int bs = Math.Max(1, batchSize);
            int k = BatchMath.BatchCount(n, bs);
            var batches = new List<List<int>>(k);
            var areaSum = new double[k];
            for (int i = 0; i < k; i++) batches.Add(new List<int>());
            if (n == 0) return batches;

            // LPT: largest area first, each into the least-loaded batch that still has room.
            var sorted = parts.OrderByDescending(p => p.Area).ThenBy(p => p.Id).ToList();
            foreach (var p in sorted)
            {
                int best = -1;
                for (int i = 0; i < k; i++)
                {
                    if (batches[i].Count >= bs) continue;                 // respect the batch_size cap
                    if (best < 0 || areaSum[i] < areaSum[best]) best = i; // least total area so far
                }
                if (best < 0) best = 0;                                   // defensive: k*bs >= n, so room always exists
                batches[best].Add(p.Id);
                areaSum[best] += p.Area;
            }
            return batches;
        }
    }

    // SEQUENTIAL: contiguous chunks in input order (parts 0..49, 50..99, ...). The simplest split; each
    // batch is a slice, so spatially/semantically adjacent parts stay together.
    public sealed class SequentialDistributor : IPartDistributor
    {
        public string Key => "sequential";
        public string Label => "Sequential";
        public string Description => "Contiguous chunks in input order (parts 1..N split into slices of batch size).";

        public List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize)
        {
            int n = parts.Count;
            int bs = Math.Max(1, batchSize);
            var batches = new List<List<int>>();
            for (int i = 0; i < n; i += bs)
                batches.Add(parts.Skip(i).Take(bs).Select(p => p.Id).ToList());
            if (batches.Count == 0) batches.Add(new List<int>());
            return batches;
        }
    }

    // SEQUENTIAL-MERGE: fill batches to batch_size (36,36,27 rather than a balanced 33,33,33) while keeping
    // an inner distributor's ordering. Run the inner distributor, concatenate its batches IN ORDER into one
    // flat sequence, then cut that sequence into contiguous batch_size chunks. This differs from plain
    // Sequential (which chunks the RAW input order): the inner distributor first arranges the parts (e.g.
    // Area-Balanced groups them into area-balanced batches), so each full-size chunk still carries a spread of areas.
    // Used by the orchestrator as a fallback when a re-batching round stalls (commits nothing): fuller
    // batches can overflow a sheet and start committing, instead of many equally under-filled ones.
    public sealed class SequentialMergeDistributor : IPartDistributor
    {
        private readonly IPartDistributor _inner;
        public SequentialMergeDistributor(IPartDistributor inner) { _inner = inner ?? new SequentialDistributor(); }

        public string Key => "sequential_merge";
        public string Label => "Sequential-Merge";
        public string Description => "Apply the chosen distribution, merge its batches in order, then cut into full batch-size sequential chunks.";

        public List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize)
        {
            int bs = Math.Max(1, batchSize);
            // 1) chosen distribution -> 2) merge batches in their current order -> 3) sequential batch_size chunks.
            var flat = new List<int>(parts.Count);
            foreach (var b in _inner.Distribute(parts, bs)) if (b != null) flat.AddRange(b);
            var batches = new List<List<int>>();
            for (int i = 0; i < flat.Count; i += bs)
                batches.Add(flat.Skip(i).Take(bs).ToList());
            if (batches.Count == 0) batches.Add(new List<int>());
            return batches;
        }
    }
}
