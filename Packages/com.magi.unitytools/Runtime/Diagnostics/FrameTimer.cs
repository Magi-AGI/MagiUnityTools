using System.Diagnostics;

namespace Magi.UnityTools.Diagnostics
{
    public class FrameTimer
    {
        private readonly Stopwatch sw = new Stopwatch();
        public void Begin() => sw.Restart();
        public double EndMs() { sw.Stop(); return sw.Elapsed.TotalMilliseconds; }
    }
}

