using System;
using System.Collections.Generic;
using UnityEngine;

namespace Magi.UnityTools.Patterns
{
    /// <summary>
    /// Minimal service locator with optional auto-discovery.
    /// Resolves dependencies at runtime so components don't need inspector-wired references.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ServiceLocator : MonoBehaviour
    {
        [Tooltip("Optional explicit services to register first. Must implement IService.")]
        [SerializeField] private List<UnityEngine.Object> services = new();

        [Tooltip("Auto-discover all IService MonoBehaviours in the scene at Awake.")]
        [SerializeField] private bool autoDiscover = true;

        private readonly Dictionary<Type, object> registry = new();

        public static ServiceLocator Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            // Register explicit services first (inspector-assigned overrides)
            foreach (var obj in services)
            {
                if (obj is IService svc)
                    RegisterService(svc);
            }

            // Auto-discover remaining IService implementations in the scene
            if (autoDiscover)
            {
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (mb is IService svc && !registry.ContainsKey(svc.GetType()))
                        RegisterService(svc);
                }
            }
        }

        public void RegisterService(IService service)
        {
            var type = service.GetType();
            registry[type] = service;

            foreach (var iface in type.GetInterfaces())
            {
                if (iface == typeof(IService)) continue;
                registry[iface] = service;
            }
        }

        public T Resolve<T>() where T : class
        {
            registry.TryGetValue(typeof(T), out var svc);
            return svc as T;
        }

        public bool TryResolve<T>(out T svc) where T : class
        {
            svc = Resolve<T>();
            return svc != null;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
