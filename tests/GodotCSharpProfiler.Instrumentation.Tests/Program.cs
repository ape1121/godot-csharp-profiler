using System.Reflection;
using System.Runtime.Loader;
using Apeworks.GodotCSharpProfiler.Instrumentation;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

for (var i = 0; i < 1000; i++) { var token = InstrumentationRecorder.Enter(0); InstrumentationRecorder.Exit(token); }
var allocationStart = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 100_000; i++) { var token = InstrumentationRecorder.Enter(0); InstrumentationRecorder.Exit(token); }
Assert(GC.GetAllocatedBytesForCurrentThread() == allocationStart, "inactive recorder allocated");
var generation1 = InstrumentationRecorder.StartCapture();
var outer = InstrumentationRecorder.Enter(1);
var inner = InstrumentationRecorder.Enter(2);
InstrumentationRecorder.Exit(inner);
InstrumentationRecorder.Exit(outer);
var first = InstrumentationRecorder.StopCapture();
Assert(first.Generation == generation1 && first.Samples.Sum(x => x.Calls) == 2, "exact runtime counts");
var stale = InstrumentationRecorder.Enter(3);
var generation2 = InstrumentationRecorder.StartCapture();
InstrumentationRecorder.Exit(stale);
var second = InstrumentationRecorder.StopCapture();
Assert(generation2 != generation1 && second.Samples.Count == 0, "capture generation isolation");
Assert(first.Samples is not List<InstrumentationRecorder.Sample>, "snapshot collection is mutable");

var source = Path.Combine(AppContext.BaseDirectory, "fixture", "Fixture.dll");
var temp = Path.Combine(Path.GetTempPath(), "instrumentation-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var woven = Path.Combine(temp, "Fixture.dll");
    File.Copy(source, woven);
    File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(woven, ".pdb"));
    FodyWeaveRunner.Weave(woven);
    using (var symbols = ModuleDefinition.ReadModule(woven, new ReaderParameters { ReadSymbols = true }))
    {
        Assert(symbols.HasSymbols, "portable PDB unreadable");
        Assert(symbols.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName.Contains("GodotCSharpProfilerInstrumentedAttribute")), "marker missing");
        var methods = symbols.Types.SelectMany(t => t.Methods).ToArray();
        Assert(!methods.Single(m => m.Name == "Async").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "async instrumented");
        Assert(!methods.Single(m => m.Name == "Iterator").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "iterator instrumented");
        Assert(!methods.Single(m => m.Name == "Trivial").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "trivial instrumented");
    }
    var doubleRejected = false;
    try { FodyWeaveRunner.Weave(woven); } catch (WeavingException e) when (e.Message.Contains("twice", StringComparison.Ordinal)) { doubleRejected = true; }
    Assert(doubleRejected, "double weave not fail-closed");

    var context = new AssemblyLoadContext("woven", true);
    context.Resolving += (_, name) => name.Name == typeof(InstrumentationRecorder).Assembly.GetName().Name ? typeof(InstrumentationRecorder).Assembly : null;
    var assembly = context.LoadFromAssemblyPath(woven);
    var type = assembly.GetType("InstrumentationFixture.Fixture`1")!.MakeGenericType(typeof(string));
    var instance = Activator.CreateInstance(type)!;
    var wovenRecorder = assembly.GetType("Apeworks.GodotCSharpProfiler.Instrumentation.InstrumentationRecorder")!;
    wovenRecorder.GetMethod("StartCapture")!.Invoke(null, null);
    object? Call(string name, params object[] args) => type.GetMethod(name)!.Invoke(instance, args);
    Assert((int)Call("MultiReturn", -1)! == -1 && (int)Call("MultiReturn", 2)! == 3, "return regression");
    Assert((int)Call("Recursive", 4)! == 4, "recursion regression");
    Assert((string)type.GetMethod("Generic")!.MakeGenericMethod(typeof(string)).Invoke(instance, new object[] { "ok" })! == "ok", "generic regression");
    Assert((int)Call("NestedHandlers", 0)! == 0, "nested handler regression");
    try { Call("Throwing"); } catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
    var capturedObject = wovenRecorder.GetMethod("StopCapture")!.Invoke(null, null)!;
    var capturedType = capturedObject.GetType();
    var wovenCalls = ((System.Collections.IEnumerable)capturedType.GetProperty("Samples")!.GetValue(capturedObject)!).Cast<object>().Sum(sample => (long)sample.GetType().GetProperty("Calls")!.GetValue(sample)!);
    var wovenForced = (long)capturedType.GetProperty("ForcedClosed")!.GetValue(capturedObject)!;
    Assert(wovenCalls == 10, $"woven Enter/Exit imbalance: {wovenCalls}");
    Assert(wovenForced == 0, "woven exits forced closed");
    context.Unload();

    var benchmark = BenchmarkFixture.Measure();
    Assert(benchmark.inactiveBytes == 0, "benchmark inactive allocation");
    Console.WriteLine("PASS: instrumentation weaver/runtime regression suite");
    Console.WriteLine($"CapturedCalls={wovenCalls}; forced={wovenForced}");
    Console.WriteLine($"Benchmark inactive={benchmark.inactiveNs:F1} ns/call enabled={benchmark.enabledNs:F1} ns/call allocations={benchmark.inactiveBytes} B");
}
finally { Directory.Delete(temp, true); }
