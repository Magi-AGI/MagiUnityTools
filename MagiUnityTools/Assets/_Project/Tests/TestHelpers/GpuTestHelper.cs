using UnityEngine;

namespace Magi.UnityTools.TestHelpers
{
    /// <summary>
    /// Helpers for GPU-related test setup and teardown.
    /// Creates/destroys temporary RenderTextures for tests that need GPU surfaces.
    /// </summary>
    public static class GpuTestHelper
    {
        /// <summary>
        /// Creates a temporary RenderTexture with the given dimensions and format.
        /// Caller must release via <see cref="DestroyTempRT"/>.
        /// </summary>
        public static RenderTexture CreateTempRT(int width, int height,
            RenderTextureFormat format = RenderTextureFormat.ARGBFloat)
        {
            var rt = new RenderTexture(width, height, 0, format)
            {
                enableRandomWrite = true
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Safely releases a RenderTexture created by <see cref="CreateTempRT"/>.
        /// </summary>
        public static void DestroyTempRT(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        /// <summary>
        /// Fills the entire RenderTexture with a solid color.
        /// Useful for setting up known initial state in tests.
        /// </summary>
        public static void ClearRT(RenderTexture rt, Color color)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, color);
            RenderTexture.active = prev;
        }
    }
}
