using System;
using System.Collections.Generic;
using System.Linq;
using nest_lib.batch;
using Xunit;
using Xunit.Abstractions;

namespace nest_lib_batch_tests
{
    // Tests for BatchOrdering.SpreadBySortedKey / SpreadWithinBatches: reorder a batch so a numeric key
    // (area) is dispersed rather than sorted, without changing membership.
    public class BatchOrderingTests
    {
        private readonly ITestOutputHelper _out;
        public BatchOrderingTests(ITestOutputHelper output) { _out = output; }

        // Area = id + 1, so a larger id means a larger area (easy to read the spread).
        private static double AreaOfId(int id) => id + 1;

        // Count of monotonic (sorted-run) adjacent pairs in a sequence's key values.
        private static int MonotonicPairs(IReadOnlyList<int> seq, Func<int, double> keyOf)
        {
            int runs = 0;
            for (int i = 2; i < seq.Count; i++)
            {
                double a = keyOf(seq[i - 2]), b = keyOf(seq[i - 1]), c = keyOf(seq[i]);
                if ((b - a) * (c - b) > 0) runs++; // same direction twice in a row
            }
            return runs;
        }

        [Fact]
        public void Is_a_permutation_no_parts_added_or_lost()
        {
            var ids = Enumerable.Range(0, 50).ToList();
            var spread = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);
            Assert.Equal(ids.OrderBy(x => x), spread.OrderBy(x => x));
            Assert.Equal(ids.Count, spread.Count);
        }

        [Fact]
        public void Result_is_not_sorted_and_alternates_across_the_range()
        {
            var ids = Enumerable.Range(0, 6).ToList();     // areas 1..6
            var spread = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);
            _out.WriteLine("areas in spread order: " + string.Join(", ", spread.Select(AreaOfId)));

            // Not the sorted (descending) order the distributors would emit.
            var sortedDesc = ids.OrderByDescending(AreaOfId).ToList();
            Assert.NotEqual(sortedDesc, spread);

            // n=6 bit-reversal (N=8): kept index order 0,4,2,1,5,3 -> ranks -> areas 6,2,4,5,1,3.
            Assert.Equal(new double[] { 6, 2, 4, 5, 1, 3 }, spread.Select(AreaOfId).ToArray());
        }

        [Fact]
        public void Spread_has_far_fewer_monotonic_runs_than_sorted()
        {
            var ids = Enumerable.Range(0, 40).ToList();
            var sortedDesc = ids.OrderByDescending(AreaOfId).ToList();
            var spread = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);

            int sortedRuns = MonotonicPairs(sortedDesc, AreaOfId);   // fully sorted => every triple monotonic
            int spreadRuns = MonotonicPairs(spread, AreaOfId);
            _out.WriteLine($"monotonic triples — sorted={sortedRuns}, spread={spreadRuns}");

            Assert.Equal(sortedDesc.Count - 2, sortedRuns);          // sorted: all 38 triples monotonic
            Assert.True(spreadRuns < sortedRuns / 2, $"spread still had {spreadRuns} monotonic triples");
        }

        [Fact]
        public void Distributor_spread_flags_are_correct()
        {
            // Area-aware methods spread; Sequential keeps its meaningful (spatial) item order.
            Assert.True(DistributorRegistry.ByKey("area_normal").SpreadWithinBatch);
            Assert.False(DistributorRegistry.ByKey("sequential").SpreadWithinBatch);
        }

        [Fact]
        public void Print_sequence_for_eyeballing()
        {
            foreach (int n in new[] { 16, 24 })
            {
                var ids = Enumerable.Range(0, n).ToList();
                var spread = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);
                _out.WriteLine($"n={n}: " + string.Join(" ", spread.Select(id => AreaOfId(id).ToString("0"))));
            }
        }

        [Fact]
        public void Is_deterministic()
        {
            var ids = Enumerable.Range(0, 37).Select(i => (i * 17) % 37).ToList(); // scrambled input order
            var a = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);
            var b = BatchOrdering.SpreadBySortedKey(ids, AreaOfId);
            Assert.Equal(a, b);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Tiny_batches_are_returned_unchanged(int n)
        {
            var ids = Enumerable.Range(0, n).ToList();
            Assert.Equal(ids, BatchOrdering.SpreadBySortedKey(ids, AreaOfId));
        }

        [Fact]
        public void SpreadWithinBatches_permutes_each_batch_but_keeps_membership()
        {
            var batches = new List<List<int>>
            {
                Enumerable.Range(0, 10).ToList(),
                Enumerable.Range(10, 20).ToList(),
                new List<int> { 30, 31 },              // <=2 untouched
            };
            var before = batches.Select(b => b.OrderBy(x => x).ToList()).ToList();
            BatchOrdering.SpreadWithinBatches(batches, AreaOfId);

            for (int i = 0; i < batches.Count; i++)
                Assert.Equal(before[i], batches[i].OrderBy(x => x).ToList()); // same set per batch
            Assert.Equal(new List<int> { 30, 31 }, batches[2]);              // small batch unchanged
            Assert.NotEqual(Enumerable.Range(0, 10).ToList(), batches[0]);   // big batch reordered
        }
    }
}
