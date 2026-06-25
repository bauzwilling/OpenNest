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
            new RoundRobinDistributor(),
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

    // AREA-NORMAL: keep the area distribution of each batch the same as the whole set. Sort all parts by
    // area, then SNAKE-deal them across K = ceil(N/batchSize) batches (0,1,..,K-1, K-1,..,1,0, ...). Dealing
    // a sorted list round-robin is stratified sampling — every batch gets one part from each area stratum,
    // so its area histogram mirrors the population's. The snake (boustrophedon) direction also keeps the
    // SUM of areas nearly equal across batches, so batches tend to need a similar number of sheets.
    public sealed class AreaNormalDistributor : IPartDistributor
    {
        public string Key => "area_normal";
        public string Label => "Area-Normal";
        public string Description => "Preserve the whole set's area distribution inside every batch (sorted snake-deal / stratified sampling).";

        public List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize)
        {
            int n = parts.Count;
            int k = BatchMath.BatchCount(n, batchSize);
            var batches = new List<List<int>>(k);
            for (int i = 0; i < k; i++) batches.Add(new List<int>());
            if (n == 0) return batches;

            // Sort by area ascending (stable on Id so the split is deterministic for a given input).
            var sorted = parts.OrderBy(p => p.Area).ThenBy(p => p.Id).ToList();

            // Snake-deal: bin 0..k-1 then k-1..0, repeating. Spreads each area stratum across all batches
            // and balances total area between them.
            int bin = 0, dir = 1;
            for (int idx = 0; idx < sorted.Count; idx++)
            {
                batches[bin].Add(sorted[idx].Id);
                bin += dir;
                if (bin == k) { bin = k - 1; dir = -1; }
                else if (bin < 0) { bin = 0; dir = 1; }
            }
            return batches;
        }
    }

    // ROUND-ROBIN: deal parts in input order across K batches. No area awareness; useful as a baseline /
    // when the input is already ordered meaningfully.
    public sealed class RoundRobinDistributor : IPartDistributor
    {
        public string Key => "round_robin";
        public string Label => "Round-Robin";
        public string Description => "Deal parts across batches in input order (no area weighting).";

        public List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize)
        {
            int n = parts.Count;
            int k = BatchMath.BatchCount(n, batchSize);
            var batches = new List<List<int>>(k);
            for (int i = 0; i < k; i++) batches.Add(new List<int>());
            for (int idx = 0; idx < n; idx++) batches[idx % k].Add(parts[idx].Id);
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
}
