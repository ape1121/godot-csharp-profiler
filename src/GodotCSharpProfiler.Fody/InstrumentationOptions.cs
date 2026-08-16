using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GodotCSharpProfiler.Fody;

internal sealed class InstrumentationOptions
{
    internal const int DefaultMaximumMethods = 16384;
    internal const int DefaultMaximumLabelLength = 512;
    internal int MaximumMethods { get; private set; } = DefaultMaximumMethods;
    internal int MaximumLabelLength { get; private set; } = DefaultMaximumLabelLength;
    internal string ProjectRoot { get; private set; } = string.Empty;
    internal IReadOnlyList<Rule> Rules { get; private set; } = Array.Empty<Rule>();

    internal static InstrumentationOptions Parse(XElement? config, string? projectDirectory)
    {
        var result = new InstrumentationOptions { ProjectRoot = CanonicalPath(projectDirectory ?? string.Empty) };
        if (config is null) return result;
        result.MaximumMethods = Positive(config.Attribute("MaximumMethods")?.Value, DefaultMaximumMethods);
        result.MaximumLabelLength = Positive(config.Attribute("MaximumLabelLength")?.Value, DefaultMaximumLabelLength);
        result.ProjectRoot = CanonicalPath(config.Attribute("ProjectRoot")?.Value ?? projectDirectory ?? string.Empty);
        result.Rules = config.Elements("Rule").Select((element, index) => Rule.Parse(element, index)).ToArray();
        return result;
    }

    private static int Positive(string? value, int fallback) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    internal static string CanonicalPath(string value) => value.Replace('\\', '/').TrimEnd('/');
}

internal sealed class Rule
{
    private readonly Regex _regex;
    private Rule(bool include, string target, string pattern, int order)
    { Include = include; Target = target; Pattern = pattern; Order = order; _regex = new Regex(Glob(pattern), RegexOptions.CultureInvariant); }
    internal bool Include { get; }
    internal string Target { get; }
    internal string Pattern { get; }
    internal int Order { get; }
    internal bool Matches(string target, string value) => (Target == "all" || Target == target) && _regex.IsMatch(value);
    internal static Rule Parse(XElement element, int order) => new(
        !string.Equals(element.Attribute("Action")?.Value, "exclude", StringComparison.OrdinalIgnoreCase),
        (element.Attribute("Target")?.Value ?? "all").ToLowerInvariant(), element.Attribute("Pattern")?.Value ?? "*", order);
    private static string Glob(string pattern) => "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
}
