using UnityEngine;
using UnityEngine.Rendering;

namespace Magi.UnityTools.Graphics
{
    /// <summary>
    /// Helper utilities for creating and managing RenderTextures with platform-optimized settings.
    /// </summary>
    public static class RenderTextureHelpers
    {
        /// <summary>
        /// Create a RenderTexture optimized for compute shader operations.
        /// </summary>
        public static RenderTexture CreateCompute(int width, int height, RenderTextureFormat format, string name = null)
        {
            var rt = new RenderTexture(width, height, 0, format)
            {
                name = name ?? "ComputeRT",
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Create a RenderTexture optimized for mobile platforms with half precision.
        /// </summary>
        public static RenderTexture CreateMobileOptimized(int width, int height, bool needsAlpha = true, string name = null)
        {
            var format = GetMobileFormat(needsAlpha);
            return CreateCompute(width, height, format, name ?? "MobileRT");
        }

        /// <summary>
        /// Get the optimal RenderTextureFormat for mobile platforms.
        /// </summary>
        public static RenderTextureFormat GetMobileFormat(bool needsAlpha = true)
        {
            // Use half precision on mobile for better performance
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                return needsAlpha ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.RGHalf;
            }
            // Fallback to float if half not supported
            return needsAlpha ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.RGFloat;
        }

        /// <summary>
        /// Create a RenderTexture for velocity/flow fields (2 components).
        /// </summary>
        public static RenderTexture CreateVelocityTexture(int width, int height, string name = null)
        {
            return CreateCompute(width, height, RenderTextureFormat.RGHalf, name ?? "VelocityRT");
        }

        /// <summary>
        /// Create a RenderTexture for pressure/divergence fields (1 component).
        /// </summary>
        public static RenderTexture CreateScalarTexture(int width, int height, string name = null)
        {
            return CreateCompute(width, height, RenderTextureFormat.RHalf, name ?? "ScalarRT");
        }

        /// <summary>
        /// Copy a RenderTexture with identical settings.
        /// </summary>
        public static RenderTexture Clone(RenderTexture source, string newName = null)
        {
            var rt = new RenderTexture(source.width, source.height, source.depth, source.format)
            {
                name = newName ?? $"{source.name}_Copy",
                enableRandomWrite = source.enableRandomWrite,
                filterMode = source.filterMode,
                wrapMode = source.wrapMode,
                useMipMap = source.useMipMap,
                autoGenerateMips = source.autoGenerateMips,
                anisoLevel = source.anisoLevel,
                volumeDepth = source.volumeDepth,
                antiAliasing = source.antiAliasing
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Clear a RenderTexture to a specified color.
        /// </summary>
        public static void Clear(RenderTexture rt, Color color)
        {
            var previousRT = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, color);
            RenderTexture.active = previousRT;
        }

        /// <summary>
        /// Release and destroy a RenderTexture safely.
        /// </summary>
        public static void SafeDestroy(ref RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                Object.Destroy(rt);
                rt = null;
            }
        }

        /// <summary>
        /// Check if a RenderTextureFormat is supported on the current platform.
        /// </summary>
        public static bool IsFormatSupported(RenderTextureFormat format)
        {
            return SystemInfo.SupportsRenderTextureFormat(format);
        }

        /// <summary>
        /// Get memory usage estimate for a RenderTexture in bytes.
        /// </summary>
        public static long GetMemoryUsage(RenderTexture rt)
        {
            if (rt == null) return 0;

            int bytesPerPixel = GetBytesPerPixel(rt.format);
            long pixels = (long)rt.width * rt.height * rt.volumeDepth;

            // Account for mipmaps
            if (rt.useMipMap)
            {
                pixels = (long)(pixels * 1.33f); // Approximate with mipmap chain
            }

            // Account for antialiasing
            if (rt.antiAliasing > 1)
            {
                pixels *= rt.antiAliasing;
            }

            return pixels * bytesPerPixel;
        }

        private static int GetBytesPerPixel(RenderTextureFormat format)
        {
            switch (format)
            {
                case RenderTextureFormat.RHalf:
                    return 2;
                case RenderTextureFormat.RGHalf:
                    return 4;
                case RenderTextureFormat.ARGBHalf:
                    return 8;
                case RenderTextureFormat.RFloat:
                    return 4;
                case RenderTextureFormat.RGFloat:
                    return 8;
                case RenderTextureFormat.ARGBFloat:
                    return 16;
                case RenderTextureFormat.ARGB32:
                    return 4;
                default:
                    return 4; // Default estimate
            }
        }
    }
}