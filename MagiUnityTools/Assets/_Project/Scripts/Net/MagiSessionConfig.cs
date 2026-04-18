using System.Collections.Generic;

namespace Magi.UnityTools.Net
{
    /// Connection parameters handed to IMagiTransport.OpenSessionAsync. Mirrors
    /// the fields of OpenSessionRequest plus the HTTP base URI — the transport
    /// derives the WebSocket URI (ws/wss + /session/{id}?seat=N) from BaseUri
    /// after the server hands back a SessionId.
    public sealed class MagiSessionConfig
    {
        public string BaseUri { get; set; }
        public string GameId { get; set; }
        public int SeatCount { get; set; }
        public long Seed { get; set; }
        public IReadOnlyDictionary<string, string> Options { get; set; }
    }
}
