using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using GodotCSharpProfiler.HarmonyInstrumentation;
using HarmonyLib;
using HarmonyProofFixtures;
using Xunit;

namespace GodotCSharpProfiler.Harmony.Tests;

[CollectionDefinition("Harmony", DisableParallelization = true)]
public sealed class HarmonyCollection;

[Collection("Harmony")]
public sealed class HarmonyInstrumentationTests : IDisposable
{
    public HarmonyInstrumentationTests()
    {
        HarmonyFilteredInstrumentor.Enabled = true;
    }

    public void Dispose()
    {
        HarmonyFilteredInstrumentor.Enabled = true;
    }

    [Fact]
    public void Preview_is_explicit_bounded_and_defaults_to_safe_skips()
    {
        var options = Options(typeof(MethodFixture), typeof(UnsupportedFixture));
        using var instrumentor = new HarmonyFilteredInstrumentor(options);

        var preview = instrumentor.Preview();

        Assert.DoesNotContain(preview.Items, item => item.Method.DeclaringType == typeof(UnselectedFixture));
        Assert.All(preview.Items, item => Assert.True(item.BoundedName.Length <= options.MaxNameLength));
        Assert.True(preview.CandidateCount <= options.MaxMethods);
        Assert.Contains(preview.Items, item => item.Method.Name == nameof(MethodFixture.Ordinary) && item.Disposition == MethodDisposition.Supported);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.Overloaded && item.Disposition == MethodDisposition.Supported);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.Generic && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.PropertyAccessor && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.Constructor && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.Abstract && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.NativeOrExtern && item.Disposition == MethodDisposition.Skipped);
    }

    [Fact]
    public void Preview_classifies_async_iterator_and_profiler_namespace_default_skips()
    {
        var asyncType = typeof(MethodFixture).GetMethod(nameof(MethodFixture.Async))!
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var iteratorType = typeof(MethodFixture).GetMethod(nameof(MethodFixture.Iterator))!
            .GetCustomAttribute<IteratorStateMachineAttribute>()!.StateMachineType;
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(
            asyncType,
            iteratorType,
            typeof(GodotCSharpProfiler.Internal.ProfilerOwnedFixture)));

        var preview = instrumentor.Preview();

        Assert.Contains(preview.Items, item => item.Method.Name == "MoveNext" && item.Category == MethodCategory.AsyncStateMachineMoveNext && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Method.Name == "MoveNext" && item.Category == MethodCategory.IteratorStateMachineMoveNext && item.Disposition == MethodDisposition.Skipped);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.ProfilerNamespace && item.Disposition == MethodDisposition.Skipped);
    }

    [Fact]
    public void Method_limit_omits_deterministically_and_reports_count()
    {
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(typeof(MethodFixture)) with { MaxMethods = 3 });

        var preview = instrumentor.Preview();

        Assert.Equal(3, preview.Items.Count);
        Assert.Equal(preview.CandidateCount - 3, preview.OmittedByMethodLimit);
    }

    [Fact]
    public void Patch_records_exact_calls_inclusive_duration_recursion_and_exception_cleanup()
    {
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(typeof(MethodFixture)));
        instrumentor.Patch();
        var fixture = new MethodFixture();
        var ordinary = Method(nameof(MethodFixture.Ordinary), typeof(int));
        var recursive = Method(nameof(MethodFixture.Recursive), typeof(int));
        var throwing = Method(nameof(MethodFixture.Throwing));
        instrumentor.ResetMeasurements();

        Assert.Equal(2, fixture.Ordinary(1));
        Assert.Equal(3, fixture.Recursive(2));
        Assert.Throws<FixtureException>(fixture.Throwing);
        Assert.Equal(3, fixture.Ordinary(2));

        var ordinaryMeasurement = instrumentor.GetMeasurement(ordinary);
        var recursiveMeasurement = instrumentor.GetMeasurement(recursive);
        var throwingMeasurement = instrumentor.GetMeasurement(throwing);
        Assert.Equal(2, ordinaryMeasurement.Calls);
        Assert.True(ordinaryMeasurement.InclusiveTimestampTicks > 0);
        Assert.Equal(3, recursiveMeasurement.Calls);
        Assert.True(recursiveMeasurement.InclusiveTimestampTicks > 0);
        Assert.Equal(1, throwingMeasurement.Calls);
        Assert.Equal(1, throwingMeasurement.Exceptions);
        Assert.True(throwingMeasurement.InclusiveTimestampTicks > 0);
    }

    [Fact]
    public void Overloads_are_instrumented_and_open_generic_is_safely_skipped()
    {
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(typeof(MethodFixture)));
        var preview = instrumentor.Preview();
        instrumentor.Patch();
        var fixture = new MethodFixture();
        instrumentor.ResetMeasurements();

        Assert.Equal(4, fixture.Overloaded(2));
        Assert.Equal("xx", fixture.Overloaded("x"));
        Assert.Equal(7, fixture.Generic(7));

        Assert.Equal(1, instrumentor.GetMeasurement(Method(nameof(MethodFixture.Overloaded), typeof(int))).Calls);
        Assert.Equal(1, instrumentor.GetMeasurement(Method(nameof(MethodFixture.Overloaded), typeof(string))).Calls);
        Assert.Contains(preview.Items, item => item.Method.Name == nameof(MethodFixture.Generic) && item.Category == MethodCategory.Generic && item.Disposition == MethodDisposition.Skipped);
    }

    [Fact]
    public async Task Opt_in_patches_async_and_iterator_state_machine_move_next()
    {
        var asyncType = typeof(MethodFixture).GetMethod(nameof(MethodFixture.Async))!
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var iteratorType = typeof(MethodFixture).GetMethod(nameof(MethodFixture.Iterator))!
            .GetCustomAttribute<IteratorStateMachineAttribute>()!.StateMachineType;
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(asyncType, iteratorType) with
        {
            IncludeCompilerGenerated = true,
            IncludeAccessors = true,
            IncludeTrivial = true
        });
        instrumentor.Patch();
        var fixture = new MethodFixture();
        instrumentor.ResetMeasurements();

        Assert.Equal(4, await fixture.Async(3));
        Assert.Equal(new[] { 0, 1, 2 }, fixture.Iterator(3).ToArray());

        var measurements = instrumentor.Snapshot();
        Assert.Contains(measurements, pair => pair.Key.Contains("MoveNext", StringComparison.Ordinal) && pair.Value.Calls > 0);
    }

    [Fact]
    public void Constructor_accessor_and_aggressive_inline_are_classified_and_opt_in_patchable()
    {
        using var instrumentor = new HarmonyFilteredInstrumentor(Options(typeof(MethodFixture)) with
        {
            IncludeConstructors = true,
            IncludeAccessors = true
        });
        var preview = instrumentor.Preview();
        instrumentor.Patch();
        instrumentor.ResetMeasurements();

        var fixture = new MethodFixture { Value = 9 };
        Assert.Equal(9, fixture.Value);
        Assert.Equal(4, fixture.AggressivelyInlined(2));

        Assert.Contains(preview.Items, item => item.Category == MethodCategory.Constructor && item.Disposition == MethodDisposition.Supported);
        Assert.Contains(preview.Items, item => item.Category == MethodCategory.PropertyAccessor && item.Disposition == MethodDisposition.Supported);
        Assert.Contains(preview.Items, item => item.Method.Name == nameof(MethodFixture.AggressivelyInlined) && item.Category == MethodCategory.InliningCandidate);
        Assert.True(instrumentor.GetMeasurement(typeof(MethodFixture).GetConstructor(Type.EmptyTypes)!).Calls >= 1);
        Assert.True(instrumentor.GetMeasurement(Method("set_Value", typeof(int))).Calls >= 1);
        Assert.True(instrumentor.GetMeasurement(Method("get_Value")).Calls >= 1);
        Assert.Equal(1, instrumentor.GetMeasurement(Method(nameof(MethodFixture.AggressivelyInlined), typeof(int))).Calls);
    }

    [Fact]
    public void UnpatchAll_is_owner_scoped_and_repeated_cycles_are_clean()
    {
        var method = Method(nameof(MethodFixture.Ordinary), typeof(int));
        var observerOwner = $"proof.observer.{Guid.NewGuid():N}";
        var observer = new HarmonyLib.Harmony(observerOwner);
        observer.Patch(method, prefix: new HarmonyMethod(typeof(HarmonyInstrumentationTests).GetMethod(nameof(ObserverPrefix), BindingFlags.NonPublic | BindingFlags.Static)!));
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                using var instrumentor = new HarmonyFilteredInstrumentor(Options(typeof(MethodFixture)) with { OwnerId = $"proof.session.{cycle}.{Guid.NewGuid():N}" });
                instrumentor.Patch();
                Assert.Contains(instrumentor.OwnerId, HarmonyLib.Harmony.GetPatchInfo(method)!.Owners);
                instrumentor.Unpatch();
                var owners = HarmonyLib.Harmony.GetPatchInfo(method)!.Owners;
                Assert.DoesNotContain(instrumentor.OwnerId, owners);
                Assert.Contains(observerOwner, owners);
            }
        }
        finally
        {
            observer.UnpatchAll(observerOwner);
        }
    }

    [Fact]
    public void Collectible_context_exposes_hard_reload_limitation_after_owner_unpatch()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "CollectibleFixture.dll");
        Assert.True(File.Exists(fixturePath), fixturePath);

        var weakReference = PatchInvokeAndUnload(fixturePath);

        for (var attempt = 0; weakReference.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(25);
        }

        Assert.True(weakReference.IsAlive, "Harmony 2.3.6 unexpectedly released its collectible-context roots; update the recorded limitation and decision evidence.");
    }

    private static WeakReference PatchInvokeAndUnload(string fixturePath)
    {
        var context = new AssemblyLoadContext($"harmony-proof-{Guid.NewGuid():N}", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(fixturePath);
        var type = assembly.GetType("HarmonyCollectibleFixture.ReloadTarget", throwOnError: true)!;
        using (var instrumentor = new HarmonyFilteredInstrumentor(Options(type) with { OwnerId = $"proof.collectible.{Guid.NewGuid():N}" }))
        {
            instrumentor.Patch();
            var instance = Activator.CreateInstance(type)!;
            Assert.Equal(7, type.GetMethod("Compute")!.Invoke(instance, [3]));
            instrumentor.Unpatch();
        }

        var weakReference = new WeakReference(context, trackResurrection: false);
        context.Unload();
        return weakReference;
    }

    private static InstrumentationOptions Options(params Type[] types) => new()
    {
        OwnerId = $"proof.tests.{Guid.NewGuid():N}",
        SelectedTypes = types,
        MaxMethods = 128,
        MaxNameLength = 100,
        TrivialIlByteThreshold = 2
    };

    private static MethodInfo Method(string name, params Type[] parameters) =>
        typeof(MethodFixture).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, parameters)!;

    private static void ObserverPrefix()
    {
    }
}
