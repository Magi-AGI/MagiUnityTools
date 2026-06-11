using System;

namespace Magi.UnityTools.Core
{
    /// <summary>
    /// Marks a struct as shared between C# and HLSL.
    /// Structs with this attribute should follow the pattern:
    /// - Use #if CSHARP_7_3_OR_NEWER for conditional compilation
    /// - Apply [StructLayout(LayoutKind.Sequential, Pack = 0)]
    /// - Only use types that exist in both C# and HLSL
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class SharedStructAttribute : Attribute
    {
        public string HlslFileName { get; }

        public SharedStructAttribute(string hlslFileName = null)
        {
            HlslFileName = hlslFileName;
        }
    }

    /// <summary>
    /// Example template for creating shared structs.
    /// Copy this pattern when creating new shared structs.
    /// </summary>
    public static class SharedStructTemplate
    {
        public const string TEMPLATE = @"
#if CSHARP_7_3_OR_NEWER
using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Magi.YourProject
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    [Serializable]
    [Magi.UnityTools.Core.SharedStruct]
#else
#define public
#endif
    public struct YourStruct
    {
        public float field1;
        public float2 field2;
        public int field3;

#if CSHARP_7_3_OR_NEWER
        // C#-only code (properties, methods, etc.)
        public static YourStruct Default => new YourStruct
        {
            field1 = 1.0f,
            field2 = float2.zero,
            field3 = 0
        };
    }
}
#else
    };  // YourStruct
#endif
";

        /// <summary>
        /// Get the include directive for a shared struct file.
        /// </summary>
        public static string GetIncludeDirective(string relativePath)
        {
            return $"#include_with_pragmas \"{relativePath}\"";
        }
    }
}