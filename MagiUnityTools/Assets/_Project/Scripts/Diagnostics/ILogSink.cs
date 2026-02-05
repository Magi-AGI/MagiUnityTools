using System.Collections.Generic;

namespace Magi.UnityTools.Diagnostics
{
    /// <summary>
    /// Abstraction for log sinks so implementations can be swapped (e.g., file-backed, CI).
    /// </summary>
    public interface ILogSink
    {
        void Add(string message);
        IEnumerable<string> GetEntries();
        void Clear();
    }
}
