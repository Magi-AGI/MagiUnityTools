using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Magi.UnityTools.Core
{
    /// <summary>
    /// Generic ping-pong buffer system for GPU compute operations.
    /// Automatically handles buffer swapping to prevent read/write conflicts.
    /// </summary>
    /// <typeparam name="T">Type of buffer (RenderTexture, ComputeBuffer, etc.)</typeparam>
    public class PingPongBuffer<T> : IDisposable where T : class
    {
        private T bufferA;
        private T bufferB;
        private bool isSwapped = false;

        /// <summary>
        /// The current buffer to read from.
        /// </summary>
        public T Read => isSwapped ? bufferB : bufferA;

        /// <summary>
        /// The current buffer to write to.
        /// </summary>
        public T Write => isSwapped ? bufferA : bufferB;

        /// <summary>
        /// Initialize with two pre-created buffers.
        /// </summary>
        public PingPongBuffer(T a, T b)
        {
            bufferA = a ?? throw new ArgumentNullException(nameof(a));
            bufferB = b ?? throw new ArgumentNullException(nameof(b));
        }

        /// <summary>
        /// Swap the read and write buffers.
        /// Call this after completing a compute dispatch.
        /// </summary>
        public void Swap()
        {
            isSwapped = !isSwapped;
        }

        /// <summary>
        /// Reset to initial state (A is read, B is write).
        /// </summary>
        public void Reset()
        {
            isSwapped = false;
        }

        /// <summary>
        /// Set compute shader texture parameters for both read and write buffers.
        /// </summary>
        public void SetComputeTextures(ComputeShader compute, int kernel, string readName, string writeName)
        {
            if (bufferA is RenderTexture && bufferB is RenderTexture)
            {
                compute.SetTexture(kernel, readName, Read as RenderTexture);
                compute.SetTexture(kernel, writeName, Write as RenderTexture);
            }
        }

        /// <summary>
        /// Set compute shader buffer parameters for both read and write buffers.
        /// </summary>
        public void SetComputeBuffers(ComputeShader compute, int kernel, string readName, string writeName)
        {
            if (bufferA is ComputeBuffer && bufferB is ComputeBuffer)
            {
                compute.SetBuffer(kernel, readName, Read as ComputeBuffer);
                compute.SetBuffer(kernel, writeName, Write as ComputeBuffer);
            }
            else if (bufferA is GraphicsBuffer && bufferB is GraphicsBuffer)
            {
                compute.SetBuffer(kernel, readName, Read as GraphicsBuffer);
                compute.SetBuffer(kernel, writeName, Write as GraphicsBuffer);
            }
        }

        public void Dispose()
        {
            // Dispose of owned resources
            if (bufferA is IDisposable disposableA)
                disposableA.Dispose();
            if (bufferB is IDisposable disposableB)
                disposableB.Dispose();

            // Destroy Unity objects
            if (bufferA is UnityEngine.Object objA)
                UnityEngine.Object.Destroy(objA);
            if (bufferB is UnityEngine.Object objB)
                UnityEngine.Object.Destroy(objB);
        }
    }

    /// <summary>
    /// Specialized PingPongBuffer for RenderTextures with creation helpers.
    /// </summary>
    public class PingPongRenderTexture : PingPongBuffer<RenderTexture>
    {
        public int Width { get; }
        public int Height { get; }
        public RenderTextureFormat Format { get; }

        /// <summary>
        /// Create a ping-pong buffer with two identically configured RenderTextures.
        /// </summary>
        public PingPongRenderTexture(int width, int height, RenderTextureFormat format, string baseName = "PingPong")
            : base(CreateRT(width, height, format, $"{baseName}_A"),
                   CreateRT(width, height, format, $"{baseName}_B"))
        {
            Width = width;
            Height = height;
            Format = format;
        }

        private static RenderTexture CreateRT(int width, int height, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(width, height, 0, format)
            {
                name = name,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Clear both buffers to a specified color.
        /// </summary>
        public void Clear(Color color)
        {
            var previousRT = RenderTexture.active;

            RenderTexture.active = Read;
            GL.Clear(true, true, color);

            RenderTexture.active = Write;
            GL.Clear(true, true, color);

            RenderTexture.active = previousRT;
        }
    }
}