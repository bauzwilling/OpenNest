using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using nest_lib.batch;

namespace nest_lib
{
    // Adapts the OpenNest2 NFP/GA solver (rhino_example -> nfp_nest.dll) to the engine-agnostic
    // IBatchNestEngine the batch orchestrator drives. Per batch it carves a sub-nest_geo out of the master
    // (nest_geo.Subset), sets each part's copy count to the batch's quantity, runs one solve, and reports
    // which local sheet each INSTANCE landed on. Placement transforms are retained per solve so the
    // component can rebuild the final, re-slotted multi-sheet layout afterwards.
    //
    // Instance model: the orchestrator works in "instance ids" (one per physical piece). A master part
    // group with copies=N expands to N instances. Identical copies are interchangeable, so an instance only
    // needs to know its master group; within a batch we hand the solver `count` copies of a group and map
    // the `count` returned placements back onto that group's instance ids in order.
    //
    // Sheet types / stock (docs/sheet_selection.md): each input sheet set is a type. With a sheet selection
    // mode or limited quantities every solve runs nfp_nest_v2; the engine reserves the sheets each solve opens
    // and gets re-pooled last sheets back (ISheetStockEngine), so a limited type is never used more often than
    // its quantity across all batches. Each output sheet is drawn with its own type's outline.
    public sealed class OpenNestBatchEngine : IBatchNestEngine, ISheetStockEngine
    {
        // Per solve: the instance transforms, plus each local sheet's slot (laid-out sheet index the transform
        // is baked against) and type. Local sheets are numbered in OPENING order, so the last one is the tail.
        private sealed class SolveData
        {
            public Dictionary<int, Transform> Xforms;
            public List<int> Slots;
            public List<int> Types;
        }

        private readonly int _sheetSelection;
        private readonly double _sheetTieTolerance;
        private readonly int[] _stock;   // per type: sheets left, -1 = unlimited; null = no limits
        private readonly Dictionary<int, int> _globalSheetType = new Dictionary<int, int>();
        private readonly nest_rhino_lib.nest_geo _master;
        private readonly nest_rhino_lib.nest_sheets _sheetsTemplate;
        private readonly List<double> _parameters;
        private readonly int _maxIterations;
        private readonly int _allRotations, _exactNfp, _useHoles, _preOrient;

        // instanceId -> master group index (built once).
        private readonly Dictionary<int, int> _instanceMaster = new Dictionary<int, int>();
        // master group -> its instance ids (in stable order).
        private readonly Dictionary<int, List<int>> _groupInstances = new Dictionary<int, List<int>>();

        // Output-grid geometry (a clean, non-overlapping sheet grid we lay the committed sheets onto).
        private readonly Point3d[] _localOrigin;   // min-corner of each template sheet slot (for normalizing)
        private readonly double _sheetW, _sheetH, _gapX, _gapY;
        private readonly int _cols;
        private readonly Polyline[] _sheet0;        // sheet-0 outline + holes, used as the output sheet stamp

        // Live cancel / preview: the currently-running sub-solve (read by the component for ESC + preview).
        public volatile bool StopRequested = false;
        private volatile rhino_example _current;
        public rhino_example CurrentNest => _current;
        public int SolvesRun { get; private set; }

        public OpenNestBatchEngine(nest_rhino_lib.nest_geo master, nest_rhino_lib.nest_sheets sheetsTemplate,
                                   List<double> parameters, int maxIterations,
                                   int allRotations, int exactNfp, int useHoles, int preOrient = 1,
                                   int sheetSelection = 0, double sheetTieTolerance = 0.01)
        {
            _master = master;
            _sheetsTemplate = sheetsTemplate;
            _sheetSelection = sheetSelection;
            _sheetTieTolerance = sheetTieTolerance;
            _stock = sheetsTemplate.type_quantities != null ? (int[])sheetsTemplate.type_quantities.Clone() : null;
            _parameters = parameters;
            _maxIterations = Math.Max(1, maxIterations);
            _allRotations = allRotations; _exactNfp = exactNfp; _useHoles = useHoles; _preOrient = preOrient;

            // Expand copies into instances. instanceId is a dense 0..M-1 index across all groups.
            var copies = master.GroupCopies();
            int inst = 0;
            for (int g = 0; g < master.geometry_sorted.Count; g++)
            {
                int q = Math.Max(1, g < copies.Count ? copies[g] : 1);
                var ids = new List<int>(q);
                for (int c = 0; c < q; c++) { _instanceMaster[inst] = g; ids.Add(inst); inst++; }
                _groupInstances[g] = ids;
            }

            // Sheet-slot geometry from the template. localOrigin[s] = min corner of template sheet s (matches
            // the sheetOx/sheetOy the solver bakes into placements). Output grid pitch + columns come from the
            // template's sheet size and gap/array settings.
            var sheets = sheetsTemplate.sheets;
            int avail = 0; for (int i = 0; i < sheets.Length; i++) if (sheets[i] != null && sheets[i].Length > 0) avail++;
            _localOrigin = new Point3d[Math.Max(1, avail)];
            for (int i = 0; i < _localOrigin.Length; i++)
            {
                if (sheets[i] != null && sheets[i].Length > 0 && sheets[i][0] != null)
                    _localOrigin[i] = sheets[i][0].BoundingBox.Min;
                else
                    _localOrigin[i] = Point3d.Origin;
            }
            if (sheets.Length > 0 && sheets[0] != null) _sheet0 = sheets[0];
            BoundingBox b0 = (_sheet0 != null && _sheet0.Length > 0 && _sheet0[0] != null) ? _sheet0[0].BoundingBox : new BoundingBox(Point3d.Origin, Point3d.Origin);
            _sheetW = b0.Max.X - b0.Min.X; _sheetH = b0.Max.Y - b0.Min.Y;
            // With several sheet types the row continuation beyond the laid-out slots uses the widest type.
            for (int t = 1; t < Math.Min(sheetsTemplate.type_count, sheets.Length); t++)
                if (sheets[t] != null && sheets[t].Length > 0 && sheets[t][0] != null)
                    _sheetW = Math.Max(_sheetW, sheets[t][0].BoundingBox.Max.X - sheets[t][0].BoundingBox.Min.X);
            _gapX = Math.Max(0, sheetsTemplate.gap_x); _gapY = Math.Max(0, sheetsTemplate.gap_y);
            _cols = Math.Max(1, sheetsTemplate.array_x);
        }

        public int InstanceCount => _instanceMaster.Count;

        // All instances as descriptors for the distributor (each instance inherits its group's area and
        // outer-loop bounding-box size, so shape-aware distributors can derive aspect ratio / compactness).
        // The size is the PRE-ORIENTED box (part_preorient), not the as-drawn one, so a distributor sees the
        // same dimensions the solver will actually nest — identical for every part that fits as drawn.
        public List<PartDescriptor> BuildDescriptors()
        {
            var areas = _master.GroupAreas();
            int rotationSteps = (_parameters != null && _parameters.Count > 0) ? (int)_parameters[0] : 0;
            var bounds = nest_rhino_lib.part_preorient.GroupExtents(
                _master, _sheetsTemplate, rotationSteps, _preOrient != 0);
            var list = new List<PartDescriptor>(_instanceMaster.Count);
            foreach (var kv in _instanceMaster.OrderBy(k => k.Key))
            {
                int g = kv.Value;
                double area = g < areas.Count ? areas[g] : 0.0;
                double w = g < bounds.Count ? bounds[g].Item1 : 0.0;
                double h = g < bounds.Count ? bounds[g].Item2 : 0.0;
                list.Add(new PartDescriptor(kv.Key, area, w, h));
            }
            return list;
        }

        public void Cancel()
        {
            StopRequested = true;
            try { var n = _current; if (n != null) n.StopRequested = true; } catch { }
        }

        public BatchSolveResult SolveBatch(IReadOnlyList<int> instanceIds)
        {
            if (StopRequested || instanceIds == null || instanceIds.Count == 0) return null;

            // Group the batch's instances by master group, preserving order so we can map placements back.
            var groupsInOrder = new List<int>();
            var instancesByGroup = new Dictionary<int, List<int>>();
            foreach (int id in instanceIds)
            {
                if (!_instanceMaster.TryGetValue(id, out int g)) continue;
                if (!instancesByGroup.TryGetValue(g, out var list)) { list = new List<int>(); instancesByGroup[g] = list; groupsInOrder.Add(g); }
                list.Add(id);
            }
            if (groupsInOrder.Count == 0) return null;

            // Carve the sub-nest_geo and set each part's copy count to its in-batch quantity.
            var sub = _master.Subset(groupsInOrder, out var localToMaster);
            for (int i = 0; i < localToMaster.Count; i++)
            {
                int gi = sub.geometry_sorted[i][0];
                int count = instancesByGroup[localToMaster[i]].Count;
                if (gi >= 0 && gi < sub.copies.Count) sub.copies[gi] = count;
            }

            // Fresh sheet copy per solve (the solver offsets/transforms sheets in place), offering only the
            // stock that is left.
            var sheets = _sheetsTemplate.duplicate();
            if (_stock != null)
            {
                sheets.type_quantities = (int[])_stock.Clone();
                if (Array.TrueForAll(_stock, q => q == 0)) return null;   // every sheet type used up
            }

            var nest = new rhino_example(ref sheets, ref sub, _parameters, _maxIterations, _preOrient)
            {
                TryAllRotations = _allRotations,
                ExactNfp = _exactNfp,
                UseHoles = _useHoles,
                SheetSelection = _sheetSelection,
                SheetTieTolerance = _sheetTieTolerance,
            };
            _current = nest;
            if (StopRequested) { nest.StopRequested = true; }
            try { nest.static_solver(ref sub); }
            finally { _current = null; SolvesRun++; }

            // Read placements: per sub group i, output_transforms[i][k] / output_polygon_sheet_ids[i][k] are
            // the k-th copy's world transform + sheet id. Map them onto that group's instance ids in order.
            var sheetOf = new Dictionary<int, int>(instanceIds.Count);
            var xforms = new Dictionary<int, Transform>(instanceIds.Count);
            var ot = nest.output_transforms;
            var os = nest.output_polygon_sheet_ids;
            // output_polygon_sheet_ids are SLOTS (laid-out sheet indices); local sheets are the opening order.
            var slots = nest.OpenedSheetSlots ?? new List<int>();
            var types = nest.OpenedSheetTypes ?? new List<int>();
            var localOfSlot = new Dictionary<int, int>();
            for (int s = 0; s < slots.Count; s++) localOfSlot[slots[s]] = s;
            for (int i = 0; i < localToMaster.Count; i++)
            {
                var ids = instancesByGroup[localToMaster[i]];
                var ti = (ot != null && i < ot.Count) ? ot[i] : null;
                var si = (os != null && i < os.Count) ? os[i] : null;
                for (int k = 0; k < ids.Count; k++)
                {
                    int slot = (si != null && k < si.Count) ? si[k] : -1;
                    sheetOf[ids[k]] = slot >= 0 && localOfSlot.TryGetValue(slot, out int local) ? local : -1;
                    if (ti != null && k < ti.Count) xforms[ids[k]] = ti[k];
                }
            }

            // Reserve the opened sheets of limited types (handed back via ReleaseSheet when re-pooled).
            if (_stock != null)
                foreach (int t in types)
                    if (t >= 0 && t < _stock.Length && _stock[t] > 0) _stock[t]--;

            var data = new SolveData { Xforms = xforms, Slots = slots, Types = types };
            return new BatchSolveResult { SheetCount = slots.Count, SheetOf = sheetOf, EngineData = data };
        }

        public void ReleaseSheet(BatchSolveResult solve, int localSheet)
        {
            if (_stock == null || !(solve?.EngineData is SolveData d)) return;
            if (localSheet < 0 || localSheet >= d.Types.Count) return;
            int t = d.Types[localSheet];
            if (t >= 0 && t < _stock.Length && _stock[t] >= 0) _stock[t]++;
        }

        // ---- final assembly ----------------------------------------------------------------------------

        public sealed class FinalPlacement
        {
            public int MasterGroup;
            public int GlobalSheet;
            public Transform Xform;   // places the ORIGINAL geometry to its final world position
        }

        // Output origin for global sheet g. Reuse the SAME sheet-slot positions the Sheets component laid out
        // (template sheet g's min-corner) so the batch result matches the plain solver's layout — a row, a
        // grid, whatever the template is. For sheets beyond the template's positioned copies, continue the
        // template's row to the right by one sheet pitch.
        public Point3d OutputOrigin(int g)
        {
            if (g >= 0 && g < _localOrigin.Length) return _localOrigin[g];
            int last = _localOrigin.Length - 1;
            Point3d basePt = last >= 0 ? _localOrigin[last] : Point3d.Origin;
            return new Point3d(basePt.X + (g - last) * (_sheetW + _gapX), basePt.Y, 0);
        }

        private Point3d LocalOrigin(int s) => (s >= 0 && s < _localOrigin.Length) ? _localOrigin[s] : Point3d.Origin;

        private static int SlotOf(SolveData d, int localSheet) =>
            d != null && localSheet >= 0 && localSheet < d.Slots.Count ? d.Slots[localSheet] : localSheet;

        // Build one placement PER instance, so every input part gets a transform + Sheet Id in the output
        // (mirrors the plain OpenNest2 GA solver, which emits output_transforms for placed AND unplaced parts
        // in a single pass). Placed instances are re-slotted onto the consolidated output grid: normalize by
        // the part's local sheet origin (baked into its solve transform), then shift to its global sheet's
        // grid origin. Unplaced instances get Identity (leaves the part at its ORIGINAL input location — the
        // master geo the component transforms is in input-world coords) and GlobalSheet -1, the same
        // convention rhino_example uses for its unplaced parts.
        public List<FinalPlacement> BuildFinalPlacements(BatchNestPlan plan)
        {
            var result = new List<FinalPlacement>(plan.Placements.Count + plan.Unplaced.Count);
            foreach (var pl in plan.Placements)
            {
                if (!(pl.Solve?.EngineData is SolveData d) || !d.Xforms.TryGetValue(pl.InstanceId, out var world))
                    continue;
                if (pl.LocalSheet >= 0 && pl.LocalSheet < d.Types.Count) _globalSheetType[pl.GlobalSheet] = d.Types[pl.LocalSheet];
                Vector3d delta = OutputOrigin(pl.GlobalSheet) - LocalOrigin(SlotOf(d, pl.LocalSheet));
                Transform final = Transform.Translation(delta) * world;
                result.Add(new FinalPlacement
                {
                    MasterGroup = _instanceMaster.TryGetValue(pl.InstanceId, out var g) ? g : 0,
                    GlobalSheet = pl.GlobalSheet,
                    Xform = final,
                });
            }
            foreach (int inst in plan.Unplaced)
            {
                result.Add(new FinalPlacement
                {
                    MasterGroup = _instanceMaster.TryGetValue(inst, out var g) ? g : 0,
                    GlobalSheet = -1,
                    Xform = Transform.Identity,
                });
            }
            return result;
        }

        // Global sheet g's own type outline (+ holes) stamped at its grid origin (call BuildFinalPlacements
        // first; the type is that of the opened sheet the global sheet came from). The type's first laid-out
        // copy is slot t, since copy i is type i % type_count.
        public List<Polyline> OutputSheet(int g)
        {
            var outp = new List<Polyline>();
            int type = _globalSheetType.TryGetValue(g, out int gt) ? gt : 0;
            var sheets = _sheetsTemplate.sheets;
            var stamp = type >= 0 && type < sheets.Length && sheets[type] != null ? sheets[type] : _sheet0;
            if (stamp == null) return outp;
            Vector3d delta = OutputOrigin(g) - LocalOrigin(stamp == _sheet0 ? 0 : type);
            var t = Transform.Translation(delta);
            foreach (var pl in stamp)
            {
                if (pl == null) continue;
                var c = new Polyline(pl);
                c.Transform(t);
                outp.Add(c);
            }
            return outp;
        }

        public nest_rhino_lib.nest_geo Master => _master;
    }
}
