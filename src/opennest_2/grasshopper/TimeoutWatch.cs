using System;

namespace opennest_2
{
    // A one-shot wall-clock watchdog: after `seconds` it invokes the supplied cancel action (which trips the
    // same stop path ESC uses, so the solver keeps its best-so-far / already-committed result). Reused by all
    // solver components. Threadpool callback — the cancel action only sets flags the solve already polls, so
    // it is safe from any thread and works in both the background-thread and embedded/inline solve paths.
    internal sealed class TimeoutWatch
    {
        private System.Timers.Timer _t;

        public void Start(double seconds, Action onTimeout)
        {
            Stop();
            if (seconds <= 0 || onTimeout == null) return;
            _t = new System.Timers.Timer(seconds * 1000.0) { AutoReset = false };
            _t.Elapsed += (s, e) => { try { onTimeout(); } catch { } };
            _t.Start();
        }

        public void Stop()
        {
            try { _t?.Stop(); _t?.Dispose(); } catch { }
            _t = null;
        }
    }
}
