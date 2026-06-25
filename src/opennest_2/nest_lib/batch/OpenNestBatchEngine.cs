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
    public sealed class OpenNestBatchEngine : IBatchNestEngine
    {
        private readonly nest_rhino_lib.nest_geo _master;
        private readonly nest_rhino_lib.nest_sheets _sheetsTemplate;
        private readonly List<double> _parameters;
        private readonly int _maxIterations;
        private readonly int _allRotations, _exactNfp, _useHoles;

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
                                   int allRotations, int exactNfp, int useHoles)
        {
            _master = master;
            _sheetsTemplate = sheetsTemplate;
            _parameters = parameters;
            _maxIterations = Math.Max(1, maxIterations);
            _allRotations = allRotations; _exactNfp = exactNfp; _useHoles = useHoles;

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
            _gapX = Math.Max(0, sheetsTemplate.gap_x); _gapY = Math.Max(0, sheetsTemplate.gap_y);
            _cols = Math.Max(1, sheetsTemplate.array_x);
        }

        public int InstanceCount => _instanceMaster.Count;

        // All instances as area-tagged descriptors for the distributor (each instance inherits its group's area).
        public List<PartDescriptor> BuildDescriptors()
        {
            var areas = _master.GroupAreas();
            var list = new List<PartDescriptor>(_instanceMaster.Count);
            foreach (var kv in _instanceMaster.OrderBy(k => k.Key))
            {
                int g = kv.Value;
                list.Add(new PartDescriptor(kv.Key, g < areas.Count ? areas[g] : 0.0));
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

            // Fresh sheet copy per solve (the solver offsets/transforms sheets in place).
            var sheets = _sheetsTemplate.duplicate();

            var nest = new rhino_example(ref sheets, ref sub, _parameters, _maxIterations)
            {
                TryAllRotations = _allRotations,
                ExactNfp = _exactNfp,
                UseHoles = _useHoles,
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
            for (int i = 0; i < localToMaster.Count; i++)
            {
                var ids = instancesByGroup[localToMaster[i]];
                var ti = (ot != null && i < ot.Count) ? ot[i] : null;
                var si = (os != null && i < os.Count) ? os[i] : null;
                for (int k = 0; k < ids.Count; k++)
                {
                    int sheet = (si != null && k < si.Count) ? si[k] : -1;
                    sheetOf[ids[k]] = sheet;
                    if (ti != null && k < ti.Count) xforms[ids[k]] = ti[k];
                }
            }

            int nSheets = 0;
            foreach (var v in sheetOf.Values) if (v >= 0) nSheets = Math.Max(nSheets, v + 1);

            return new BatchSolveResult { SheetCount = nSheets, SheetOf = sheetOf, EngineData = xforms };
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

        // Re-slot every committed placement onto the consolidated output grid: normalize by the part's local
        // sheet origin (baked into its solve transform), then shift to its global sheet's grid origin.
        public List<FinalPlacement> BuildFinalPlacements(BatchNestPlan plan)
        {
            var result = new List<FinalPlacement>(plan.Placements.Count);
            foreach (var pl in plan.Placements)
            {
                if (!(pl.Solve?.EngineData is Dictionary<int, Transform> xf) || !xf.TryGetValue(pl.InstanceId, out var world))
                    continue;
                Vector3d delta = OutputOrigin(pl.GlobalSheet) - LocalOrigin(pl.LocalSheet);
                Transform final = Transform.Translation(delta) * world;
                result.Add(new FinalPlacement
                {
                    MasterGroup = _instanceMaster.TryGetValue(pl.InstanceId, out var g) ? g : 0,
                    GlobalSheet = pl.GlobalSheet,
                    Xform = final,
                });
            }
            return result;
        }

        // The sheet-0 outline (+ holes) stamped at global sheet g's grid origin.
        public List<Polyline> OutputSheet(int g)
        {
            var outp = new List<Polyline>();
            if (_sheet0 == null) return outp;
            Vector3d delta = OutputOrigin(g) - LocalOrigin(0);
            var t = Transform.Translation(delta);
            foreach (var pl in _sheet0)
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
