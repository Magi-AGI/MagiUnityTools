using UnityEngine;
using Magi.UnityTools.Patterns;

namespace Magi.UnityTools.TestHelpers
{
    /// <summary>
    /// Test helpers for ServiceLocator setup and teardown.
    /// Ensures clean singleton state between tests.
    /// </summary>
    public static class ServiceLocatorTestHelper
    {
        /// <summary>
        /// Creates a fresh ServiceLocator on a new GameObject.
        /// Destroys any existing singleton first.
        /// </summary>
        public static (GameObject go, ServiceLocator locator) CreateTestLocator()
        {
            ResetServiceLocator();
            var go = new GameObject("TestServiceLocator");
            var locator = go.AddComponent<ServiceLocator>();
            return (go, locator);
        }

        /// <summary>
        /// Destroys the test locator GameObject and resets the singleton.
        /// </summary>
        public static void DestroyTestLocator(GameObject go)
        {
            if (go != null)
                Object.DestroyImmediate(go);
            ResetServiceLocator();
        }

        /// <summary>
        /// Resets the ServiceLocator singleton to null via reflection.
        /// Use this in [TearDown] to ensure clean state.
        /// </summary>
        public static void ResetServiceLocator()
        {
            if (ServiceLocator.Instance != null)
                Object.DestroyImmediate(ServiceLocator.Instance.gameObject);

            // Also clear via reflection in case OnDestroy didn't fire (edit-mode)
            var prop = typeof(ServiceLocator).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            prop?.SetValue(null, null);
        }
    }
}
