using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino;

namespace opennest_2
{
    // OpenNest2 BATCH: a layer OVER the NFP/GA nester for large part counts. The engine gives great
    // results in acceptable time up to ~50 parts but degrades on 1000s; this component splits the parts
    // into batches it nests well (HOW is pluggable — see the Distribution option), nests each, KEEPS every
    // full sheet, and re-pools the not-full LAST sheet of every batch into the next round, repeating until
    // the leftover fits in a single batch. The heavy lifting is engine-agnostic (nest_lib.batch.*); this
    // component only adapts OpenNest's solver to it and assembles the final, re-slotted multi-sheet output.
    public class component_nest_batch : NestOptionsHostComponent, IGH_BakeAwareObject
    {
        public override GH_Exposure Exposure => GH_Exposure.primary;

        private BoundingBox bbox = new BoundingBox();
        private List<List<Polyline>> sheets_display = new List<List<Polyline>>();
        private List<Polyline> simplified_borders = new List<Polyline>();
        private List<Curve> geometry = new List<Curve>();
        public override BoundingBox ClippingBox => bbox;

        private enum Phase { Idle, Computing, Ready }
        private volatile Phase _phase = Phase.Idle;
        private System.Threading.Tasks.Task _task;
        private readonly System.Timers.Timer _timer;
        private Action _wake;

        private volatile bool _runActive = false;
        private bool _prevRunInput = false;
        private volatile bool _cancelled = false;
        private volatile string _progress = "";

        // Live mode (Run held TRUE): re-nest when the inputs actually change. _solvedSig is the signature of
        // the solve we last launched; a differing signature on a re-expire means a real edit -> re-nest.
        private string _solvedSig = null;
        private string _pendingSig = null;
        private volatile bool _restartRequested = false;   // inputs changed mid-solve -> re-nest after publish

        // Optional wall-clock timeout for the WHOLE batch run (seconds; 0 = off). Trips the same stop path as ESC.
        private readonly TimeoutWatch _timeout = new TimeoutWatch();
        private double _timeoutSecs = 0;

        // pending solve inputs / result
        private nest_lib.OpenNestBatchEngine _engine;
        private nest_lib.batch.BatchNestPlan _plan;
        private List<nest_lib.OpenNestBatchEngine.FinalPlacement> _placements;
        private nest_rhino_lib.nest_geo _masterGeo;
        private string _font = "MecSoft_Font-1 1";

        // cached outputs (re-emitted on a no-Run re-expire so downstream/viewport don't blank)
        private bool _hasResult = false;
        private List<Polyline> _o_sheets;
        private GH_Structure<GH_Curve> _o_borders, _o_sheettxt;
        private GH_Structure<IGH_GeometricGoo> _o_allgeo, _o_attr;
        private GH_Structure<GH_Transform> _o_xforms;
        private GH_Structure<GH_Integer> _o_sheetid;

        public component_nest_batch()
            : base("OpenNest2 Batch", "OpenNest2 Batch",
                "Nests large part counts by splitting them into batches the NFP solver handles well (~50 parts), then consolidating the partly-filled last sheet of every batch. Feed it the Sheets and Geometry components, like OpenNest2.",
                "Params", "OpenNest2")
        {
            _timer = new System.Timers.Timer(150) { AutoReset = false };
            _timer.Elapsed += OnTick;
            _wake = WakeForRetry;
            BuildOptions();
        }

        private void BuildOptions()
        {
            _options.Clear();
            _options.AddRange(NestOptionCatalog.OpenNest2Batch());
        }

        // Engine option "name value" tokens (same shape rhino_example's ctor parses), read BY NAME from the
        // option rows. Mirrors component_nest2.BuildOptionStrings but skips the batch-only keys; font last.
        private List<string> BuildEngineOptionStrings()
        {
            string Tok(string key, string fallback) { var o = Opt(key); return o != null ? o.EmitToken() : key + " " + fallback; }
            return new List<string>
            {
                Tok("num_of_rotations", "4"),
                "wiggle 0",
                Tok("placement_type", "1"),
                "spacing 0",
                Tok("seed", "30"),
                "simplify_tolerance 1",
                Tok("mutation", "10"),
                Tok("population", "10"),
                "time 0",
                Tok("all_rotations", "1"),
                "exact_nfp 1",
                Tok("font", "MecSoft_Font-1 1"),
            };
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Sheets", "Sheets", "From OpenNest tab, use component Sheets.", GH_ParamAccess.item);
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry.", GH_ParamAccess.item);
            pManager.AddParameter(MakeOptionsInput());
            pManager.AddIntegerParameter("Iterations", "Iterations", "GA generations per batch solve. ~5-20 typical (kept modest because many batches run).", GH_ParamAccess.item, 10);
            pManager.AddBooleanParameter("Run", "Run", "Wire a Boolean Toggle. TRUE = run the batch nest on a background thread (ESC stops); FALSE = hold the last result.", GH_ParamAccess.item, false);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Sheets", "Sheets", "Polylines representing the consolidated output sheets.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Borders", "Borders", "Placed part outline curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("All Geo", "All Geometry", "All placed geometry, grouped per sheet.", GH_ParamAccess.tree);
            pManager.AddTransformParameter("Transforms", "Transforms", "Move/rotate transform placing each part.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Sheet Id", "Sheet Id", "Consolidated sheet index each part landed on.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Sheet Txt", "Sheet Txt", "Sheet-number labels as text curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Attributes", "Attributes", "Attribute geometry carried with each part.", GH_ParamAccess.tree);
            for (int i = 0; i < pManager.ParamCount; i++) pManager.HideParameter(i);
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lw = args.DefaultCurveThickness;
            if (_phase == Phase.Computing)
            {
                // Live per-batch preview from the currently running sub-solve.
                var nest = _engine?.CurrentNest;
                if (nest != null)
                {
                    var ps = nest.LiveSheets; var pb = nest.LiveBorders;
                    if (ps != null) foreach (var pl in ps) args.Display.DrawPolyline(pl, col);
                    if (pb != null) foreach (var pl in pb) args.Display.DrawPolyline(pl, col, lw);
                }
                return;
            }
            for (int i = 0; i < sheets_display.Count; i++)
                for (int j = 0; j < sheets_display[i].Count; j++)
                    args.Display.DrawPolyline(sheets_display[i][j], col);
            foreach (var pl in simplified_borders) args.Display.DrawPolyline(pl, col, lw);
            foreach (var c in geometry) args.Display.DrawCurve(c, col);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ApplyWiredOptions(DA);

            // Embedded / headless host (cluster, ScriptEditor "Create Project", Player, Compute): the deferred
            // async re-solve is never honored there, so solve synchronously and publish in this single pass.
            if (GhRunContext.IsEmbedded(this)) { SolveSynchronously(DA); return; }

            bool runInput = false; DA.GetData(4, ref runInput);
            if (runInput && !_prevRunInput) { _runActive = true; _runButtonRequested = true; }
            else if (!runInput && _runActive)
            {
                _runActive = false; CancelSolve();
                try { EngineGate.Nfp.Dequeue(_wake); } catch { }
            }
            _prevRunInput = runInput;

            if (_phase == Phase.Computing)
            {
                if (!_runActive) { CancelSolve(); return; }
                // Live: an input changed while solving -> re-nest once this solve finishes.
                if (InputsChanged(DA)) _restartRequested = true;
                return;
            }

            if (_phase != Phase.Ready)
            {
                bool launch = _runButtonRequested; _runButtonRequested = false;
                if (!_runActive) { this.Message = null; EmitCached(DA); return; }
                if (!launch)
                {
                    // Live: re-nest only when the inputs actually changed; otherwise hold the last result.
                    if (_runActive && InputsChanged(DA)) { launch = true; this.Message = "live — restarting…"; }
                    else { this.Message = "live — watching"; EmitCached(DA); return; }
                }

                if (!TryAcquireEngine())
                {
                    this.Message = "queued — waiting for engine…";
                    EmitCached(DA);
                    return;
                }

                InputsChanged(DA); _solvedSig = _pendingSig;   // record the signature of the inputs we're nesting

                nest_rhino_lib.nest_sheets sheets = null; DA.GetData(0, ref sheets);
                nest_rhino_lib.nest_geo geo = null; DA.GetData(1, ref geo);
                if (sheets == null || geo == null)
                {
                    ReleaseEngine(); this.Message = "missing Sheets/Geometry"; return;
                }

                int iterations = 10; DA.GetData(3, ref iterations); if (iterations < 1) iterations = 1;

                // Read batch-layer options by name.
                int batchSize = (int)Math.Max(1, OptNum("batch_size", 50));
                int maxRounds = (int)Math.Max(1, OptNum("max_rounds", 12));
                var distributor = nest_lib.batch.DistributorRegistry.ByIndex(OptChoiceIndex("distribution", 0));

                // Engine options -> parameters list (parameters[0..8] by index, like rhino_example expects).
                var optStrings = BuildEngineOptionStrings();
                var parameters = new List<double>();
                for (int i = 0; i < optStrings.Count - 1; i++)
                {
                    string[] t = optStrings[i].Split(' ');
                    int id = t.Length > 1 ? 1 : 0;
                    if (double.TryParse(t[id], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r)) parameters.Add(r);
                }
                _font = optStrings[optStrings.Count - 1];
                int allRot = 0, exactNfp = 1, useHoles = 1;
                foreach (var line in optStrings)
                {
                    var t = line.Split(' ');
                    if (t.Length >= 2 && t[0] == "all_rotations" && int.TryParse(t[1], out int ar)) allRot = ar;
                    if (t.Length >= 2 && t[0] == "exact_nfp" && int.TryParse(t[1], out int en)) exactNfp = en;
                    if (t.Length >= 2 && t[0] == "element_holes" && int.TryParse(t[1], out int eh)) useHoles = eh;
                }

                // Duplicate inputs (the solver mutates them) and build the engine + orchestrator.
                _masterGeo = geo.duplicate();
                var sheetsDup = sheets.duplicate();
                try
                {
                    _engine = new nest_lib.OpenNestBatchEngine(_masterGeo, sheetsDup, parameters, iterations, allRot, exactNfp, useHoles);
                }
                catch (Exception ex) { RhinoApp.WriteLine(ex.ToString()); ReleaseEngine(); this.Message = "input error"; return; }

                _plan = null; _placements = null; _hasResult = false;
                _cancelled = false; _progress = "starting…";
                _timeoutSecs = OptNum("timeout", 0);   // 0 = no limit; caps the whole run
                _phase = Phase.Computing;
                this.Message = "starting…  (ESC = stop)";

                int bs = batchSize, mr = maxRounds;
                var dist = distributor;
                _task = new System.Threading.Tasks.Task(() => RunOrchestration(bs, mr, dist));
                return;
            }

            // ===== PASS 2: orchestration finished -> assemble + publish =====
            try { AssembleOutputs(DA); }
            catch (Exception ex) { RhinoApp.WriteLine(ex.ToString()); }
            _phase = Phase.Idle;
            StopClock();

            // An input changed while we were solving -> re-nest with the new inputs (deferred re-expire is the
            // GH-safe way to relaunch from inside a solution).
            if (_restartRequested && _runActive)
            {
                _restartRequested = false;
                OnPingDocument()?.ScheduleSolution(5, d => { try { ExpireSolution(true); } catch { } });
            }
        }

        // Cheap, stable signature of the meaningful inputs (Iterations + option tokens + every nesting-boundary
        // point + every sheet point, rounded). Lets a real edit be told apart from an unrelated re-expire.
        private static string SigOf(nest_rhino_lib.nest_sheets sheets, nest_rhino_lib.nest_geo geo,
                                    int iters, List<string> optTokens)
        {
            long[] h = { unchecked((long)1469598103934665603) };   // FNV-1a-ish
            void MixI(long v) { unchecked { h[0] = (h[0] ^ v) * 1099511628211L; } }
            void MixD(double d) { MixI(BitConverter.DoubleToInt64Bits(Math.Round(d, 6))); }
            MixI(iters);
            if (optTokens != null) foreach (var t in optTokens) if (t != null) foreach (char c in t) MixI(c);
            try
            {
                if (geo != null && geo.boundary_sorted != null)
                    foreach (var part in geo.boundary_sorted)
                        foreach (var loop in part)
                        {
                            var pl = loop.Item2; if (pl == null) continue;
                            for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                        }
                if (sheets != null && sheets.sheets != null)
                    foreach (var arr in sheets.sheets)
                        if (arr != null)
                            foreach (var pl in arr)
                            {
                                if (pl == null) continue;
                                for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                            }
            }
            catch { }
            return h[0].ToString();
        }

        // Reads the inputs, caches the signature into _pendingSig, and reports whether it differs from the
        // solve we last launched. Includes the batch-layer options so changing Batch Size / Distribution /
        // Max Rounds also re-nests.
        private bool InputsChanged(IGH_DataAccess DA)
        {
            nest_rhino_lib.nest_sheets s = null; DA.GetData(0, ref s);
            nest_rhino_lib.nest_geo g = null; DA.GetData(1, ref g);
            int it = 10; DA.GetData(3, ref it); if (it < 1) it = 1;
            var tokens = BuildEngineOptionStrings();
            tokens.Add("batch_size " + (int)Math.Max(1, OptNum("batch_size", 50)));
            tokens.Add("distribution " + OptChoiceIndex("distribution", 0));
            tokens.Add("max_rounds " + (int)Math.Max(1, OptNum("max_rounds", 12)));
            _pendingSig = SigOf(s, g, it, tokens);
            return _pendingSig != _solvedSig;
        }

        protected override void AfterSolveInstance()
        {
            if (_phase == Phase.Computing && _task != null && _task.Status == System.Threading.Tasks.TaskStatus.Created)
            {
                StartClock();
                _task.Start(System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        private void RunOrchestrationCore(int batchSize, int maxRounds, nest_lib.batch.IPartDistributor distributor)
        {
            var orch = new nest_lib.batch.BatchNestOrchestrator(_engine, distributor, batchSize, maxRounds)
            {
                IsCancelled = () => _cancelled,
                OnProgress = msg => _progress = msg,
            };
            var descriptors = _engine.BuildDescriptors();
            _plan = orch.Run(descriptors);
            _placements = _engine.BuildFinalPlacements(_plan);
        }

        private void RunOrchestration(int batchSize, int maxRounds, nest_lib.batch.IPartDistributor distributor)
        {
            try { RunOrchestrationCore(batchSize, maxRounds, distributor); }
            catch (Exception ex) { RhinoApp.WriteLine(ex.ToString()); }
            finally { ReleaseEngine(); }
            _phase = Phase.Ready;
            RhinoApp.InvokeOnUiThread((Action)(() => { try { ExpireSolution(true); } catch { } }));
        }

        // Synchronous one-shot solve for embedded/headless hosts: read inputs, run the orchestrator inline,
        // and publish in the SAME pass (no background thread, no deferred ExpireSolution).
        private void SolveSynchronously(IGH_DataAccess DA)
        {
            bool runInput = false; DA.GetData(4, ref runInput);
            if (!runInput) { this.Message = "Run = false"; return; }

            nest_rhino_lib.nest_sheets sheets = null; DA.GetData(0, ref sheets);
            nest_rhino_lib.nest_geo geo = null; DA.GetData(1, ref geo);
            if (sheets == null || geo == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "missing Sheets/Geometry"); return; }

            int iterations = 10; DA.GetData(3, ref iterations); if (iterations < 1) iterations = 1;
            int batchSize = (int)Math.Max(1, OptNum("batch_size", 50));
            int maxRounds = (int)Math.Max(1, OptNum("max_rounds", 12));
            var distributor = nest_lib.batch.DistributorRegistry.ByIndex(OptChoiceIndex("distribution", 0));

            var optStrings = BuildEngineOptionStrings();
            var parameters = new List<double>();
            for (int i = 0; i < optStrings.Count - 1; i++)
            {
                string[] t = optStrings[i].Split(' ');
                int id = t.Length > 1 ? 1 : 0;
                if (double.TryParse(t[id], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r)) parameters.Add(r);
            }
            _font = optStrings[optStrings.Count - 1];
            int allRot = 0, exactNfp = 1, useHoles = 1;
            foreach (var line in optStrings)
            {
                var t = line.Split(' ');
                if (t.Length >= 2 && t[0] == "all_rotations" && int.TryParse(t[1], out int ar)) allRot = ar;
                if (t.Length >= 2 && t[0] == "exact_nfp" && int.TryParse(t[1], out int en)) exactNfp = en;
                if (t.Length >= 2 && t[0] == "element_holes" && int.TryParse(t[1], out int eh)) useHoles = eh;
            }

            bool gated = false;
            try
            {
                gated = EngineGate.Nfp.TryAcquire(_wake);   // best-effort; an embedded host is usually the only solver running
                _masterGeo = geo.duplicate();
                var sheetsDup = sheets.duplicate();
                _engine = new nest_lib.OpenNestBatchEngine(_masterGeo, sheetsDup, parameters, iterations, allRot, exactNfp, useHoles);
                _cancelled = false; _progress = "";
                _timeoutSecs = OptNum("timeout", 0);
                _phase = Phase.Computing;
                _timeout.Start(_timeoutSecs, TimeoutCancel);
                RunOrchestrationCore(batchSize, maxRounds, distributor);
                _phase = Phase.Ready;
                AssembleOutputs(DA);
            }
            catch (Exception ex) { RhinoApp.WriteLine(ex.ToString()); AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); }
            finally { _timeout.Stop(); if (gated) EngineGate.Nfp.Release(); _phase = Phase.Idle; }
        }

        private void AssembleOutputs(IGH_DataAccess DA)
        {
            ResetDisplay();
            var geo = _masterGeo;
            var placements = _placements ?? new List<nest_lib.OpenNestBatchEngine.FinalPlacement>();
            var plan = _plan;

            // Output sheets (one per consolidated global sheet), stamped on the clean output grid.
            int totalSheets = plan != null ? plan.TotalSheets : 0;
            var output_sheets = new List<Polyline>();
            for (int g = 0; g < totalSheets; g++)
            {
                var sh = _engine.OutputSheet(g);
                output_sheets.AddRange(sh);
                sheets_display.Add(sh);
            }
            DA.SetDataList(0, output_sheets);

            // Group placements by master part group; copies become the k sub-index.
            var byGroup = new Dictionary<int, List<nest_lib.OpenNestBatchEngine.FinalPlacement>>();
            foreach (var p in placements)
            {
                if (!byGroup.TryGetValue(p.MasterGroup, out var l)) { l = new List<nest_lib.OpenNestBatchEngine.FinalPlacement>(); byGroup[p.MasterGroup] = l; }
                l.Add(p);
            }

            var borders = new GH_Structure<GH_Curve>();
            var allGeo = new GH_Structure<IGH_GeometricGoo>();
            var xforms = new GH_Structure<GH_Transform>();
            var sheetId = new GH_Structure<GH_Integer>();
            var attrs = new GH_Structure<IGH_GeometricGoo>();

            for (int i = 0; i < geo.geometry_sorted.Count; i++)
            {
                if (!byGroup.TryGetValue(i, out var copies)) continue;   // group not placed (shouldn't happen unless unplaced)
                for (int k = 0; k < copies.Count; k++)
                {
                    var xf = copies[k].Xform;
                    xforms.Append(new GH_Transform(xf), new GH_Path(i));
                    sheetId.Append(new GH_Integer(copies[k].GlobalSheet), new GH_Path(i));

                    // borders: each boundary loop (outer + holes), per copy
                    for (int j = 0; j < geo.boundary_sorted[i].Count; j++)
                    {
                        Curve crv = geo.boundary_sorted[i][j].Item2.ToNurbsCurve();
                        crv.Transform(xf);
                        borders.Append(new GH_Curve(crv), new GH_Path(i, k));
                    }

                    // all geometry of the group, transformed; plus its attribute geometry
                    for (int j = 0; j < geo.geometry_sorted[i].Count; j++)
                    {
                        int gi = geo.geometry_sorted[i][j];
                        GeometryBase gb = geo.geometry[gi].Duplicate();
                        gb.Transform(xf);
                        allGeo.Append(GH_Convert.ToGeometricGoo(gb), new GH_Path(i));
                        if (gb is Curve cc) { bbox.Union(gb.GetBoundingBox(false)); geometry.Add(cc); }

                        if (gi < geo.geometry_attributes.Count)
                            foreach (var ga in geo.geometry_attributes[gi])
                            {
                                var gac = ga.Duplicate(); gac.Transform(xf);
                                attrs.Append(GH_Convert.ToGeometricGoo(gac), new GH_Path(i));
                            }
                    }
                }
            }
            DA.SetDataTree(1, borders);
            DA.SetDataTree(2, allGeo);
            DA.SetDataTree(3, xforms);
            DA.SetDataTree(4, sheetId);
            DA.SetDataTree(6, attrs);

            // Sheet-number labels (positive radius branch only, mirroring OpenNest2's default).
            var sheetTxt = new GH_Structure<GH_Curve>();
            var opt = _font.Split(' ');
            if (opt.Length > 1 && double.TryParse(opt[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double radius) && radius > 0)
            {
                for (int g = 0; g < sheets_display.Count; g++)
                {
                    if (sheets_display[g].Count == 0) continue;
                    Point3d center = sheets_display[g][0].BoundingBox.Max - new Vector3d(radius, radius, 0);
                    var circle = new Circle(center, radius);
                    Curve[] crvs = nest_rhino_lib.ToRhino.get_text(g.ToString(), circle, opt[0]);
                    foreach (var c in crvs) sheetTxt.Append(new GH_Curve(c), new GH_Path(g));
                }
            }
            DA.SetDataTree(5, sheetTxt);

            // status + warnings
            int placedCount = placements.Count;
            int unplaced = plan != null ? plan.Unplaced.Count : 0;
            this.Message = (_cancelled ? "stopped — " : "") + $"{totalSheets} sheet(s), {_engine.SolvesRun} solve(s)";
            if (unplaced > 0) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{unplaced} part(s) could not be placed on any sheet.");

            _o_sheets = output_sheets; _o_borders = borders; _o_allgeo = allGeo; _o_xforms = xforms;
            _o_sheetid = sheetId; _o_sheettxt = sheetTxt; _o_attr = attrs; _hasResult = true;
        }

        private void EmitCached(IGH_DataAccess DA)
        {
            if (!_hasResult) return;
            if (_o_sheets != null) DA.SetDataList(0, _o_sheets);
            if (_o_borders != null) DA.SetDataTree(1, _o_borders);
            if (_o_allgeo != null) DA.SetDataTree(2, _o_allgeo);
            if (_o_xforms != null) DA.SetDataTree(3, _o_xforms);
            if (_o_sheetid != null) DA.SetDataTree(4, _o_sheetid);
            if (_o_sheettxt != null) DA.SetDataTree(5, _o_sheettxt);
            if (_o_attr != null) DA.SetDataTree(6, _o_attr);
        }

        private void ResetDisplay()
        {
            bbox = new BoundingBox();
            sheets_display = new List<List<Polyline>>();
            simplified_borders = new List<Polyline>();
            geometry = new List<Curve>();
        }

        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            try
            {
                this.Message = _cancelled ? ("stopping… " + _progress) : _progress;
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try { if (_phase == Phase.Computing) { OnDisplayExpired(true); ExpirePreview(true); RhinoDoc.ActiveDoc?.Views.Redraw(); } }
                    catch { }
                }));
            }
            catch { }
            if (_phase == Phase.Computing) _timer.Start();
        }

        private void StartClock() { RhinoApp.EscapeKeyPressed += OnEscape; _timer.Start(); _timeout.Start(_timeoutSecs, TimeoutCancel); }
        private void StopClock() { try { RhinoApp.EscapeKeyPressed -= OnEscape; } catch { } try { _timer.Stop(); } catch { } _timeout.Stop(); }

        // Wall-clock timeout fired: stop the whole batch run, keep the sheets committed so far (same as ESC).
        private void TimeoutCancel()
        {
            if (_phase == Phase.Computing) { _runActive = false; _cancelled = true; _engine?.Cancel(); this.Message = "timeout — keeping result so far"; }
        }

        private void OnEscape(object sender, EventArgs e)
        {
            if (_phase == Phase.Computing) { _runActive = false; _cancelled = true; _engine?.Cancel(); this.Message = "stopping…"; }
        }
        private void CancelSolve() { if (_phase == Phase.Computing) { _cancelled = true; _engine?.Cancel(); } }

        private bool TryAcquireEngine() => EngineGate.Nfp.TryAcquire(_wake);
        private void ReleaseEngine() => EngineGate.Nfp.Release();
        private void WakeForRetry()
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { if (_runActive && _phase == Phase.Idle) { _runButtonRequested = true; ExpireSolution(true); } } catch { }
            }));
        }

        public override bool IsBusy => _runActive;
        public override void OnRunClicked()
        {
            if (_runActive || _phase == Phase.Computing)
            {
                _runActive = false; CancelSolve();
                try { EngineGate.Nfp.Dequeue(_wake); } catch { }
                this.Message = "stopped";
                if (_phase != Phase.Computing) ExpireSolution(true);
            }
            else { _runActive = true; _runButtonRequested = true; ExpireSolution(true); }
        }

        protected override void ExpireDownStreamObjects()
        {
            if (_phase != Phase.Computing) base.ExpireDownStreamObjects();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            try { if (_phase == Phase.Computing) { _cancelled = true; _engine?.Cancel(); } } catch { }
            StopClock();
            try { EngineGate.Nfp.Dequeue(_wake); } catch { }
            try { if (_phase == Phase.Computing) ReleaseEngine(); } catch { }
            base.RemovedFromDocument(document);
        }

        // bake the placed geometry + sheets
        public void BakeGeometry(RhinoDoc doc, List<Guid> obj_ids)
        {
            foreach (var c in geometry) obj_ids.Add(doc.Objects.AddCurve(c));
            for (int i = 0; i < sheets_display.Count; i++)
                for (int j = 0; j < sheets_display[i].Count; j++)
                    obj_ids.Add(doc.Objects.AddPolyline(sheets_display[i][j]));
            doc.Views.ActiveView?.Redraw();
        }
        public void BakeGeometry(RhinoDoc doc, Rhino.DocObjects.ObjectAttributes att, List<Guid> obj_ids) => BakeGeometry(doc, obj_ids);
        public bool IsBakeCapable => _hasResult;

        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest_2;
        public override Guid ComponentGuid => new Guid("B7E1F2A4-3C5D-4E6F-9A8B-1D2C3E4F5A6B");
    }
}
