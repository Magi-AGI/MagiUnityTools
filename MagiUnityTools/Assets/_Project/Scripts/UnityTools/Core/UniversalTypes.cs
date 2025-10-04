// Universal type definitions for consistent usage between C# and HLSL
// Provides standard Unity.Mathematics type aliases for cross-platform development

// Standard precision types for convenience
using float2 = Unity.Mathematics.float2;
using float3 = Unity.Mathematics.float3;
using float4 = Unity.Mathematics.float4;
using float2x2 = Unity.Mathematics.float2x2;
using float3x3 = Unity.Mathematics.float3x3;
using float4x4 = Unity.Mathematics.float4x4;

using int2 = Unity.Mathematics.int2;
using int3 = Unity.Mathematics.int3;
using int4 = Unity.Mathematics.int4;
using uint2 = Unity.Mathematics.uint2;
using uint3 = Unity.Mathematics.uint3;
using uint4 = Unity.Mathematics.uint4;

namespace Magi.UnityTools.Core
{
    /// <summary>
    /// Universal type system for cross-platform Unity development.
    /// These types ensure consistency between C# and compute/fragment shaders.
    /// </summary>
    public static class UniversalTypes
    {
        /// <summary>
        /// HLSL include content for type definitions.
        /// Copy this to an .hlsl file or include directly in shaders.
        /// </summary>
        public const string HLSL_TYPES_INCLUDE = @"
// Universal type definitions for HLSL
// Matches the C# usings in UniversalTypes.cs

#ifndef MAGI_UNIVERSAL_TYPES_HLSL
#define MAGI_UNIVERSAL_TYPES_HLSL

// Standard types - no custom aliases needed
// Projects can define their own precision types as needed

#endif // MAGI_UNIVERSAL_TYPES_HLSL
";

        /// <summary>
        /// Get the optimal RenderTextureFormat for the current platform.
        /// </summary>
        public static UnityEngine.RenderTextureFormat GetOptimalFormat(bool needsAlpha = true)
        {
#if UNITY_IOS || UNITY_ANDROID
            // Mobile: Use half precision
            return needsAlpha ?
                UnityEngine.RenderTextureFormat.ARGBHalf :
                UnityEngine.RenderTextureFormat.RGHalf;
#else
            // Desktop/Console: Use full precision
            return needsAlpha ?
                UnityEngine.RenderTextureFormat.ARGBFloat :
                UnityEngine.RenderTextureFormat.RGFloat;
#endif
        }

        /// <summary>
        /// Calculate optimal thread group size for compute shader dispatch.
        /// </summary>
        public static int CalculateThreadGroups(int dataSize, int threadGroupSize = 8)
        {
            return (dataSize + threadGroupSize - 1) / threadGroupSize;
        }
    }
}
