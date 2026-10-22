using Grasshopper.Kernel;
using System;
using System.Windows.Forms;

namespace opennest_2
{
    // Right-click "Simplify: Max deviation" toggle shared by the GH1 Geometry components (Curves + Surfaces).
    // Off (default) = distance mode: Simplify x>0 divides curved segments every x units. On = Simplify x>0 is the
    // maximum deviation eps and the nesting outline CONTAINS the original (nest_geo.contain_simplify). The state is
    // saved with the component under KEY; files without it load as distance mode.
    internal static class simplify_mode_menu
    {
        internal const string KEY = "simplify_max_deviation";
        internal const string MESSAGE = "Max deviation";

        internal static void Append(GH_Component component, ToolStripDropDown menu, bool on, Action<bool> set)
        {
            GH_DocumentObject.Menu_AppendSeparator(menu);
            var item = GH_DocumentObject.Menu_AppendItem(menu, "Simplify: Max deviation (contains original)", (s, e) =>
            {
                component.RecordUndoEvent("Simplify mode");
                set(!on);
                component.ExpireSolution(true);
            }, true, on);
            item.ToolTipText =
                "Off: Simplify x>0 divides curved segments every x units (points on the curve).\n" +
                "On: Simplify x>0 is the MAXIMUM DEVIATION from the original curve. The nesting outline then\n" +
                "contains the part (outers never smaller, holes never larger), so simplifying can't cause an overlap.";
        }
    }
}
