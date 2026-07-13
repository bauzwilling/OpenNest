using System;
using System.Collections.Generic;
using System.Linq;
using nest_lib.batch;
using Xunit;
using Xunit.Abstractions;

namespace nest_lib_batch_tests
{
    // Unit tests for AreaNormalDistributor ("Area Balanced"): an LPT (largest-processing-time) greedy that
    // splits N parts into ceil(N/batchSize) batches of nearly-equal TOTAL AREA, each batch <= batchSize.
    public class AreaNormalDistributorTests
    {
        private readonly ITestOutputHelper _out;
        public AreaNormalDistributorTests(ITestOutputHelper output) { _out = output; }

        private static readonly AreaNormalDistributor Sut = new AreaNormalDistributor();

        private static List<PartDescriptor> Parts(params double[] areas)
        {
            var list = new List<PartDescriptor>(areas.Length);
            for (int i = 0; i < areas.Length; i++) list.Add(new PartDescriptor(i, areas[i]));
            return list;
        }

        private static double AreaOf(IReadOnlyList<PartDescriptor> parts, int id) => parts.First(p => p.Id == id).Area;
        private static double BatchArea(List<int> batch, IReadOnlyList<PartDescriptor> parts) => batch.Sum(id => AreaOf(parts, id));

        // ---- invariants -----------------------------------------------------------------------------

        [Fact]
        public void Metadata_is_stable()
        {
            Assert.Equal("area_normal", Sut.Key);
            Assert.Equal("Area Balanced", Sut.Label);
        }

        [Fact]
        public void Empty_input_yields_one_empty_batch()
        {
            var batches = Sut.Distribute(Parts(), batchSize: 10);
            Assert.Single(batches);
            Assert.Empty(batches[0]);
        }

        [Theory]
        [InlineData(1, 10)]
        [InlineData(10, 10)]   // exactly one full batch
        [InlineData(11, 10)]   // one over -> 2 batches
        [InlineData(100, 33)]  // ceil(100/33) = 4
        [InlineData(50, 7)]    // ceil(50/7)  = 8
        public void Batch_count_is_ceil_n_over_batchsize(int n, int batchSize)
        {
            var parts = Parts(Enumerable.Range(0, n).Select(i => 1.0 + i).ToArray());
            var batches = Sut.Distribute(parts, batchSize);
            int expected = (int)Math.Ceiling(n / (double)batchSize);
            Assert.Equal(expected, batches.Count);
        }

        [Fact]
        public void Every_part_appears_exactly_once_no_drops_no_dupes()
        {
            var parts = Parts(Enumerable.Range(0, 137).Select(i => (i * 7 % 23) + 1.0).ToArray());
            var batches = Sut.Distribute(parts, batchSize: 20);

            var all = batches.SelectMany(b => b).ToList();
            Assert.Equal(137, all.Count);                                  // nothing dropped or duplicated
            Assert.Equal(Enumerable.Range(0, 137), all.OrderBy(x => x));   // exactly ids 0..136
        }

        [Fact]
        public void No_batch_exceeds_batch_size()
        {
            var parts = Parts(Enumerable.Range(0, 200).Select(i => (double)((i % 5) + 1)).ToArray());
            var batches = Sut.Distribute(parts, batchSize: 30);
            Assert.All(batches, b => Assert.True(b.Count <= 30, $"batch had {b.Count} > 30"));
        }

        [Fact]
        public void Is_deterministic_for_same_input()
        {
            var parts = Parts(Enumerable.Range(0, 90).Select(i => (double)((i * 13) % 40 + 1)).ToArray());
            var a = Sut.Distribute(parts, 25);
            var b = Sut.Distribute(parts, 25);
            Assert.Equal(a.Select(x => x.ToArray()), b.Select(x => x.ToArray()));
        }

        [Fact]
        public void Equal_area_parts_split_into_equal_counts_ties_by_id()
        {
            // 6 identical-area parts, batchSize 3 -> 2 batches. LPT with area ties broken by Id.
            var parts = Parts(1, 1, 1, 1, 1, 1);
            var batches = Sut.Distribute(parts, 3);

            Assert.Equal(2, batches.Count);
            Assert.All(batches, b => Assert.Equal(3, b.Count));
            Assert.Equal(6.0, batches.Sum(b => BatchArea(b, parts)), 9);
        }

        // ---- the point of the method: total area is balanced across batches -------------------------

        [Fact]
        public void Balances_total_area_across_batches()
        {
            // A deliberately lumpy area distribution: a few big parts + many small ones.
            var rng = new int[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 1 };
            var parts = Parts(rng.Select(x => (double)x).ToArray());
            int batchSize = 5; // 20 parts -> 4 batches

            var batches = Sut.Distribute(parts, batchSize);
            var areas = batches.Select(b => BatchArea(b, parts)).ToList();

            double total = rng.Sum();
            double ideal = total / batches.Count;
            double max = areas.Max(), min = areas.Min();
            double spreadPct = (max - min) / ideal * 100.0;

            _out.WriteLine($"parts (areas)   : {string.Join(", ", rng)}");
            _out.WriteLine($"batchSize={batchSize}  batches={batches.Count}  total area={total}  ideal/batch={ideal:F1}");
            for (int i = 0; i < batches.Count; i++)
                _out.WriteLine($"  batch {i}: count={batches[i].Count}  area={areas[i]:F1}  ids=[{string.Join(",", batches[i])}]");
            _out.WriteLine($"max={max:F1}  min={min:F1}  spread={max - min:F1} ({spreadPct:F1}% of ideal)");

            // LPT keeps the imbalance small: worst batch is within one largest-part of the best.
            Assert.True(max - min <= rng.Max(), $"area spread {max - min} exceeded largest part {rng.Max()}");
            // In practice this input balances very tightly.
            Assert.True(spreadPct < 15.0, $"area spread {spreadPct:F1}% too large");
        }
    }
}
