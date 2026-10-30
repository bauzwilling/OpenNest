# 02 · NFP + GA

Nest parts into a sheet with the NFP + genetic-algorithm engine (handles concave parts + holes).

Project: [Download `02_nfp.zip`](downloads/02_nfp.zip) — Windows `run.bat`, macOS/Linux `./run.command`.

```cpp
// 02 nfp — nest parts into a sheet with the NFP + genetic-algorithm engine.
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    int    pvc[]  = { 4, 4, 4, 3 };                      // 4 parts
    double pxy[]  = { 0,0, 30,0, 30,12, 0,12,
                      0,0, 20,0, 20,20, 0,20,
                      0,0, 40,0, 40,8,  0,8,
                      0,0, 24,0, 0,24 };
    int    pqty[] = { 3, 3, 3, 3 };                      // 3 copies each -> 12 instances
    int    svc[]  = { 4 };
    double sxy[]  = { 0,0, 150,0, 150,150, 0,150 };

    NfpParams p = {0};
    p.placementType = 1; p.rotations = 4; p.populationSize = 10; p.mutationRate = 10;
    p.seed = 1; p.clipperScale = 1e7; p.curveTolerance = 0.3; p.mode = 1; p.generations = 10; p.useParallel = 1;

    int    part_holes[4]  = {0,0,0,0};                   // no part holes
    int    sheet_holes[1] = {0};                         // no sheet holes
    double tx[12], ty[12], ang[12]; int sid[12], pidx[12]; int n_sheets; double fitness;
    int placed = nfp_nest(4, pvc, pxy, pqty, NULL, part_holes, NULL, NULL,
                          1, svc, sxy, sheet_holes, NULL, NULL,
                          &p, tx, ty, ang, sid, pidx, &n_sheets, &fitness);

    printf("placed %d/12 on %d sheet(s), fitness %.3f\n", placed, n_sheets, fitness);
    for (int i = 0; i < 12; i++)
        printf("  part %d -> sheet %d  (%.2f, %.2f) @ %.1f deg\n", pidx[i], sid[i], tx[i], ty[i], ang[i]);
    return 0;
}
```

## Several sheet types: `nfp_nest_v2`

`nfp_nest_v2` takes sheet **types** instead of individual sheets: each type has a quantity (`-1` = unlimited)
and, for the min-price mode, a price per whole sheet. The engine then picks the type of every sheet it opens:
`0` = list order, `1` = min total sheet area, `2` = min total price. `out_sheet_id` numbers the opened sheets
in opening order and `out_sheet_type[s]` gives each one's type. Full rules: [sheet selection](../../sheet_selection.md).

```cpp
int    svc[]   = { 4, 4 };                                  // two sheet types
double sxy[]   = { 0,0, 100,0, 100,60, 0,60,                // a full sheet
                   0,0, 40,0, 40,30, 0,30 };                // an offcut
int    shc[]   = { 0, 0 };
int    qty[]   = { -1, 2 };                                 // unlimited full sheets, 2 offcuts
int    stype[12];
int placed = nfp_nest_v2(4, pvc, pxy, pqty, NULL, part_holes, NULL, NULL,
                         2, svc, sxy, shc, NULL, NULL, qty, NULL, 1 /* min area */,
                         &p, tx, ty, ang, sid, pidx, stype, &n_sheets, &fitness);
```
