using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace NestEval
{
    // Collects every test's metrics and, when the whole run finishes, writes machine-readable
    // (results.csv) and human-readable (summary.md) reports to <repo>/eval-results/. This is the
    // channel the agent reads to eval its progress: correctness is gated by the test asserts, and
    // packing quality (utilization / sheets / wall) is dumped here regardless of pass/fail.
    public sealed class EvalReport : IDisposable
    {
        private readonly ConcurrentBag<NestMetrics> _rows = new ConcurrentBag<NestMetrics>();

        public void Add(NestMetrics m) => _rows.Add(m);

        public void Dispose()
        {
            if (_rows.IsEmpty) return;
            var outDir = Path.Combine(RepoRoot(), "eval-results");
            Directory.CreateDirectory(outDir);
            var rows = _rows.OrderBy(r => r.Dataset).ThenBy(r => r.Engine).ToList();
            WriteCsv(Path.Combine(outDir, "results.csv"), rows);
            WriteSummary(Path.Combine(outDir, "summary.md"), rows);
        }

        private static void WriteCsv(string path, System.Collections.Generic.List<NestMetrics> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("dataset,engine,seed,placed,total,sheets,util,overlap_area,oob_area,part_area,wall_ms,fitness");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[]
                {
                    r.Dataset, r.Engine, I(r.Seed), I(r.Placed), I(r.Total), I(r.Sheets),
                    F(r.Utilization, 4), F(r.OverlapArea, 3), F(r.OutOfBoundsArea, 3),
                    F(r.PartArea, 1), I((int)r.WallMs), F(r.Fitness, 3)
                }));
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteSummary(string path, System.Collections.Generic.List<NestMetrics> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Nesting eval");
            sb.AppendLine();
            sb.AppendLine("Higher `util` = tighter packing (the quality signal). `overlap`/`oob` must be ~0");
            sb.AppendLine("(correctness); `placed/total` should be all-placed. Utilization is the metric to");
            sb.AppendLine("watch across changes.");
            sb.AppendLine();
            sb.AppendLine("| dataset | engine | placed/total | sheets | util | overlap | oob | wall ms | fitness |");
            sb.AppendLine("|---------|--------|--------------|--------|------|---------|-----|---------|---------|");
            foreach (var r in rows)
                sb.AppendLine($"| {r.Dataset} | {r.Engine} | {r.Placed}/{r.Total} | {r.Sheets} | " +
                              $"{r.Utilization:P1} | {r.OverlapArea:0.###} | {r.OutOfBoundsArea:0.###} | " +
                              $"{r.WallMs} | {r.Fitness:0.###} |");
            File.WriteAllText(path, sb.ToString());
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string F(double v, int d) => Math.Round(v, d).ToString(CultureInfo.InvariantCulture);

        // Walk up from the test assembly until a directory containing ".git" is found (the repo root),
        // so eval-results/ lands at a stable, easy-to-find location regardless of cwd.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }

    // One shared EvalReport across the whole test run (disposed once, after the last test).
    [CollectionDefinition("nest-eval")]
    public sealed class EvalCollection : ICollectionFixture<EvalReport> { }
}
