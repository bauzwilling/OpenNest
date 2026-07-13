using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace opennest_2
{
    // BATCH DISTRIBUTION (test harness): runs ONLY the batching step of OpenNest2 Batch — no nesting. It takes
    // the same Geometry input as the solver components, expands copies to instances exactly like
    // OpenNestBatchEngine.BuildDescriptors, hands them to the chosen distributor (nest_lib.batch.*), and lays
    // the resulting batches out in a grid so you can see — and diagnose — how the parts were split. Useful for
    // comparing distribution methods (Area Balanced / Area Snake / Multi-Feature Stratified) in isolation,
    // without waiting on a full nest.
    public class component_batch_distribution : GH_Component
    {
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        private BoundingBox _bbox = BoundingBox.Empty;
        private readonly List<Polyline> _display = new List<Polyline>();
        public override BoundingBox ClippingBox => _bbox;

        public component_batch_distribution()
            : base("OpenNest2 Batch Distribution", "Batch Distribution",
                   "Splits the Geometry parts into batches using the chosen distribution method and shows the batches (no nesting). A test harness for the batch distributors. Feed it the Geometry component, like OpenNest2.",
                   "Params", "OpenNest2")
        { }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Batch Size", "Batch Size", "Max parts per batch (same as OpenNest2 Batch). Batch count = ceil(parts / batch size).", GH_ParamAccess.item, 50);
            pManager.AddIntegerParameter("Distribution", "Distribution", BuildDistributionHelp(), GH_ParamAccess.item, 0);
            pManager.AddBooleanParameter("Spread", "Spread", "Reorder each batch's feed order (large/small alternating), as the batch nester does. Applies to area-aware distributions.", GH_ParamAccess.item, true);
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Batches", "Batches", "Part outlines grouped per batch (one branch per batch), laid out in a row per batch.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Part Group", "Part Group", "Geometry group index of each part, per batch branch — shows each batch's composition.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Area", "Area", "Part area per part, per batch branch.", GH_ParamAccess.tree);
            pManager.AddTextParameter("Diagnostics", "Diagnostics", "Per-batch shape stats: count, area / aspect ratio / compactness ranges + averages.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Batch Count", "Batch Count", "Number of batches produced.", GH_ParamAccess.item);
        }

        private static string BuildDistributionHelp()
        {
            var all = nest_lib.batch.DistributorRegistry.All();
            var sb = new StringBuilder("How the parts are split into batches. ");
            for (int i = 0; i < all.Count; i++) sb.Append(i).Append('=').Append(all[i].Label).Append(i < all.Count - 1 ? ", " : ".");
            return sb.ToString();
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            _display.Clear();
            _bbox = BoundingBox.Empty;

            nest_rhino_lib.nest_geo geo = null;
            if (!DA.GetData(0, ref geo) || geo == null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Geometry."); return; }

            int batchSize = 50; DA.GetData(1, ref batchSize); batchSize = Math.Max(1, batchSize);
            int distIndex = 0; DA.GetData(2, ref distIndex);
            var distributor = nest_lib.batch.DistributorRegistry.ByIndex(distIndex);
            bool spread = true; DA.GetData(3, ref spread);
            Message = distributor.Label;

            var boundaries = geo.boundary_sorted;
            if (boundaries == null || boundaries.Count == 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Geometry has no parts."); return; }

            var areas = geo.GroupAreas();
            var bounds = geo.GroupBounds();
            var copies = geo.GroupCopies();
            int groupCount = geo.geometry_sorted != null ? geo.geometry_sorted.Count : boundaries.Count;

            // Expand copies to instances (dense id 0..M-1 across groups), exactly like OpenNestBatchEngine.
            var instanceGroup = new List<int>();
            for (int g = 0; g < groupCount; g++)
            {
                int q = Math.Max(1, g < copies.Count ? copies[g] : 1);
                for (int c = 0; c < q; c++) instanceGroup.Add(g);
            }

            // One descriptor per instance, carrying its group's area + bounding-box size.
            var descriptors = new List<nest_lib.batch.PartDescriptor>(instanceGroup.Count);
            for (int id = 0; id < instanceGroup.Count; id++)
            {
                int g = instanceGroup[id];
                double area = g < areas.Count ? areas[g] : 0.0;
                double w = g < bounds.Count ? bounds[g].Item1 : 0.0;
                double h = g < bounds.Count ? bounds[g].Item2 : 0.0;
                descriptors.Add(new nest_lib.batch.PartDescriptor(id, area, w, h));
            }

            List<List<int>> batches;
            try { batches = distributor.Distribute(descriptors, batchSize); }
            catch (Exception ex)
            {
                // Shape-aware distributors reject invalid features (e.g. zero-width parts) — surface it.
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, distributor.Label + ": " + ex.Message);
                return;
            }

            // Spread each batch's order (large/small alternating) instead of the distributor's sorted run —
            // the same post-step the batch nester applies before feeding a batch to the engine. Optional (Spread
            // input) and skipped for distributors whose item order is meaningful (e.g. Sequential).
            if (spread && distributor.SpreadWithinBatch)
                nest_lib.batch.BatchOrdering.SpreadWithinBatches(batches,
                    id => { int g = id >= 0 && id < instanceGroup.Count ? instanceGroup[id] : -1; return g >= 0 && g < areas.Count ? areas[g] : 0.0; });

            var outlines = new GH_Structure<GH_Curve>();
            var partGroup = new GH_Structure<GH_Integer>();
            var partArea = new GH_Structure<GH_Number>();
            var diagnostics = new List<string>();

            // Layout: one row per batch (stacked downward); parts flow left-to-right by their bbox width.
            double maxDim = 0;
            foreach (var t in bounds) maxDim = Math.Max(maxDim, Math.Max(t.Item1, t.Item2));
            double gap = maxDim > 0 ? maxDim * 0.15 : 1.0;

            double y = 0;
            for (int b = 0; b < batches.Count; b++)
            {
                var path = new GH_Path(b);
                outlines.EnsurePath(path); partGroup.EnsurePath(path); partArea.EnsurePath(path);

                var batch = batches[b] ?? new List<int>();
                double x = 0, rowH = 0;
                foreach (int id in batch)
                {
                    int g = id >= 0 && id < instanceGroup.Count ? instanceGroup[id] : -1;
                    if (g < 0 || g >= boundaries.Count || boundaries[g] == null || boundaries[g].Count == 0) continue;

                    BoundingBox bb = boundaries[g][0].Item3;
                    double w = bb.Max.X - bb.Min.X, h = bb.Max.Y - bb.Min.Y;
                    var move = Transform.Translation(x - bb.Min.X, y - bb.Min.Y, 0);

                    for (int loop = 0; loop < boundaries[g].Count; loop++)
                    {
                        var pl = new Polyline(boundaries[g][loop].Item2);
                        pl.Transform(move);
                        _display.Add(pl);
                        _bbox.Union(pl.BoundingBox);
                        var crv = pl.ToPolylineCurve();
                        outlines.Append(new GH_Curve(crv), path);
                    }
                    partGroup.Append(new GH_Integer(g), path);
                    partArea.Append(new GH_Number(g < areas.Count ? areas[g] : 0.0), path);

                    x += w + gap;
                    rowH = Math.Max(rowH, h);
                }

                diagnostics.Add(DescribeBatch(b, batch, instanceGroup, areas, bounds));
                y -= (rowH > 0 ? rowH : maxDim) + gap;
            }

            DA.SetDataTree(0, outlines);
            DA.SetDataTree(1, partGroup);
            DA.SetDataTree(2, partArea);
            DA.SetDataList(3, diagnostics);
            DA.SetData(4, batches.Count);
        }

        // Per-batch shape summary (area / aspect ratio / compactness), computed from the batch's descriptors so
        // it reflects whichever distributor was chosen — not just the multi-feature one.
        private static string DescribeBatch(int index, List<int> batch, List<int> instanceGroup,
                                            List<double> areas, List<Tuple<double, double>> bounds)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ar = new List<double>();      // areas
            var asp = new List<double>();     // aspect ratios (valid only)
            var comp = new List<double>();    // compactness (valid only)
            foreach (int id in batch)
            {
                int g = id >= 0 && id < instanceGroup.Count ? instanceGroup[id] : -1;
                if (g < 0) continue;
                double a = g < areas.Count ? areas[g] : 0.0;
                ar.Add(a);
                if (g < bounds.Count)
                {
                    double w = bounds[g].Item1, h = bounds[g].Item2;
                    if (w > 0 && h > 0)
                    {
                        double longS = Math.Max(w, h), shortS = Math.Min(w, h);
                        asp.Add(longS / shortS);
                        comp.Add(a / (w * h));
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("Batch ").Append(index).Append(": Count=").Append(batch.Count);
            if (ar.Count > 0)
                sb.Append(", TotalArea=").Append(ar.Sum().ToString("F1", ci))
                  .Append(", Area=").Append(ar.Min().ToString("F1", ci)).Append("..").Append(ar.Max().ToString("F1", ci))
                  .Append(" (avg ").Append(ar.Average().ToString("F1", ci)).Append(')');
            if (asp.Count > 0)
                sb.Append(", Aspect=").Append(asp.Min().ToString("F2", ci)).Append("..").Append(asp.Max().ToString("F2", ci))
                  .Append(" (avg ").Append(asp.Average().ToString("F2", ci)).Append(')');
            if (comp.Count > 0)
                sb.Append(", Compact=").Append(comp.Min().ToString("F2", ci)).Append("..").Append(comp.Max().ToString("F2", ci))
                  .Append(" (avg ").Append(comp.Average().ToString("F2", ci)).Append(')');
            return sb.ToString();
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            foreach (var pl in _display) args.Display.DrawPolyline(pl, col, args.DefaultCurveThickness);
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest_2;
        public override Guid ComponentGuid => new Guid("C1A2B3D4-5E6F-4A7B-8C9D-0E1F2A3B4C5D");
    }
}
