using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GodotCSharpProfiler.Fody;

public sealed class ModuleWeaver : BaseModuleWeaver
{
    private const string MarkerName = "GodotCSharpProfiler.Fody.GodotCSharpProfilerInstrumentedAttribute";
    private const string RecorderName = "Apeworks.GodotCSharpProfiler.Instrumentation.InstrumentationRecorder";
    private const string TokenName = RecorderName + "/Token";
    private static readonly string[] SafeNamespacePrefixes = { "Apeworks.GodotCSharpProfiler", "GodotCSharpProfiler.Fody", "Fody", "Mono.Cecil" };

    public override void Execute()
    {
        var options = InstrumentationOptions.Parse(Config, ProjectDirectoryPath);
        var hash = ConfigurationHash(options);
        if (options.EmbeddedConfigHash is not null && options.EmbeddedConfigHash != hash)
            throw new WeavingException($"GodotCSharpProfiler embedded ConfigHash '{options.EmbeddedConfigHash}' does not match normalized configuration '{hash}'. Regenerate FodyWeavers.xml.");
        var marker = ModuleDefinition.Assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == MarkerName);
        if (marker is not null)
        {
            var oldHash = marker.ConstructorArguments.Count == 1 ? marker.ConstructorArguments[0].Value as string : null;
            throw new WeavingException($"GodotCSharpProfiler automatic instrumentation refused to weave '{ModuleDefinition.Assembly.Name.Name}' twice (existing config {oldHash ?? "unknown"}, requested {hash}). Clean the build output and rebuild.");
        }

        if (!TryFindRecorder(out var enter, out var exit, out var tokenType))
        {
            WriteWarning($"GodotCSharpProfiler automatic instrumentation is a no-op for '{ModuleDefinition.Assembly.Name.Name}': recorder hook {RecorderName}.Enter(int)/Exit(Token) is missing. Include Runtime/Instrumentation/InstrumentationRecorder.cs or remove the weaver package.");
            return;
        }

        var classified = AllMethods(ModuleDefinition.Types).Select(m => Classify(m, options)).OrderBy(x => x.Label, StringComparer.Ordinal).ToArray();
        var selected = classified.Where(x => x.Eligible).ToArray();
        if (selected.Length > options.MaximumMethods)
            throw new WeavingException($"GodotCSharpProfiler selected {selected.Length} methods, exceeding the configured/runtime bound of {options.MaximumMethods}. Tighten ordered include/exclude rules; instrumentation never truncates silently.");
        var longLabel = selected.FirstOrDefault(x => Encoding.UTF8.GetByteCount(x.Label) > options.MaximumLabelLength);
        if (longLabel is not null)
            throw new WeavingException($"GodotCSharpProfiler canonical label exceeds MaximumLabelLength={options.MaximumLabelLength}: '{longLabel.Label}'. Tighten filters or increase the bounded label limit.");

        for (var id = 0; id < selected.Length; id++) Instrument(selected[id].Method, id, enter!, exit!, tokenType!);
        AddManifest(hash, selected.Select(item => item.Label).ToArray(), classified.Length - selected.Length);
        AddMarker(hash);
        WriteInfo($"GodotCSharpProfiler instrumented {selected.Length} methods and skipped {classified.Length - selected.Length} (config {hash}).");
    }

    public override IEnumerable<string> GetAssembliesForScanning() => new[] { "mscorlib", "System", "System.Runtime", "netstandard" };

    private bool TryFindRecorder(out MethodReference? enter, out MethodReference? exit, out TypeReference? token)
    {
        enter = exit = null; token = null;
        var recorder = AllTypes(ModuleDefinition.Types).FirstOrDefault(t => t.FullName == RecorderName);
        if (recorder is null) return false;
        var enterDefinition = recorder.Methods.FirstOrDefault(m => m.Name == "Enter" && m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 && m.ReturnType.FullName == TokenName);
        var exitDefinition = recorder.Methods.FirstOrDefault(m => m.Name == "Exit" && m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == TokenName && m.ReturnType.MetadataType == MetadataType.Void);
        if (enterDefinition is null || exitDefinition is null) return false;
        enter = ModuleDefinition.ImportReference(enterDefinition);
        exit = ModuleDefinition.ImportReference(exitDefinition);
        token = ModuleDefinition.ImportReference(enterDefinition.ReturnType);
        return true;
    }

    private Classification Classify(MethodDefinition method, InstrumentationOptions options)
    {
        var label = CanonicalLabel(method);
        string? reason = null;
        if (method.IsConstructor) reason = "constructor";
        else if (method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn) reason = "accessor";
        else if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl || method.IsInternalCall) reason = "bodyless/native/abstract";
        else if (method.ReturnType is ByReferenceType) reason = "byref return";
        else if (method.CallingConvention == MethodCallingConvention.VarArg) reason = "vararg";
        else if (Generated(method)) reason = "generated";
        else if (HasAttribute(method, "System.Runtime.CompilerServices.AsyncStateMachineAttribute")) reason = "async";
        else if (HasAttribute(method, "System.Runtime.CompilerServices.IteratorStateMachineAttribute")) reason = "iterator";
        else if (SafeNamespacePrefixes.Any(p => method.DeclaringType.Namespace == p || method.DeclaringType.Namespace.StartsWith(p + ".", StringComparison.Ordinal))) reason = "protected namespace";
        else if (Unsupported(method)) reason = "unsupported IL";
        else if (Trivial(method)) reason = "trivial";
        else if (!SourceAllowed(method, options.ProjectRoot)) reason = "missing or non-project source";

        var included = reason is null;
        if (included)
        {
            foreach (var rule in options.Rules)
            {
                if (rule.Matches("namespace", method.DeclaringType.Namespace) || rule.Matches("type", method.DeclaringType.FullName.Replace('/', '+')) || rule.Matches("method", label))
                    included = rule.Include;
            }
        }
        return new Classification(method, label, included, included ? "eligible" : reason ?? "excluded by rule");
    }

    private static bool SourceAllowed(MethodDefinition method, string root)
    {
        if (root.Length == 0) return false;
        var sequencePoints = method.DebugInformation.SequencePoints.Where(point => !point.IsHidden).ToArray();
        if (sequencePoints.Length == 0) return false;
        var documents = sequencePoints.Select(point => point.Document?.Url).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        if (documents.Length == 0) return false;
        foreach (var raw in documents)
        {
            string path;
            try { path = InstrumentationOptions.CanonicalPath(raw!); }
            catch (WeavingException) { return false; }
            if (path.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(path == root || path.StartsWith(root + "/", StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private static bool Generated(MethodDefinition method) => HasAttribute(method, "System.Runtime.CompilerServices.CompilerGeneratedAttribute") || HasAttribute(method.DeclaringType, "System.Runtime.CompilerServices.CompilerGeneratedAttribute") || method.DeclaringType.Name.StartsWith("<", StringComparison.Ordinal);
    private static bool HasAttribute(ICustomAttributeProvider provider, string name) => provider.HasCustomAttributes && provider.CustomAttributes.Any(a => a.AttributeType.FullName == name);
    private static bool Trivial(MethodDefinition method) => method.Body.Instructions.Count(i => i.OpCode.Code != Code.Nop) <= 2;
    private static bool Unsupported(MethodDefinition method) => method.Body.Instructions.Any(i => i.OpCode == OpCodes.Calli || i.OpCode == OpCodes.Jmp || i.OpCode == OpCodes.Localloc || i.OpCode == OpCodes.Cpblk || i.OpCode == OpCodes.Initblk || i.OpCode == OpCodes.Arglist || i.OpCode == OpCodes.Mkrefany || i.OpCode == OpCodes.Refanyval || i.OpCode == OpCodes.Refanytype || i.OpCode == OpCodes.Tail);

    private static string CanonicalLabel(MethodDefinition method)
    {
        static string TypeName(TypeReference type) => type switch
        {
            GenericParameter parameter => "!" + parameter.Position,
            ByReferenceType byRef => TypeName(byRef.ElementType) + "&",
            PointerType pointer => TypeName(pointer.ElementType) + "*",
            ArrayType array => TypeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
            GenericInstanceType generic => generic.ElementType.FullName + "<" + string.Join(",", generic.GenericArguments.Select(TypeName)) + ">",
            _ => type.FullName
        };
        return method.DeclaringType.FullName.Replace('/', '+') + "::" + method.Name + (method.HasGenericParameters ? "`" + method.GenericParameters.Count : "") + "(" + string.Join(",", method.Parameters.Select(p => TypeName(p.ParameterType))) + "):" + TypeName(method.ReturnType);
    }

                private static void Instrument(MethodDefinition method, int id, MethodReference enter, MethodReference exit, TypeReference tokenType)
    {
        var body = method.Body;
        // Added probes and finally blocks can push short branch targets out of range.
        foreach (var instruction in body.Instructions)
        {
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => instruction.OpCode
            };
        }
        var il = body.GetILProcessor();
        var originalFirst = body.Instructions[0];
        var token = new VariableDefinition(tokenType);
        body.Variables.Add(token);
        body.InitLocals = true;
        il.InsertBefore(originalFirst, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Call, enter));
        il.InsertBefore(originalFirst, il.Create(OpCodes.Stloc, token));
        var returns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void) { result = new VariableDefinition(method.ReturnType); body.Variables.Add(result); }
        var finallyStart = il.Create(OpCodes.Ldloc, token);
        var after = il.Create(OpCodes.Nop);
        foreach (var ret in returns)
        {
            // Mutate the original ret so existing branches and handler boundaries still target the
            // stack-consuming instruction, then append the stack-empty leave.
            if (result is null) { ret.OpCode = OpCodes.Leave; ret.Operand = after; }
            else
            {
                ret.OpCode = OpCodes.Stloc;
                ret.Operand = result;
                il.InsertAfter(ret, il.Create(OpCodes.Leave, after));
            }
        }
        il.Append(finallyStart);
        il.Append(il.Create(OpCodes.Call, exit));
        il.Append(il.Create(OpCodes.Endfinally));
        il.Append(after);
        if (result is not null) il.Append(il.Create(OpCodes.Ldloc, result));
        il.Append(il.Create(OpCodes.Ret));
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = originalFirst, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
    }

    private void AddManifest(string hash, IReadOnlyList<string> labels, int skipped)
    {
        var manifest = new TypeDefinition("Apeworks.GodotCSharpProfiler.Instrumentation", "GodotCSharpProfilerInstrumentationManifest", TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public, ModuleDefinition.TypeSystem.Object);
        AddConstant("ConfigHash", hash, ModuleDefinition.TypeSystem.String);
        AddConstant("InstrumentedCount", labels.Count, ModuleDefinition.TypeSystem.Int32);
        AddConstant("SkippedCount", skipped, ModuleDefinition.TypeSystem.Int32);

        var getLabel = new MethodDefinition("GetLabel", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, ModuleDefinition.TypeSystem.String);
        getLabel.Parameters.Add(new ParameterDefinition("methodId", ParameterAttributes.None, ModuleDefinition.TypeSystem.Int32));
        var il = getLabel.Body.GetILProcessor();
        var targets = labels.Select(_ => il.Create(OpCodes.Nop)).ToArray();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Switch, targets);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        for (var id = 0; id < labels.Count; id++)
        {
            il.Append(targets[id]);
            il.Emit(OpCodes.Ldstr, labels[id]);
            il.Emit(OpCodes.Ret);
        }
        manifest.Methods.Add(getLabel);
        ModuleDefinition.Types.Add(manifest);

        void AddConstant(string name, object value, TypeReference type)
        {
            manifest.Fields.Add(new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, type) { Constant = value });
        }
    }

    private void AddMarker(string hash)
    {
        var attribute = new TypeDefinition("GodotCSharpProfiler.Fody", "GodotCSharpProfilerInstrumentedAttribute", TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.NotPublic, ModuleDefinition.ImportReference(typeof(Attribute)));
        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, ModuleDefinition.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("configHash", ParameterAttributes.None, ModuleDefinition.TypeSystem.String));
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, ModuleDefinition.ImportReference(typeof(Attribute).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null)!));
        il.Emit(OpCodes.Ret);
        attribute.Methods.Add(ctor);
        ModuleDefinition.Types.Add(attribute);
        var custom = new CustomAttribute(ctor);
        custom.ConstructorArguments.Add(new CustomAttributeArgument(ModuleDefinition.TypeSystem.String, hash));
        ModuleDefinition.Assembly.CustomAttributes.Add(custom);
    }

    private static string ConfigurationHash(InstrumentationOptions options)
    {
        var canonical = options.ProjectRoot + "\n" + options.MaximumMethods + "\n" + options.MaximumLabelLength + "\n" + string.Join("\n", options.Rules.OrderBy(r => r.Order).Select(r => $"{r.Order}:{r.Include}:{r.Target}:{r.Pattern}"));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots) => roots.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
    private static IEnumerable<MethodDefinition> AllMethods(IEnumerable<TypeDefinition> roots) => AllTypes(roots).SelectMany(t => t.Methods);
    private sealed class Classification
    {
        internal Classification(MethodDefinition method, string label, bool eligible, string reason) { Method = method; Label = label; Eligible = eligible; Reason = reason; }
        internal MethodDefinition Method { get; }
        internal string Label { get; }
        internal bool Eligible { get; }
        internal string Reason { get; }
    }
}
