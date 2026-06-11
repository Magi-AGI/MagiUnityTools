# MagiUnityTools

Common Unity patterns, diagnostics, and utilities library for Magi-AGI Unity projects.

## Overview

MagiUnityTools is a Unity Package Manager (UPM) compatible library providing battle-tested design patterns, performance diagnostics, and mathematical utilities used across the Magi ecosystem. It demonstrates Unity best practices while offering reusable components for production projects.

## Package: com.magi.unitytools

Runtime helpers and editor tooling for Unity development.

### Package Structure

```
Assets/
└── _Project/
    ├── Scripts/
    │   └── UnityTools/
    │       ├── Core/
    │       ├── Diagnostics/
    │       ├── Patterns/
    │       ├── Performance/
    │       ├── Magi.UnityTools.asmdef
    │       └── package.json
    └── Editor/ (optional)
```
### Using with MagiUnityDependencyManager

```yaml
packages:
  com.magi.unitytools: file:../../MagiUnityTools/MagiUnityTools/Assets/_Project/Scripts
```

Run `../MagiUnityDependencyManager/magi-deps.ps1 apply -ProjectPath ./MyProject -Strict` to regenerate `Packages/manifest.json`, then `verify -Strict` before committing.



## Core Features

### Design Patterns

#### Singleton Pattern Variants
```csharp
// MonoBehaviour Singleton
public class GameManager : MonoSingleton<GameManager>
{
    protected override void OnSingletonAwake()
    {
        // One-time initialization
    }
}

// Persistent Singleton (survives scene changes)
public class AudioManager : PersistentSingleton<AudioManager>
{
    protected override void OnPersistentAwake()
    {
        // Setup that persists across scenes
    }
}

// Lazy Singleton (created on first access)
public class ConfigManager : LazySingleton<ConfigManager>
{
    protected override void Initialize()
    {
        // Deferred initialization
    }
}
```

#### Advanced Object Pooling
```csharp
// Generic pool with auto-expansion
var particlePool = new AutoExpandPool<ParticleSystem>(
    prefab: particlePrefab,
    initialSize: 50,
    maxSize: 200,
    expansionRate: 1.5f
);

// Pooled object with automatic return
using (var pooledParticle = particlePool.GetScoped())
{
    pooledParticle.Value.Play();
    // Automatically returned to pool when disposed
}
```

#### State Machine Framework
```csharp
public class PlayerStateMachine : StateMachine<PlayerState>
{
    void Start()
    {
        AddState(new IdleState());
        AddState(new MovingState());
        AddState(new AttackingState());

        SetInitialState<IdleState>();
    }
}
```

### Diagnostics Tools

#### Performance Profiling
```csharp
using MagiUnityTools.Diagnostics;

void Update()
{
    using (var marker = ProfilerHelpers.Sample("AI Update"))
    {
        UpdateAI();
        marker.SetMetadata("entities", entityCount);
    }

    // Automatic performance warnings
    if (ProfilerHelpers.DetectSpike("AI Update", 5.0f))
    {
        Debug.LogWarning("AI performance spike detected!");
    }
}
```

#### Memory Tracking
```csharp
// Track memory allocations
MemoryTracker.BeginSample("Level Load");
LoadLevel();
var allocation = MemoryTracker.EndSample("Level Load");

Debug.Log($"Level load allocated: {allocation.TotalMB:F2} MB");
Debug.Log($"GC collections: {allocation.GCCount}");
```

### Mathematical Utilities

#### Optimized Math Operations
```csharp
// SIMD-optimized operations
Vector3[] positions = GetPositions();
Vector3[] results = FastMath.BatchNormalize(positions);

// Fast approximations
float sqrt = FastMath.FastSqrt(value);      // ~2x faster
float sin = FastMath.FastSin(angle);        // ~3x faster
float atan2 = FastMath.FastAtan2(y, x);     // ~2.5x faster
```

#### Interpolation & Easing
```csharp
// 30+ easing functions
float t = Interpolation.EaseInOutCubic(normalizedTime);
Vector3 pos = Interpolation.CatmullRom(p0, p1, p2, p3, t);

// Spring physics
var spring = new Spring(stiffness: 100, damping: 10);
position = spring.Update(position, target, deltaTime);
```

## Installation

### Via Unity Package Manager

1. Open Package Manager (Window → Package Manager)
2. Click "+" → "Add package from disk..."
3. Select `MagiUnityTools/Packages/com.magi.unitytools/package.json`

### Via manifest.json

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.magi.unitytools": "file:../../MagiUnityTools/Packages/com.magi.unitytools"
  }
}
```

### Via MagiUnityDependencyManager

Add to `depfile.yaml`:
```yaml
packages:
  - name: com.magi.unitytools
    path: ../MagiUnityTools/Packages/com.magi.unitytools
    version: 1.0.0
```

## Best Practices Demonstrated

### Performance
- Zero-allocation patterns where possible
- Object pooling for frequently instantiated objects
- Efficient data structures and algorithms
- Profiler-friendly code with markers

### Code Quality
- SOLID principles throughout
- Comprehensive XML documentation
- Unit test coverage
- Thread-safe implementations where needed

### Unity-Specific
- Proper lifecycle management
- Editor/Runtime code separation
- Conditional compilation for platforms
- Asset database integration

## Usage in Production

MagiUnityTools is designed for production use:

- **Battle-tested**: Used in multiple shipped projects
- **Performance-focused**: Optimized for mobile and VR
- **Well-documented**: Comprehensive API documentation
- **Maintained**: Regular updates and bug fixes

## Testing

Run tests via Unity Test Framework:
```
Window → General → Test Runner → Run All
```

Test categories:
- **Unit Tests**: Core functionality
- **Integration Tests**: Unity-specific features
- **Performance Tests**: Benchmark critical paths

## Editor Extensions

### Performance Analyzer Window
```
Window → Magi Tools → Performance Analyzer
```
- Real-time performance metrics
- Memory allocation tracking
- Draw call analysis
- Automatic optimization suggestions

### Pattern Validator
```
Window → Magi Tools → Pattern Validator
```
- Validates singleton usage
- Checks for memory leaks
- Identifies anti-patterns
- Suggests improvements

## Documentation

- [API Reference](Documentation/API/index.md)
- [Pattern Cookbook](Documentation/Patterns.md)
- [Performance Guide](Documentation/Performance.md)
- [Migration Guide](Documentation/Migration.md)

## Examples

See the `Samples~` folder for example implementations:
- Singleton usage examples
- Object pooling scenarios
- State machine implementations
- Performance optimization techniques

## Related Projects

- **[Inkling](../Inkling)**: Production usage example
- **[InkTools](../InkTools)**: Specialized simulation tools
- **[MagiUnityDependencyManager](../MagiUnityDependencyManager)**: Dependency management
- **[InkModel](../InkModel)**: ML pipeline integration

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines.

## License

See [LICENSE](LICENSE) for details.

