using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GodotCSharpProfiler.HarmonyInstrumentation;

public sealed class HarmonyFilteredInstrumentor : IDisposable
{
    private static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(HarmonyFilteredInstrumentor), nameof(Prefix));
    private static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(HarmonyFilteredInstrumentor), nameof(Finalizer));
    private static readonly ConcurrentDictionary<MethodBase, Counter> Counters = new();
    private static int _enabled = 1;

    private readonly InstrumentationOptions _options;
    private readonly Harmony _harmony;
    private readonly List<MethodBase> _patchedMethods = [];
    private bool _disposed;

    public HarmonyFilteredInstrumentor(InstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OwnerId))
        {
            throw new ArgumentException("An owner-scoped Harmony ID is required.", nameof(options));
        }

        if (options.SelectedTypes is null || options.SelectedTypes.Count == 0)
        {
            throw new ArgumentException("At least one explicit selected type is required.", nameof(options));
        }

        if (options.MaxMethods <= 0 || options.MaxNameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Method and name bounds must be positive.");
        }

        _options = options;
        _harmony = new Harmony(options.OwnerId);
    }

    public string OwnerId => _options.OwnerId;

    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public InstrumentationPreview Preview()
    {
        var candidates = DiscoverCandidates();
        var limited = candidates.Take(_options.MaxMethods).ToArray();
        var items = limited.Select(Classify).ToArray();
        return new InstrumentationPreview(
            items,
            candidates.Count,
            items.Count(item => item.Disposition == MethodDisposition.Supported),
            items.Count(item => item.Disposition == MethodDisposition.Skipped),
            candidates.Count - limited.Length);
    }

    public PatchSessionMetrics Patch()
    {
        ThrowIfDisposed();
        if (_patchedMethods.Count != 0)
        {
            throw new InvalidOperationException("This session is already patched.");
        }

        var preview = Preview();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var item in preview.Items.Where(item => item.Disposition == MethodDisposition.Supported))
            {
                _harmony.Patch(
                    item.Method,
                    prefix: new HarmonyMethod(PrefixMethod),
                    finalizer: new HarmonyMethod(FinalizerMethod));
                _patchedMethods.Add(item.Method);
                Counters.TryAdd(item.Method, new Counter());
            }
        }
        catch
        {
            Unpatch();
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        return new PatchSessionMetrics(stopwatch.Elapsed, _patchedMethods.Count);
    }

    public MethodMeasurement GetMeasurement(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Counters.TryGetValue(method, out var counter)
            ? counter.Snapshot()
            : default;
    }

    public IReadOnlyDictionary<string, MethodMeasurement> Snapshot()
    {
        return _patchedMethods.ToDictionary(
            FormatName,
            method => GetMeasurement(method),
            StringComparer.Ordinal);
    }

    public void ResetMeasurements()
    {
        foreach (var method in _patchedMethods)
        {
            Counters.AddOrUpdate(method, static _ => new Counter(), static (_, _) => new Counter());
        }
    }

    public void Unpatch()
    {
        _harmony.UnpatchAll(_options.OwnerId);
        foreach (var method in _patchedMethods)
        {
            Counters.TryRemove(method, out _);
        }

        _patchedMethods.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unpatch();
        _disposed = true;
    }

    private List<MethodBase> DiscoverCandidates()
    {
        var result = new List<MethodBase>();
        var selected = _options.SelectedTypes.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal);
        foreach (var type in selected)
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            result.AddRange(type.GetConstructors(flags));
            result.AddRange(type.GetMethods(flags));
        }

        return result
            .Distinct(MethodBaseComparer.Instance)
            .OrderBy(FormatName, StringComparer.Ordinal)
            .ToList();
    }

    private MethodInventoryItem Classify(MethodBase method)
    {
        var name = Bound(FormatName(method));
        var compilerGenerated = method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || method.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) == true;
        var isMoveNext = method.Name == "MoveNext" && compilerGenerated;
        var stateMachineCategory = IsAsyncStateMachine(method.DeclaringType)
            ? MethodCategory.AsyncStateMachineMoveNext
            : IsIteratorStateMachine(method.DeclaringType)
                ? MethodCategory.IteratorStateMachineMoveNext
                : MethodCategory.CompilerGenerated;

        if (method.IsAbstract)
        {
            return Skip(method, name, MethodCategory.Abstract, "abstract method has no patchable body");
        }

        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 || method.GetMethodBody() is null)
        {
            return Skip(method, name, MethodCategory.NativeOrExtern, "native/extern/runtime method has no managed IL body");
        }

        if (method.IsConstructor && !_options.IncludeConstructors)
        {
            return Skip(method, name, MethodCategory.Constructor, "constructors are excluded by default");
        }

        if (method.IsSpecialName && IsAccessor(method))
        {
            return _options.IncludeAccessors
                ? new MethodInventoryItem(method, name, MethodDisposition.Supported, MethodCategory.PropertyAccessor, "accessor explicitly included")
                : Skip(method, name, MethodCategory.PropertyAccessor, "property/event accessors are excluded by default");
        }

        if (compilerGenerated && !_options.IncludeCompilerGenerated)
        {
            return Skip(method, name, isMoveNext ? stateMachineCategory : MethodCategory.CompilerGenerated, "compiler-generated methods are excluded by default");
        }

        if (IsProfilerNamespace(method.DeclaringType) && !_options.IncludeProfilerNamespaces)
        {
            return Skip(method, name, MethodCategory.ProfilerNamespace, "profiler namespaces are excluded by default");
        }

        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
        {
            return Skip(method, name, MethodCategory.Generic, "open generic methods require explicit closed instantiations and are skipped by this proof");
        }

        var ilSize = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
        if (ilSize <= _options.TrivialIlByteThreshold && !_options.IncludeTrivial)
        {
            return Skip(method, name, MethodCategory.Trivial, $"IL size {ilSize} is at or below trivial threshold");
        }

        return new MethodInventoryItem(method, name, MethodDisposition.Supported, CategorizeSupported(method, isMoveNext, stateMachineCategory), "supported managed method");
    }

    private static MethodCategory CategorizeSupported(MethodBase method, bool isMoveNext, MethodCategory stateMachineCategory)
    {
        if (isMoveNext)
        {
            return stateMachineCategory;
        }

        if (method.IsConstructor)
        {
            return MethodCategory.Constructor;
        }

        if (method.IsSpecialName && IsAccessor(method))
        {
            return MethodCategory.PropertyAccessor;
        }

        if (method.MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining))
        {
            return MethodCategory.InliningCandidate;
        }

        var overloadCount = method.DeclaringType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Count(candidate => candidate.Name == method.Name) ?? 0;
        return overloadCount > 1 ? MethodCategory.Overloaded : MethodCategory.Ordinary;
    }

    private static bool IsAccessor(MethodBase method) =>
        method.Name.StartsWith("get_", StringComparison.Ordinal)
        || method.Name.StartsWith("set_", StringComparison.Ordinal)
        || method.Name.StartsWith("add_", StringComparison.Ordinal)
        || method.Name.StartsWith("remove_", StringComparison.Ordinal);

    private static bool IsAsyncStateMachine(Type? type) =>
        type?.GetInterfaces().Any(interfaceType => interfaceType == typeof(IAsyncStateMachine)) == true;

    private static bool IsIteratorStateMachine(Type? type) =>
        type is not null
        && typeof(System.Collections.IEnumerator).IsAssignableFrom(type)
        && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool IsProfilerNamespace(Type? type) =>
        type?.Namespace?.StartsWith("GodotCSharpProfiler", StringComparison.Ordinal) == true
        || type?.Namespace?.StartsWith("Apeworks.GodotCSharpProfiler", StringComparison.Ordinal) == true;

    private static MethodInventoryItem Skip(MethodBase method, string name, MethodCategory category, string reason) =>
        new(method, name, MethodDisposition.Skipped, category, reason);

    private string Bound(string value) => value.Length <= _options.MaxNameLength ? value : value[.._options.MaxNameLength];

    private static string FormatName(MethodBase method)
    {
        var declaringType = method.DeclaringType?.FullName ?? "<global>";
        var parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
        return $"{declaringType}.{method.Name}({parameters})";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Prefix(MethodBase __originalMethod, out PrefixState __state)
    {
        if (!Enabled)
        {
            __state = default;
            return;
        }

        __state = new PrefixState(true, Stopwatch.GetTimestamp());
    }

    private static Exception? Finalizer(MethodBase __originalMethod, PrefixState __state, Exception? __exception)
    {
        if (__state.Started)
        {
            var elapsed = Stopwatch.GetTimestamp() - __state.StartTimestamp;
            Counters.GetOrAdd(__originalMethod, static _ => new Counter()).Add(elapsed, __exception is not null);
        }

        return __exception;
    }

    private readonly record struct PrefixState(bool Started, long StartTimestamp);

    private sealed class Counter
    {
        private long _calls;
        private long _inclusiveTicks;
        private long _exceptions;

        public void Add(long ticks, bool exception)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _inclusiveTicks, ticks);
            if (exception)
            {
                Interlocked.Increment(ref _exceptions);
            }
        }

        public MethodMeasurement Snapshot() => new(
            Interlocked.Read(ref _calls),
            Interlocked.Read(ref _inclusiveTicks),
            Interlocked.Read(ref _exceptions));
    }

    private sealed class MethodBaseComparer : IEqualityComparer<MethodBase>
    {
        public static readonly MethodBaseComparer Instance = new();

        public bool Equals(MethodBase? x, MethodBase? y) => x == y;

        public int GetHashCode(MethodBase obj) => obj.GetHashCode();
    }
}
