using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using GodotCSharpProfiler.CecilInstrumentation;
using Mono.Cecil;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var source = Path.Combine(AppContext.BaseDirectory, "fixture", "Fixture.dll");
var sourcePdb = Path.ChangeExtension(source, ".pdb");
var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
var classifications = CecilWeaver.Classify(source);

string Reason(string method) => classifications.Single(c => c.Id.Contains("::" + method + "(", StringComparison.Ordinal)).Reason;
Assert(Reason("Ordinary") == "eligible", "ordinary classification");
Assert(Reason("Recursive") == "eligible", "recursive classification");
Assert(classifications.Where(c => c.Id.Contains("Overloaded")).All(c => c.Eligible), "overload classification");
Assert(Reason("Generic") == "eligible", "generic classification");
Assert(Reason("Throwing") == "eligible", "throwing classification");
Assert(Reason("Async") == "async", "async classification");
Assert(Reason("Iterator") == "iterator", "iterator classification");
Assert(classifications.Where(c => c.Id.Contains("Property")).All(c => c.Reason == "accessor"), "accessor classification");
Assert(classifications.Single(c => c.Id.Contains("::.ctor(")).Reason == "constructor", "constructor classification");

var temp = Path.Combine(Path.GetTempPath(), "cecil-proof-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var woven = Path.Combine(temp, "Fixture.dll");
    var result = CecilWeaver.WeaveCopy(source, woven);
    Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) == originalHash, "source DLL changed");
    Assert(File.Exists(Path.ChangeExtension(woven, ".pdb")), "PDB not copied");
    using (var pdbCheck = AssemblyDefinition.ReadAssembly(woven, new ReaderParameters { ReadSymbols = true }))
        Assert(pdbCheck.MainModule.HasSymbols, "PDB unreadable");

    var doubleWeaveRejected = false;
    try { CecilWeaver.WeaveCopy(woven, Path.Combine(temp, "twice", "Fixture.dll")); }
    catch (InvalidOperationException e) when (e.Message.Contains("already instrumented")) { doubleWeaveRejected = true; }
    Assert(doubleWeaveRejected, "double weave was not rejected");

    Probe.Reset();
    var context = new AssemblyLoadContext("cecil-proof", isCollectible: true);
    context.Resolving += (_, name) => name.Name == typeof(Probe).Assembly.GetName().Name ? typeof(Probe).Assembly : null;
    var assembly = context.LoadFromAssemblyPath(woven);
    var type = assembly.GetType("CecilFixture.Fixture")!;
    var fixture = Activator.CreateInstance(type)!;
    object? Call(string name, Type[] types, params object[] args) => type.GetMethod(name, types)!.Invoke(fixture, args);
    Call("Ordinary", [typeof(int)], 4);
    Call("Recursive", [typeof(int)], 3);
    Call("Overloaded", [typeof(string)], "a");
    Call("Overloaded", [typeof(int)], 2);
    type.GetMethod("Generic")!.MakeGenericMethod(typeof(string)).Invoke(fixture, ["g"]);
    try { Call("Throwing", Type.EmptyTypes); } catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }

    var eligible = result.Methods.Where(c => c.Eligible).ToArray();
    foreach (var method in eligible)
    {
        var expected = method.Id.Contains("Recursive") ? 4 : 1;
        Assert(Probe.Counts(method.Id) == (expected, expected), $"counts for {method.Id}: {Probe.Counts(method.Id)}");
    }
    Assert(Probe.Active == 0, "throwing cleanup left an active probe");
    Assert(result.Methods.Where(c => !c.Eligible).All(c => Probe.Counts(c.Id) == (0, 0)), "skipped method instrumented");
    context.Unload();

    Console.WriteLine($"PASS: 1 executable Cecil instrumentation proof");
    Console.WriteLine($"Eligible={eligible.Length}, skipped={result.Methods.Count - eligible.Length}, elapsed={result.Elapsed.TotalMilliseconds:F3} ms, sizeDelta={result.SizeDelta:+#;-#;0} bytes");
    Console.WriteLine($"SourceHash={originalHash}; PDB=readable; double-weave=rejected; active={Probe.Active}");
}
finally
{
    Directory.Delete(temp, true);
}
