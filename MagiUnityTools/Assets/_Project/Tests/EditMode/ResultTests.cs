using System;
using NUnit.Framework;
using Magi.UnityTools.Core;

namespace Magi.UnityTools.Tests
{
    public class ResultTests
    {
        [Test]
        public void Success_IsSuccess()
        {
            var r = Result.Success();
            Assert.IsTrue(r.IsSuccess);
            Assert.IsEmpty(r.Error);
            Assert.IsNull(r.Exception);
        }

        [Test]
        public void Fail_String_IsNotSuccess()
        {
            var r = Result.Fail("something broke");
            Assert.IsFalse(r.IsSuccess);
            Assert.AreEqual("something broke", r.Error);
            Assert.IsNull(r.Exception);
        }

        [Test]
        public void Fail_Exception_PreservesException()
        {
            var ex = new InvalidOperationException("bad state");
            var r = Result.Fail(ex);
            Assert.IsFalse(r.IsSuccess);
            Assert.AreEqual("bad state", r.Error);
            Assert.AreSame(ex, r.Exception);
        }

        [Test]
        public void WithContext_PrependsOnFailure()
        {
            var r = Result.Fail("timeout").WithContext("Init");
            Assert.AreEqual("Init: timeout", r.Error);
        }

        [Test]
        public void WithContext_NoOpOnSuccess()
        {
            var r = Result.Success().WithContext("Init");
            Assert.IsTrue(r.IsSuccess);
        }

        [Test]
        public void ToString_Success_ReturnsOK()
        {
            Assert.AreEqual("OK", Result.Success().ToString());
        }

        [Test]
        public void ToString_Fail_ReturnsError()
        {
            Assert.AreEqual("Error: oops", Result.Fail("oops").ToString());
        }
    }
}
