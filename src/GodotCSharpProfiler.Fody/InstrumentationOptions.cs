using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fody;

namespace GodotCSharpProfiler.Fody;

internal sealed class InstrumentationOptions
{
    internal const int RuntimeMaximumMethods = 16384;
    internal const int RuntimeMaximumLabelLength = 512;
    internal const int DefaultMaximumMethods = RuntimeMaximumMethods;
    internal const int DefaultMaximumLabelLength = RuntimeMaximumLabelLength;
    internal int MaximumMethods { get; private set; } = DefaultMaximumMethods;
    internal int MaximumLabelLength { get; private set; } = DefaultMaximumLabelLength;
    internal string ProjectRoot { get; private set; } = string.Empty;
    internal string? EmbeddedConfigHash { get; private set; }
    internal IReadOnlyList<Rule> Rules { get; private set; } = Array.Empty<Rule>();

    internal static InstrumentationOptions Parse(XElement? config, string? projectDirectory)
    {
        var result = new InstrumentationOptions { ProjectRoot = CanonicalPath(projectDirectory ?? string.Empty) };
        if (config is null) return result;
        if (config.Name.LocalName != "GodotCSharpProfiler")
            throw Error($"expected <GodotCSharpProfiler>, found <{config.Name.LocalName}>");

        // Owner is installer lifecycle metadata; it is intentionally excluded from the instrumentation hash.
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Owner", "MaximumMethods", "MaximumLabelLength", "ProjectRoot", "ConfigHash" };
        foreach (var attribute in config.Attributes())
            if (!allowed.Contains(attribute.Name.LocalName)) throw Error($"unknown configuration field '{attribute.Name.LocalName}'");
        foreach (var child in config.Elements())
            if (child.Name.LocalName != "Rule") throw Error($"unknown or duplicate configuration field '{child.Name.LocalName}'");
        if (config.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
            throw Error("configuration may contain only ordered <Rule> children");

        result.MaximumMethods = Limit(config, "MaximumMethods", DefaultMaximumMethods, RuntimeMaximumMethods);
        result.MaximumLabelLength = Limit(config, "MaximumLabelLength", DefaultMaximumLabelLength, RuntimeMaximumLabelLength);
        result.ProjectRoot = CanonicalPath(config.Attribute("ProjectRoot")?.Value ?? projectDirectory ?? string.Empty);
        if (result.ProjectRoot.Length == 0) throw Error("ProjectRoot must resolve to a non-empty canonical path");
        result.EmbeddedConfigHash = config.Attribute("ConfigHash")?.Value;
        if (result.EmbeddedConfigHash is not null && !Regex.IsMatch(result.EmbeddedConfigHash, "^[0-9a-f]{16}$", RegexOptions.CultureInvariant))
            throw Error("ConfigHash must be exactly 16 lowercase hexadecimal characters");
        result.Rules = config.Elements().Select((element, index) => Rule.Parse(element, index)).ToArray();
        return result;
    }

    private static int Limit(XElement config, string name, int fallback, int maximum)
    {
        var value = config.Attribute(name)?.Value;
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0 || parsed > maximum)
            throw Error($"{name} must be an integer from 1 through {maximum}");
        return parsed;
    }

    internal static string CanonicalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Path.GetFullPath(value).Replace('\\', '/').TrimEnd('/'); }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        { throw Error($"invalid ProjectRoot: {exception.Message}"); }
    }

    private static WeavingException Error(string message) => new($"Invalid GodotCSharpProfiler configuration: {message}.");
}

internal sealed class Rule
{
    private readonly Regex _regex;

    private Rule(bool include, string target, string pattern, int order, Regex regex)
    { Include = include; Target = target; Pattern = pattern; Order = order; _regex = regex; }

    internal bool Include { get; }
    internal string Target { get; }
    internal string Pattern { get; }
    internal int Order { get; }
    internal bool Matches(string target, string value) => (Target == "all" || Target == target) && _regex.IsMatch(value);

    internal static Rule Parse(XElement element, int order)
    {
        if (element.HasElements || element.Nodes().Any()) throw Error(order, "Rule must be empty");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Action", "Target", "Pattern" };
        foreach (var attribute in element.Attributes())
            if (!allowed.Contains(attribute.Name.LocalName)) throw Error(order, $"unknown or duplicate field '{attribute.Name.LocalName}'");

        var action = element.Attribute("Action")?.Value;
        var target = element.Attribute("Target")?.Value;
        var rawPattern = element.Attribute("Pattern")?.Value;
        if (action != "include" && action != "exclude") throw Error(order, "Action must be exactly 'include' or 'exclude'");
        if (target != "all" && target != "namespace" && target != "type" && target != "method")
            throw Error(order, "Target must be exactly 'all', 'namespace', 'type', or 'method'");
        if (string.IsNullOrEmpty(rawPattern)) throw Error(order, "Pattern is required");
        var pattern = rawPattern!;
        if (pattern.IndexOfAny(new[] { '\0', '\r', '\n', '\\' }) >= 0 || pattern.Contains("***"))
            throw Error(order, "Pattern is not a valid canonical glob");

        var expression = "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        try { return new Rule(action == "include", target, pattern, order, new Regex(expression, RegexOptions.CultureInvariant)); }
        catch (ArgumentException exception) { throw Error(order, $"invalid Pattern: {exception.Message}"); }
    }

    private static WeavingException Error(int order, string message) => new($"Invalid GodotCSharpProfiler configuration: Rule #{order + 1} {message}.");
}
