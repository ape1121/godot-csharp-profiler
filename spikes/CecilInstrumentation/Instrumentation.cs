using System.Diagnostics;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GodotCSharpProfiler.CecilInstrumentation;

public static class Probe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Enters = new();
    private static readonly Dictionary<string, int> Exits = new();
    private static int _active;

    public static string Enter(string id)
    {
        lock (Gate)
        {
            Enters[id] = Enters.GetValueOrDefault(id) + 1;
            _active++;
        }
        return id;
    }

    public static void Exit(string token)
    {
        lock (Gate)
        {
            Exits[token] = Exits.GetValueOrDefault(token) + 1;
            _active--;
        }
    }

    public static (int Enters, int Exits) Counts(string id)
    {
        lock (Gate) return (Enters.GetValueOrDefault(id), Exits.GetValueOrDefault(id));
    }

    public static int Active { get { lock (Gate) return _active; } }

    public static void Reset()
    {
        lock (Gate)
        {
            Enters.Clear();
            Exits.Clear();
            _active = 0;
        }
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CecilInstrumentedAttribute : Attribute;

public sealed record MethodClassification(string Id, bool Eligible, string Reason);
public sealed record WeaveResult(IReadOnlyList<MethodClassification> Methods, TimeSpan Elapsed, long SizeDelta);

public static class CecilWeaver
{
    public static IReadOnlyList<MethodClassification> Classify(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        return assembly.MainModule.Types.SelectMany(AllMethods)
            .Where(m => m.DeclaringType.Name == "Fixture")
            .Select(Classify).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    public static WeaveResult WeaveCopy(string sourceAssembly, string outputAssembly)
    {
        if (Path.GetFullPath(sourceAssembly) == Path.GetFullPath(outputAssembly))
            throw new InvalidOperationException("Source assembly must never be modified.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputAssembly)!);
        File.Copy(sourceAssembly, outputAssembly, true);
        var sourcePdb = Path.ChangeExtension(sourceAssembly, ".pdb");
        var outputPdb = Path.ChangeExtension(outputAssembly, ".pdb");
        if (File.Exists(sourcePdb)) File.Copy(sourcePdb, outputPdb, true);

        var before = new FileInfo(outputAssembly).Length;
        var stopwatch = Stopwatch.StartNew();
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(sourceAssembly)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(Probe).Assembly.Location)!);
        var reader = new ReaderParameters { ReadSymbols = File.Exists(outputPdb), AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
        using var assembly = AssemblyDefinition.ReadAssembly(outputAssembly, reader);
        if (assembly.CustomAttributes.Any(a => a.AttributeType.FullName == typeof(CecilInstrumentedAttribute).FullName))
            throw new InvalidOperationException("Assembly is already instrumented.");

        var module = assembly.MainModule;
        var enter = module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.Enter))!);
        var exit = module.ImportReference(typeof(Probe).GetMethod(nameof(Probe.Exit))!);
        var classifications = new List<MethodClassification>();
        foreach (var method in module.Types.SelectMany(AllMethods).Where(m => m.DeclaringType.Name == "Fixture"))
        {
            var classification = Classify(method);
            classifications.Add(classification);
            if (classification.Eligible) Instrument(method, classification.Id, enter, exit);
        }

        var markerCtor = module.ImportReference(typeof(CecilInstrumentedAttribute).GetConstructor(Type.EmptyTypes)!);
        assembly.CustomAttributes.Add(new CustomAttribute(markerCtor));
        assembly.Write(outputAssembly, new WriterParameters { WriteSymbols = reader.ReadSymbols });
        stopwatch.Stop();
        return new(classifications.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(), stopwatch.Elapsed,
            new FileInfo(outputAssembly).Length - before);
    }

    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
        type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

    private static MethodClassification Classify(MethodDefinition method)
    {
        var id = method.FullName;
        if (method.IsConstructor) return new(id, false, "constructor");
        if (method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn) return new(id, false, "accessor");
        if (HasAttribute(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute")) return new(id, false, "async");
        if (HasAttribute(method, "System.Runtime.CompilerServices.IteratorStateMachineAttribute")) return new(id, false, "iterator");
        if (!method.HasBody || method.IsAbstract) return new(id, false, "no body");
        return new(id, true, "eligible");
    }

    private static bool HasAttribute(MethodDefinition method, string fullName) =>
        method.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

    private static void Instrument(MethodDefinition method, string id, MethodReference enter, MethodReference exit)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        var token = new VariableDefinition(method.Module.TypeSystem.String);
        method.Body.Variables.Add(token);
        method.Body.InitLocals = true;
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, id));
        il.InsertBefore(first, il.Create(OpCodes.Call, enter));
        il.InsertBefore(first, il.Create(OpCodes.Stloc, token));

        var returns = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            result = new VariableDefinition(method.ReturnType);
            method.Body.Variables.Add(result);
        }
        var finallyStart = il.Create(OpCodes.Ldloc, token);
        foreach (var ret in returns)
        {
            if (result is not null) il.InsertBefore(ret, il.Create(OpCodes.Stloc, result));
            ret.OpCode = OpCodes.Leave;
        }
        var after = il.Create(OpCodes.Nop);
        foreach (var ret in returns) ret.Operand = after;
        il.Append(finallyStart);
        il.Append(il.Create(OpCodes.Call, exit));
        il.Append(il.Create(OpCodes.Endfinally));
        il.Append(after);
        if (result is not null) il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ret));
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = first,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = after
        });
    }
}
