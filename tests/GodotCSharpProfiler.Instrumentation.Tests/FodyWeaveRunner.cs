using System.Reflection;
using System.Xml.Linq;
using GodotCSharpProfiler.Fody;
using Mono.Cecil;

internal static class FodyWeaveRunner
{
    internal static void Weave(string path, string? configuration = null, string? projectRoot = null)
    {
        projectRoot ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixture"));
        configuration ??= $"<GodotCSharpProfiler MaximumMethods=\"100\" MaximumLabelLength=\"512\" ProjectRoot=\"{Escape(projectRoot)}\"><Rule Action=\"include\" Target=\"namespace\" Pattern=\"InstrumentationFixture\" /></GodotCSharpProfiler>";
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        var pdb = Path.ChangeExtension(path, ".pdb");
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadSymbols = File.Exists(pdb), InMemory = true, AssemblyResolver = resolver });
        var weaver = new ModuleWeaver();
        Set(weaver, "ModuleDefinition", module);
        Set(weaver, "ProjectDirectoryPath", projectRoot);
        Set(weaver, "Config", XElement.Parse(configuration, LoadOptions.PreserveWhitespace));
        weaver.Execute();
        module.Write(path, new WriterParameters { WriteSymbols = module.HasSymbols });
    }

    private static string Escape(string value) => new XAttribute("x", value).ToString().Split('"')[1];

    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().BaseType!.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        property.SetValue(target, value);
    }
}
