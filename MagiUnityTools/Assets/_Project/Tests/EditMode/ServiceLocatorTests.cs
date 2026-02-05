using NUnit.Framework;
using UnityEngine;
using Magi.UnityTools.Patterns;

namespace Magi.UnityTools.Tests
{
    public class ServiceLocatorTests
    {
        private GameObject locatorGo;
        private ServiceLocator locator;

        [SetUp]
        public void SetUp()
        {
            // Clear any previous instance
            if (ServiceLocator.Instance != null)
                Object.DestroyImmediate(ServiceLocator.Instance.gameObject);

            locatorGo = new GameObject("TestLocator");
            locator = locatorGo.AddComponent<ServiceLocator>();
        }

        [TearDown]
        public void TearDown()
        {
            if (locatorGo != null)
                Object.DestroyImmediate(locatorGo);
        }

        private interface ITestService : IService { string Name { get; } }

        private class TestService : MonoBehaviour, ITestService
        {
            public string Name => "TestImpl";
        }

        private interface IOtherService : IService { }

        private class OtherService : MonoBehaviour, IOtherService { }

        [Test]
        public void RegisterAndResolve_ByConcreteType()
        {
            var go = new GameObject("Svc");
            var svc = go.AddComponent<TestService>();
            locator.RegisterService(svc);

            var resolved = locator.Resolve<TestService>();
            Assert.IsNotNull(resolved);
            Assert.AreEqual("TestImpl", resolved.Name);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void RegisterAndResolve_ByInterface()
        {
            var go = new GameObject("Svc");
            var svc = go.AddComponent<TestService>();
            locator.RegisterService(svc);

            var resolved = locator.Resolve<ITestService>();
            Assert.IsNotNull(resolved);
            Assert.AreEqual("TestImpl", resolved.Name);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void TryResolve_ReturnsFalse_WhenNotRegistered()
        {
            var found = locator.TryResolve<IOtherService>(out var svc);
            Assert.IsFalse(found);
            Assert.IsNull(svc);
        }

        [Test]
        public void TryResolve_ReturnsTrue_WhenRegistered()
        {
            var go = new GameObject("Svc");
            var svc = go.AddComponent<OtherService>();
            locator.RegisterService(svc);

            var found = locator.TryResolve<IOtherService>(out var resolved);
            Assert.IsTrue(found);
            Assert.IsNotNull(resolved);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Resolve_ReturnsNull_WhenNotRegistered()
        {
            var resolved = locator.Resolve<IOtherService>();
            Assert.IsNull(resolved);
        }
    }
}
