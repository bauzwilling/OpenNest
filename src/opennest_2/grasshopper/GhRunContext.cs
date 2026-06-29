using Grasshopper;
using Grasshopper.Kernel;

namespace opennest_2
{
    // Tells a solver component whether it is running on the INTERACTIVE canvas (where the async background-
    // thread solve + deferred ExpireSolution publishes results on a later pass) or EMBEDDED / HEADLESS — a
    // Grasshopper cluster, a ScriptEditor "Create Project" compiled component, RhinoCompute, or the
    // Grasshopper Player. In the embedded/headless case the host solves the inner document ONCE,
    // synchronously, and reads the outputs at the end of that single pass — it never honors a later
    // ExpireSolution (and headless has no UI message pump to run InvokeOnUiThread at all). So those nodes
    // produced empty output. When IsEmbedded is true the solver must run the solve SYNCHRONOUSLY and publish
    // in the same pass.
    internal static class GhRunContext
    {
        public static bool IsEmbedded(GH_Component comp)
        {
            try
            {
                var canvas = Instances.ActiveCanvas;
                if (canvas == null) return true;                 // headless: Player / Compute / compiled run, no canvas
                var active = canvas.Document;
                if (active == null) return true;
                var mine = comp?.OnPingDocument();
                if (mine == null) return true;
                // My document isn't the one shown on the canvas => I live in a child document (cluster, or a
                // ScriptEditor-compiled component that hosts the definition in its own GH_Document).
                if (!ReferenceEquals(mine, active)) return true;
                return false;
            }
            catch { return false; }   // on any doubt, keep the proven interactive (async) path
        }
    }
}
