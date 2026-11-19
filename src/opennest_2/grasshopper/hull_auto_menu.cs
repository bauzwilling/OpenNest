using GH_IO.Serialization;
using Grasshopper.Kernel;
using System;
using System.Globalization;
using System.Windows.Forms;

namespace opennest_2
{
    // Right-click "Hull: Auto" option shared by the GH1 Geometry components (Curves + Surfaces). The Hull input stays
    // a Boolean (so saved files and wired toggles are unchanged); this refines Hull = True:
    //   Hull False             -> no hull (0)
    //   Hull True              -> every part by its convex hull (1)
    //   Hull True + Hull: Auto -> every part EXCEPT those with a concave pocket of at least "Auto min pocket (%)" of
    //                             their area, which keep their outline so other parts can nest into the pocket (2).
    // Both settings are saved with the component. A NEW component starts with Auto on (2.89.11: Hull Auto is the
    // default, with the Hull input defaulting to True); files saved before that load as Auto off, 5%, so they
    // nest as they did.
    internal sealed class hull_auto_menu
    {
        internal const string KEY = "hull_auto";
        internal const string KEY_POCKET = "hull_auto_pocket_percent";

        public bool Auto = true;   // new components; Read() turns it off for files without the key
        public double PocketPercent = nest_rhino_lib.nest_geo_util.HULL_AUTO_DEFAULT_POCKET_PERCENT;

        // simplify_parameters[1] for the Hull input value (nest_geo_util.hull_mode).
        public double HullValue(bool hullInput) =>
            !hullInput ? nest_rhino_lib.nest_geo_util.HULL_OFF
            : Auto ? nest_rhino_lib.nest_geo_util.HULL_AUTO
            : nest_rhino_lib.nest_geo_util.HULL_ON;

        public string Message(bool hullInput) =>
            hullInput && Auto ? "Hull auto " + PocketPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%" : null;

        // After geo_to_nest_geo: say how many parts Auto hulled and kept, so the choice is visible.
        public void Report(GH_Component component, nest_rhino_lib.nest_geo geo, bool hullInput)
        {
            if (!hullInput || !Auto || geo == null) return;
            component.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Hull Auto: hulled {geo.hull_auto_hulled} part(s); kept the outline of {geo.hull_auto_kept} part(s) with a concave pocket of at least "
                + PocketPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% of their area.");
        }

        public void Append(GH_Component component, ToolStripDropDown menu)
        {
            GH_DocumentObject.Menu_AppendSeparator(menu);
            var item = GH_DocumentObject.Menu_AppendItem(menu, "Hull: Auto (keep parts with a concave pocket)", (s, e) =>
            {
                component.RecordUndoEvent("Hull auto");
                Auto = !Auto;
                component.ExpireSolution(true);
            }, true, Auto);
            item.ToolTipText =
                "Applies when Hull = True.\n" +
                "Off: every part is nested by its convex hull.\n" +
                "On: every part is hulled EXCEPT one with a concave pocket (the area between a hull edge and the outline)\n" +
                "of at least 'Auto min pocket (%)' of its area: it keeps its outline so other parts can nest into the pocket.\n" +
                "The largest pocket counts, not their sum, so parts with only small notches are still hulled.";

            GH_DocumentObject.Menu_AppendItem(menu, "Auto min pocket (%):", null, false);
            GH_DocumentObject.Menu_AppendTextItem(menu, PocketPercent.ToString("0.##", CultureInfo.InvariantCulture),
                (s, e) =>
                {
                    if (e.KeyCode != Keys.Enter || s == null) return;
                    if (double.TryParse((s.Text ?? "").Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        && !double.IsNaN(v) && !double.IsInfinity(v))
                    {
                        component.RecordUndoEvent("Hull auto pocket");
                        PocketPercent = Math.Max(0, Math.Min(100, v));
                        component.ExpireSolution(true);
                    }
                    menu.Close();
                }, null, true);
        }

        public void Write(GH_IWriter writer)
        {
            writer.SetBoolean(KEY, Auto);
            writer.SetDouble(KEY_POCKET, PocketPercent);
        }

        public void Read(GH_IReader reader)
        {
            Auto = false;
            PocketPercent = nest_rhino_lib.nest_geo_util.HULL_AUTO_DEFAULT_POCKET_PERCENT;
            reader.TryGetBoolean(KEY, ref Auto);
            reader.TryGetDouble(KEY_POCKET, ref PocketPercent);
        }
    }
}
