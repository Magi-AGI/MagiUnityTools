using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Magi.UnityTools.Diagnostics;

namespace Magi.UnityTools.Tests
{
    public class LogSinkTests
    {
        private class MockLogSink : ILogSink
        {
            private readonly List<string> entries = new();
            public void Add(string message) => entries.Add(message);
            public IEnumerable<string> GetEntries() => entries;
            public void Clear() => entries.Clear();
        }

        [Test]
        public void Add_StoresEntries()
        {
            var sink = new MockLogSink();
            sink.Add("one");
            sink.Add("two");
            CollectionAssert.AreEqual(new[] { "one", "two" }, sink.GetEntries().ToArray());
        }

        [Test]
        public void Clear_RemovesAllEntries()
        {
            var sink = new MockLogSink();
            sink.Add("entry");
            sink.Clear();
            Assert.IsEmpty(sink.GetEntries());
        }

        [Test]
        public void GetEntries_EmptyByDefault()
        {
            var sink = new MockLogSink();
            Assert.IsEmpty(sink.GetEntries());
        }
    }
}
