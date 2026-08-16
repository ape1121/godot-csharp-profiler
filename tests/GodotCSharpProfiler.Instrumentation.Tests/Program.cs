using System.Reflection;
using System.Runtime.Loader;
using System.Xml;
using System.Xml.Linq;
using Apeworks.GodotCSharpProfiler.Instrumentation;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static void AssertInvalidConfig(string source, string xml)
{
    var directory = Path.Combine(Path.GetTempPath(), "invalid-weaver-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var assembly = Path.Combine(directory, "Fixture.dll");
        File.Copy(source, assembly);
        File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(assembly, ".pdb"));
        var rejected = false;
        try { FodyWeaveRunner.Weave(assembly, xml); }
        catch (Exception exception) when (exception is WeavingException || exception is XmlException) { rejected = true; }
        Assert(rejected, $"invalid config accepted: {xml}");
    }
    finally { Directory.Delete(directory, true); }
}

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
var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixture"));
var temp = Path.Combine(Path.GetTempPath(), "instrumentation-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var woven = Path.Combine(temp, "Fixture.dll");
    File.Copy(source, woven);
    File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(woven, ".pdb"));
    FodyWeaveRunner.Weave(woven);
    string expectedHash;
    string[] expectedLabels;
    int expectedSkipped;
    using (var symbols = ModuleDefinition.ReadModule(woven, new ReaderParameters { ReadSymbols = true }))
    {
        Assert(symbols.HasSymbols, "portable PDB unreadable");
        Assert(symbols.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName.Contains("GodotCSharpProfilerInstrumentedAttribute")), "marker missing");
        var methods = symbols.Types.SelectMany(t => t.Methods).ToArray();
        Assert(!methods.Single(m => m.Name == "Async").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "async instrumented");
        Assert(!methods.Single(m => m.Name == "Iterator").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "iterator instrumented");
        Assert(!methods.Single(m => m.Name == "Trivial").Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "trivial instrumented");
        Assert(!methods.Single(m => m.Name == ".ctor" && m.DeclaringType.Name.StartsWith("Fixture", StringComparison.Ordinal)).Body.Instructions.Any(i => i.OpCode == OpCodes.Endfinally), "constructor instrumented");
        var manifest = symbols.Types.Single(t => t.Name == "GodotCSharpProfilerInstrumentationManifest");
        expectedHash = (string)manifest.Fields.Single(f => f.Name == "ConfigHash").Constant!;
        var count = (int)manifest.Fields.Single(f => f.Name == "InstrumentedCount").Constant!;
        expectedSkipped = (int)manifest.Fields.Single(f => f.Name == "SkippedCount").Constant!;
        expectedLabels = manifest.Methods.Single(m => m.Name == "GetLabel").Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToArray();
        Assert(expectedLabels.Length == count && count <= InstrumentationRecorder.MaximumMethods, "manifest count is not bounded");
        Assert(expectedLabels.SequenceEqual(expectedLabels.OrderBy(label => label, StringComparer.Ordinal)), "manifest IDs are not deterministic");
        Assert(expectedLabels.All(label => System.Text.Encoding.UTF8.GetByteCount(label) <= InstrumentationRecorder.MaximumLabelLength), "manifest label exceeds runtime cap");
        var enterCalls = methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference called && called.Name == "Enter").ToArray();
        Assert(enterCalls.Length >= expectedLabels.Length && enterCalls.All(call => call.Previous.OpCode != OpCodes.Ldstr), "per-call label strings were emitted");
    }
    var doubleRejected = false;
    try { FodyWeaveRunner.Weave(woven); } catch (WeavingException e) when (e.Message.Contains("twice", StringComparison.Ordinal)) { doubleRejected = true; }
    Assert(doubleRejected, "double weave not fail-closed");

    var context = new AssemblyLoadContext("woven", true);
    context.Resolving += (_, name) => name.Name == typeof(InstrumentationRecorder).Assembly.GetName().Name ? typeof(InstrumentationRecorder).Assembly : null;
    var assembly = context.LoadFromAssemblyPath(woven);
    Assert(InstrumentationManifest.TryRead(assembly, out var discovered) && discovered is not null, "runtime manifest discovery failed after isolated load");
    var manifestApi = discovered!;
    Assert(manifestApi.ConfigHash == expectedHash && manifestApi.SkippedCount == expectedSkipped, "runtime manifest metadata differs from assembly");
    Assert(manifestApi.Labels.SequenceEqual(expectedLabels), "runtime manifest IDs do not exactly match assembly labels");
    Assert(manifestApi.ResolveLabel(-1) is null && manifestApi.ResolveLabel(manifestApi.InstrumentedCount) is null, "manifest accepted out-of-range ID");

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
    var samples = ((System.Collections.IEnumerable)capturedType.GetProperty("Samples")!.GetValue(capturedObject)!).Cast<object>().ToArray();
    var wovenCalls = samples.Sum(sample => (long)sample.GetType().GetProperty("Calls")!.GetValue(sample)!);
    var wovenForced = (long)capturedType.GetProperty("ForcedClosed")!.GetValue(capturedObject)!;
    Assert(wovenCalls == 10, $"woven Enter/Exit imbalance: {wovenCalls}");
    Assert(wovenForced == 0, "woven exits forced closed");
    Assert(samples.All(sample => manifestApi.ResolveLabel((int)sample.GetType().GetProperty("MethodId")!.GetValue(sample)!) is not null), "snapshot ID did not resolve exactly through manifest");
    context.Unload();

    // A malicious catch-all include cannot override missing sequence points or source outside ProjectRoot.
    var excluded = Path.Combine(temp, "Excluded.dll");
    File.Copy(source, excluded);
    File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(excluded, ".pdb"));
    var malicious = $"<GodotCSharpProfiler ProjectRoot=\"{new XAttribute("x", temp).ToString().Split('\"')[1]}\"><Rule Action=\"include\" Target=\"all\" Pattern=\"**\" /></GodotCSharpProfiler>";
    FodyWeaveRunner.Weave(excluded, malicious, temp);
    using (var module = ModuleDefinition.ReadModule(excluded))
    {
        // Recorder internals are protected separately; the generated manifest is the selection authority.
        var generatedManifest = module.Types.Single(t => t.Name == "GodotCSharpProfilerInstrumentationManifest");
        Assert((int)generatedManifest.Fields.Single(f => f.Name == "InstrumentedCount").Constant! == 0, "hard exclusions were overrideable");
    }

    var noPoints = Path.Combine(temp, "NoPoints.dll");
    File.Copy(source, noPoints);
    File.Copy(Path.ChangeExtension(source, ".pdb"), Path.ChangeExtension(noPoints, ".pdb"));
    using (var module = ModuleDefinition.ReadModule(noPoints, new ReaderParameters { ReadSymbols = true, InMemory = true }))
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods)) method.DebugInformation.SequencePoints.Clear();
        module.Write(noPoints, new WriterParameters { WriteSymbols = true });
    }
    FodyWeaveRunner.Weave(noPoints, null, fixtureRoot);
    using (var module = ModuleDefinition.ReadModule(noPoints))
        Assert((int)module.Types.Single(t => t.Name == "GodotCSharpProfilerInstrumentationManifest").Fields.Single(f => f.Name == "InstrumentedCount").Constant! == 0, "missing sequence points were overrideable");

    AssertInvalidConfig(source, "<GodotCSharpProfiler MaximumMethods=\"0\" />");
    AssertInvalidConfig(source, "<GodotCSharpProfiler MaximumMethods=\"16385\" />");
    AssertInvalidConfig(source, "<GodotCSharpProfiler MaximumLabelLength=\"513\" />");
    AssertInvalidConfig(source, "<GodotCSharpProfiler><Rule Action=\"allow\" Target=\"all\" Pattern=\"**\" /></GodotCSharpProfiler>");
    AssertInvalidConfig(source, "<GodotCSharpProfiler><Rule Action=\"include\" Target=\"assembly\" Pattern=\"**\" /></GodotCSharpProfiler>");
    AssertInvalidConfig(source, "<GodotCSharpProfiler><Rule Action=\"include\" Target=\"all\" Pattern=\"***\" /></GodotCSharpProfiler>");
    AssertInvalidConfig(source, "<GodotCSharpProfiler><Rule Action=\"include\" Target=\"all\" Pattern=\"**\" Order=\"1\" /></GodotCSharpProfiler>");
    AssertInvalidConfig(source, "<GodotCSharpProfiler><MaximumMethods>10</MaximumMethods></GodotCSharpProfiler>");

    var benchmark = BenchmarkFixture.Measure();
    Assert(benchmark.inactiveBytes == 0, "benchmark inactive allocation");
    Console.WriteLine("PASS: instrumentation weaver/runtime regression suite");
    Console.WriteLine($"Manifest instrumented={expectedLabels.Length}; skipped={expectedSkipped}; config={expectedHash}");
    Console.WriteLine($"CapturedCalls={wovenCalls}; forced={wovenForced}");
    Console.WriteLine($"Benchmark inactive={benchmark.inactiveNs:F1} ns/call enabled={benchmark.enabledNs:F1} ns/call allocations={benchmark.inactiveBytes} B");
}
finally { Directory.Delete(temp, true); }
