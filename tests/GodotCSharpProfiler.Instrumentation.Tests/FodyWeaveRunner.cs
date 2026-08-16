using System.Reflection;
using Fody;
using GodotCSharpProfiler.Fody;
using Mono.Cecil;

internal static class FodyWeaveRunner
{
    internal static void Weave(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        var pdb = Path.ChangeExtension(path, ".pdb");
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadSymbols = File.Exists(pdb), InMemory = true, AssemblyResolver = resolver });
        var weaver = new ModuleWeaver();
        Set(weaver, "ModuleDefinition", module);
        Set(weaver, "ProjectDirectoryPath", Path.GetDirectoryName(path)!);
        Set(weaver, "Config", System.Xml.Linq.XElement.Parse("<GodotCSharpProfiler MaximumMethods=\"100\"><Rule Action=\"include\" Target=\"namespace\" Pattern=\"InstrumentationFixture\" /></GodotCSharpProfiler>"));
        weaver.Execute();
        module.Write(path, new WriterParameters { WriteSymbols = module.HasSymbols });
    }

    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().BaseType!.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        property.SetValue(target, value);
    }
}
