#nullable enable
using System.Globalization;
using System.Text.Json;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Bounded codec for untrusted editor metadata retained across managed assembly reloads.</summary>
public static class ProfilerReloadStateCodec
{
    public const int MaximumCharacters = 1_000_000;

    public static bool TryDecode(string? json, out ProfilerDockReloadState? state)
    {
        state = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaximumCharacters)
            return false;
        try
        {
            var candidate = JsonSerializer.Deserialize<ProfilerDockReloadState>(json);
            return ProfilerDockController.TryNormalizeReloadState(candidate, out state);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or
                                      ArgumentException or InvalidOperationException or OverflowException or
                                      NullReferenceException)
        {
            return false;
        }
    }

    public static bool TryEncode(ProfilerDockReloadState? value, out string json)
    {
        json = string.Empty;
        if (!ProfilerDockController.TryNormalizeReloadState(value, out var normalized))
            return false;
        try
        {
            json = JsonSerializer.Serialize(normalized);
            if (json.Length == 0 || json.Length > MaximumCharacters)
            {
                json = string.Empty;
                return false;
            }
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or
                                      ArgumentException or InvalidOperationException or OverflowException or
                                      NullReferenceException)
        {
            json = string.Empty;
            return false;
        }
    }
}

/// <summary>Allocation-bounded scalar session-ID metadata codec.</summary>
public static class ProfilerReloadSessionIdsCodec
{
    public const int MaximumSessions = 64;
    public const int MaximumCharacters = 1_024;

    public static string Encode(IEnumerable<int> sessionIds) => string.Join(",",
        (sessionIds ?? Array.Empty<int>()).Where(id => id >= 0).Distinct().OrderBy(id => id)
            .Take(MaximumSessions));

    public static bool TryDecode(string? encoded, out int[] sessionIds)
    {
        sessionIds = Array.Empty<int>();
        if (encoded is null || encoded.Length > MaximumCharacters)
            return false;
        if (encoded.Length == 0)
            return true;

        var result = new List<int>(Math.Min(MaximumSessions, 8));
        var seen = new HashSet<int>();
        var start = 0;
        for (var index = 0; index <= encoded.Length; index++)
        {
            if (index != encoded.Length && encoded[index] != ',')
                continue;
            var token = encoded.AsSpan(start, index - start).Trim();
            if (token.Length == 0 || !int.TryParse(token, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var sessionId) || sessionId < 0)
                return false;
            if (!seen.Contains(sessionId))
            {
                if (result.Count == MaximumSessions)
                {
                    sessionIds = Array.Empty<int>();
                    return false;
                }
                seen.Add(sessionId);
                result.Add(sessionId);
            }
            start = index + 1;
        }
        sessionIds = result.ToArray();
        return true;
    }

    public static bool TryDecode(IReadOnlyList<long>? values, out int[] sessionIds)
    {
        sessionIds = Array.Empty<int>();
        if (values is null || values.Count > MaximumSessions)
            return false;
        var result = new List<int>(values.Count);
        var seen = new HashSet<int>();
        foreach (var value in values)
        {
            if (value is < 0 or > int.MaxValue)
                return false;
            var sessionId = (int)value;
            if (seen.Add(sessionId)) result.Add(sessionId);
        }
        sessionIds = result.ToArray();
        return true;
    }
}

/// <summary>Wrapper replacement changes handlers only; teardown remains an explicit operation.</summary>
public static class ProfilerDebuggerPanelBinding
{
    public static T Rebind<T>(T? previous, T replacement, Action<T> detach, Action<T> attach)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(detach);
        ArgumentNullException.ThrowIfNull(attach);
        if (previous is not null)
            detach(previous);
        attach(replacement);
        return replacement;
    }
}
