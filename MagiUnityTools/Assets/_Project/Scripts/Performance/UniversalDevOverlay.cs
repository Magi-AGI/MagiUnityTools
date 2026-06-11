using System.Collections.Generic;
using UnityEngine;

namespace Magi.UnityTools.Performance
{
    /// <summary>
    /// Universal development overlay for performance monitoring and debugging.
    /// Displays customizable performance metrics in an on-screen GUI.
    /// </summary>
    public class UniversalDevOverlay : MonoBehaviour
    {
        [Header("Display Settings")]
        [SerializeField] private bool show = true;
        [SerializeField] private Rect overlayRect = new Rect(10, 10, 250, 200);
        [SerializeField] private Color backgroundColor = new Color(0, 0, 0, 0.7f);
        [SerializeField] private Color textColor = Color.white;

        [Header("Performance Metrics")]
        [SerializeField] private bool showFPS = true;
        [SerializeField] private bool showFrameTime = true;
        [SerializeField] private bool showMemory = true;
        [SerializeField] private bool showDrawCalls = false;
        [SerializeField] private bool showTriangles = false;

        // Custom metrics that can be set by other systems
        private Dictionary<string, float> customMetrics = new Dictionary<string, float>();
        private Dictionary<string, Color> metricColors = new Dictionary<string, Color>();

        // FPS calculation
        private float deltaTime = 0.0f;
        private float fps = 0.0f;
        private float frameTimeMs = 0.0f;

        // Style cache
        private GUIStyle boxStyle;
        private GUIStyle labelStyle;

        private void Start()
        {
            // Create custom GUI styles
            CreateStyles();
        }

        private void CreateStyles()
        {
            boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = MakeTex(2, 2, backgroundColor);

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.normal.textColor = textColor;
            labelStyle.fontSize = 12;
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;

            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private void Update()
        {
            // Calculate FPS
            deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
            fps = 1.0f / deltaTime;
            frameTimeMs = deltaTime * 1000.0f;
        }

        /// <summary>
        /// Set a custom metric value to display.
        /// </summary>
        public void SetMetric(string name, float value, Color? color = null)
        {
            customMetrics[name] = value;
            if (color.HasValue)
                metricColors[name] = color.Value;
        }

        /// <summary>
        /// Remove a custom metric.
        /// </summary>
        public void RemoveMetric(string name)
        {
            customMetrics.Remove(name);
            metricColors.Remove(name);
        }

        /// <summary>
        /// Clear all custom metrics.
        /// </summary>
        public void ClearMetrics()
        {
            customMetrics.Clear();
            metricColors.Clear();
        }

        /// <summary>
        /// Toggle overlay visibility.
        /// </summary>
        public void Toggle()
        {
            show = !show;
        }

        private void OnGUI()
        {
            if (!show) return;

            // Ensure styles are created
            if (boxStyle == null || labelStyle == null)
                CreateStyles();

            GUILayout.BeginArea(overlayRect, boxStyle);

            // Built-in metrics
            if (showFPS)
            {
                labelStyle.normal.textColor = GetFPSColor(fps);
                GUILayout.Label($"FPS: {fps:F1}", labelStyle);
            }

            if (showFrameTime)
            {
                labelStyle.normal.textColor = textColor;
                GUILayout.Label($"Frame: {frameTimeMs:F2} ms", labelStyle);
            }

            if (showMemory)
            {
                float memoryMB = System.GC.GetTotalMemory(false) / (1024f * 1024f);
                GUILayout.Label($"Memory: {memoryMB:F1} MB", labelStyle);
            }

#if UNITY_EDITOR
            if (showDrawCalls)
            {
                GUILayout.Label($"Draw Calls: {UnityEditor.UnityStats.drawCalls}", labelStyle);
            }

            if (showTriangles)
            {
                GUILayout.Label($"Triangles: {UnityEditor.UnityStats.triangles:N0}", labelStyle);
            }
#endif

            // Custom metrics
            foreach (var kvp in customMetrics)
            {
                labelStyle.normal.textColor = metricColors.ContainsKey(kvp.Key) ?
                    metricColors[kvp.Key] : textColor;
                GUILayout.Label($"{kvp.Key}: {kvp.Value:F2}", labelStyle);
            }

            GUILayout.EndArea();
        }

        private Color GetFPSColor(float fps)
        {
            if (fps >= 60) return Color.green;
            if (fps >= 30) return Color.yellow;
            return Color.red;
        }

        /// <summary>
        /// Create a singleton instance of the DevOverlay.
        /// </summary>
        public static UniversalDevOverlay CreateSingleton()
        {
            var existing = FindOverlayInstance();
            if (existing != null)
                return existing;

            var go = new GameObject("[DevOverlay]");
            DontDestroyOnLoad(go);
            return go.AddComponent<UniversalDevOverlay>();
        }

        private static UniversalDevOverlay FindOverlayInstance()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<UniversalDevOverlay>();
#else
            return Object.FindObjectOfType<UniversalDevOverlay>();
#endif
        }

    }

    /// <summary>
    /// Static helper for quick access to the DevOverlay.
    /// </summary>
    public static class DevOverlay
    {
        private static UniversalDevOverlay instance;

        private static UniversalDevOverlay Instance
        {
            get
            {
                if (instance == null)
                    instance = UniversalDevOverlay.CreateSingleton();
                return instance;
            }
        }

        public static void SetMetric(string name, float value, Color? color = null)
        {
            Instance.SetMetric(name, value, color);
        }

        public static void RemoveMetric(string name)
        {
            Instance.RemoveMetric(name);
        }

        public static void Clear()
        {
            Instance.ClearMetrics();
        }

        public static void Toggle()
        {
            Instance.Toggle();
        }
    }
}






