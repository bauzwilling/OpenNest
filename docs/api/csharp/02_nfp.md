# 02 · NFP + GA

Nest parts into a sheet with the NFP + genetic-algorithm engine (handles concave parts + holes).

Project: [Download `02_nfp.zip`](downloads/02_nfp.zip) — Windows `run.bat`, macOS/Linux `./run.command`.

```csharp
// 02 nfp — nest parts into a sheet with the NFP + genetic-algorithm engine.
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct NfpParams {
    public int placementType, rotations, mutationRate, populationSize, seed;
    public double curveTolerance, clipperScale, spacing, sheetSpacing, rotationLimit;
    public int useHoles, exploreConcave, clipByHull, clipByRects, simplify, mode, generations, numSeeds, useParallel;
    public double timeBudgetSecs;
    public int maxSheets, edgeSamples, compactionPasses, tryAllRotations, exactNfp, stallGenerations;
}

static class Program {
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_nest(int part_count, int[] pvc, double[] pxy, int[] pqty, int[] part_rotations,
        int[] phc, int[] phvc, double[] phxy, int sheet_count, int[] svc, double[] sxy,
        int[] shc, int[] shvc, double[] shxy, ref NfpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx, out int nSheets, out double fitness);

    static void Main() {
        int[] pvc = { 4, 4, 4, 3 };
        double[] pxy = { 0,0, 30,0, 30,12, 0,12,  0,0, 20,0, 20,20, 0,20,
                         0,0, 40,0, 40,8, 0,8,    0,0, 24,0, 0,24 };
        int[] pqty = { 3, 3, 3, 3 };                    // 12 instances
        int[] svc = { 4 }; double[] sxy = { 0,0, 150,0, 150,150, 0,150 };

        var p = new NfpParams { placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10,
                                seed = 1, curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 10, useParallel = 1 };
        var tx = new double[12]; var ty = new double[12]; var ang = new double[12];
        var sid = new int[12]; var pidx = new int[12];
        int placed = nfp_nest(4, pvc, pxy, pqty, null, new int[4], null, null, 1, svc, sxy,
                              new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);

        Console.WriteLine($"placed {placed}/12 on {nSheets} sheet(s), fitness {fitness:F3}");
        for (int i = 0; i < 12; i++)
            Console.WriteLine($"  part {pidx[i]} -> sheet {sid[i]}  ({tx[i]:F2}, {ty[i]:F2}) @ {ang[i]:F1} deg");
    }
}
```

## Search length and parallel searches

- **`generations`** caps how long one search runs. **`stallGenerations`** (default `0` = off) ends a search earlier, once its
  best layout has not improved for that many generations. Make it long: the decisive improvement can come after a long
  plateau (on one 12-part job the 2-sheet layout came 1000–1500 generations after the last improvement, so a window of
  1000 stayed on 3 sheets and 1500 found 2), while on a job that settles early a short window saves most of the time.
- **`mode = 2`** (turbo) runs `numSeeds` searches in parallel with seeds `seed`, `seed + 1`, …; search `s` gives exactly what
  a single `mode = 1` search with seed `seed + s` gives, and the best layout is returned. Each search stops on its own
  `stallGenerations`.
- The parallel searches **share** the part-to-part NFPs of hole-free parts and the sheet inner-fit NFPs, so each is
  computed once for all seeds (11–29% faster on a 12-part job with 4–8 seeds; parts with holes keep per-search NFPs).
  Sharing never changes the result: every rotated part is built from the unrotated part by its absolute angle, so an
  entry is the same whichever search computes it. `NFP_NO_SHARED_CACHE=1` in the environment turns sharing off.
