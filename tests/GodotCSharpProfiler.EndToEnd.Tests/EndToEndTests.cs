using Xunit;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

namespace GodotCSharpProfiler.EndToEnd.Tests;

public sealed class EndToEndTests
{
    [Theory]
    [InlineData(PrimaryMode.Sampling, true, CaptureSource.Sampling, CaptureSource.ManualSpans)]
    [InlineData(PrimaryMode.AutomaticInstrumentation, false, CaptureSource.AutomaticSpans, null)]
    [InlineData(PrimaryMode.None, true, CaptureSource.ManualSpans, null)]
    public void Strict_loop_negotiates_captures_and_commits_source_separated_results(
        PrimaryMode primary, bool manual, CaptureSource first, CaptureSource? second)
    {
        using var loop = new Loop();
        loop.Handshake();
        Assert.Equal(CaptureState.Ready, loop.Editor.Snapshot.State);
        Assert.Equal(AllModes, loop.Editor.Snapshot.SupportedModes);

        var configuration = ModeConfiguration.Default with { Primary = primary, IncludeManual = manual };
        Assert.True(loop.Editor.Start(configuration));
        loop.Pump();
        Assert.Equal(CaptureState.Capturing, loop.Editor.Snapshot.State);

        loop.Backend.Pending.Add(Batch(first, 7, first == CaptureSource.Sampling ? 8 : 8_000_000, first == CaptureSource.Sampling ? 0 : 2));
        if (second is { } overlay) loop.Backend.Pending.Add(Batch(overlay, 9, 3_000_000, 1));
        loop.Runtime.Flush();
        loop.Pump();
        Assert.False(loop.Editor.CompletedResults.HasResults); // batches never mutate displayed completed results

        Assert.True(loop.Editor.Stop());
        loop.Pump();
        Assert.Equal(CaptureState.Complete, loop.Editor.Snapshot.State);
        Assert.Equal(new[] { first }.Concat(second is { } value ? [value] : []), loop.Editor.CompletedResults.Groups.Select(group => group.Source));
        Assert.All(loop.Editor.CompletedResults.Groups, group => Assert.NotEmpty(group.Rows));
        Assert.False(loop.Backend.IsActive);
    }

    [Fact]
    public void Busy_and_wrong_owner_do_not_steal_or_stop_runtime_lease()
    {
        var transport = new QueueTransport();
        var backend = new FakeBackend(AllModes);
        using var runtime = new RuntimeCaptureCoordinator(Token, transport, backend);
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1), "owner-a"));
        Assert.True(runtime.Receive(Start(1), "owner-a"));
        Assert.False(runtime.Receive(Start(1), "owner-b"));
        Assert.False(runtime.Receive(Stop(1, 2), "owner-b"));
        Assert.True(backend.IsActive);
        Assert.True(runtime.Receive(Stop(1, 2), "owner-a"));
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void Malformed_stale_duplicate_and_sequence_gap_preserve_completed_display()
    {
        using var loop = new Loop();
        loop.Handshake();
        Assert.True(loop.Editor.Start(ModeConfiguration.Default with { IncludeManual = true }));
        loop.Pump();
        loop.Backend.Pending.Add(Batch(CaptureSource.Sampling, 1, 5, 0));
        loop.Runtime.Flush();
        loop.Pump();
        Assert.True(loop.Editor.Stop());
        loop.Pump();
        var completed = loop.Editor.CompletedResults;
        var snapshot = loop.Editor.Snapshot;

        Assert.False(loop.Editor.Receive(new Dictionary<string, object?> { ["kind"] = "batch" }));
        var stale = StrictWireAdapter.Serialize(new BatchMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, snapshot.Generation - 1,
            snapshot.Sequence + 1, snapshot.Fingerprint!, CaptureSource.Sampling, false, false,
            QualityCounters.Zero, [new(99, 99, 0)]));
        Assert.False(loop.Editor.Receive(stale));
        var gap = StrictWireAdapter.Serialize(new StateMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, snapshot.Generation,
            snapshot.Sequence + 2, snapshot.Fingerprint!, CaptureState.Complete, CaptureSource.Sampling,
            CaptureCompleteness.Complete, PartialReason.None, QualityCounters.Zero));
        Assert.False(loop.Editor.Receive(gap));
        Assert.True(loop.Editor.Receive(StrictWireAdapter.Serialize(new HelloMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, "runtime", 100))));
        Assert.Same(completed, loop.Editor.CompletedResults);
        Assert.Equal(snapshot, loop.Editor.Snapshot);
    }

    [Fact]
    public void Disconnect_preserves_completed_results_and_unload_disposes_every_backend()
    {
        var loop = new Loop();
        loop.Handshake();
        Assert.True(loop.Editor.Start(ModeConfiguration.Default with { Primary = PrimaryMode.None, IncludeManual = true }));
        loop.Pump();
        loop.Backend.Pending.Add(Batch(CaptureSource.ManualSpans, 1, 1_000_000, 1));
        loop.Runtime.Flush();
        loop.Pump();
        Assert.True(loop.Editor.Stop());
        var completed = loop.Editor.CompletedResults;
        loop.Editor.Disconnect();
        Assert.Equal(CaptureState.Disconnected, loop.Editor.Snapshot.State);
        Assert.Same(completed, loop.Editor.CompletedResults);
        loop.Dispose();
        Assert.False(loop.Backend.IsActive);
        Assert.True(loop.Backend.Disposed);
    }

    [Fact]
    public void Incompatible_major_is_rejected_without_state_or_result_mutation()
    {
        using var loop = new Loop();
        loop.Handshake();
        var snapshot = loop.Editor.Snapshot;
        var results = loop.Editor.CompletedResults;
        var incompatible = new WireMap(new KeyValuePair<string, WireValue>[]
        {
            new("kind", new WireString("hello")), new("major", new WireInteger(ProtocolVersion.Major + 1)),
            new("minor", new WireInteger(0)), new("runtimeToken", new WireString(Token)),
            new("role", new WireString("runtime")), new("maxBatchBytes", new WireInteger(1024))
        });
        Assert.False(loop.Editor.Receive(incompatible));
        Assert.Equal(snapshot, loop.Editor.Snapshot);
        Assert.Same(results, loop.Editor.CompletedResults);
    }

    [Fact]
    public void RepeatedSamplingDrainsKeepStableIdentityLabelsAndTerminalQualityEndToEnd()
    {
        using var loop = new Loop();
        loop.Handshake();
        Assert.True(loop.Editor.Start(ModeConfiguration.Default));
        loop.Pump();
        loop.Backend.Pending.Add(new(CaptureSource.Sampling, false, false,
            new QualityCounters(1, 1, 0, 0), [new(41, "Game.First", 2, 0)]));
        loop.Runtime.Flush();
        loop.Pump();
        loop.Backend.Pending.Add(new(CaptureSource.Sampling, false, false,
            new QualityCounters(1, 0, 1, 0), [new(42, "Game.Second", 3, 0)]));
        loop.Runtime.Flush();
        loop.Pump();
        Assert.True(loop.Editor.Stop());
        loop.Pump();

        var rows = loop.Editor.CompletedResults.Groups.Single().Rows;
        Assert.Equal(["Game.Second", "Game.First"], rows.Select(x => x.Name));
        Assert.Equal(new QualityCounters(2, 1, 1, 0), loop.Editor.Snapshot.Quality);
        Assert.Equal(2, loop.Editor.CompletedResults.Truncated);
    }

    [Fact]
    public void Production_source_has_no_legacy_control_or_frame_messages()
    {
        var root = FindRepositoryRoot();
        var production = Directory.EnumerateFiles(Path.Combine(root, "addons", "godot_csharp_profiler"), "*.cs", SearchOption.AllDirectories);
        var forbidden = new[] { "cs_profiler:start", "cs_profiler:stop", "cs_profiler:frame" };
        foreach (var path in production)
        {
            var source = File.ReadAllText(path);
            foreach (var value in forbidden) Assert.DoesNotContain(value, source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "addons"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static RuntimeSourceBatch Batch(CaptureSource source, long id, long value, long calls) =>
        new(source, source != CaptureSource.Sampling, false, new QualityCounters(1, 0, 0, 0),
            [new(id, $"Method {id}", value, calls)]);
    private static WireMap Configure(long generation) => StrictWireAdapter.Serialize(new ConfigureMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token,
        generation, Fingerprint, CaptureModes.ManualScopes, 0, 64, "", "", ""));
    private static WireMap Start(long generation) => StrictWireAdapter.Serialize(new StartMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, Fingerprint));
    private static WireMap Stop(long generation, long sequence) => StrictWireAdapter.Serialize(new StopMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, sequence, Fingerprint));

    private const string Token = "runtime-token";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef";
    private const CaptureModes AllModes = CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes;

    private sealed class Loop : IDisposable
    {
        public QueueTransport RuntimeOutput { get; } = new();
        public Queue<WireMap> EditorOutput { get; } = new();
        public FakeBackend Backend { get; } = new(AllModes);
        public RuntimeCaptureCoordinator Runtime { get; }
        public EditorCaptureCoordinator Editor { get; }
        public Loop()
        {
            Runtime = new RuntimeCaptureCoordinator(Token, RuntimeOutput, Backend);
            Editor = new EditorCaptureCoordinator("editor-owner", EditorOutput.Enqueue);
        }
        public void Handshake() { Runtime.Connect(); Pump(); }
        public void Pump()
        {
            var progress = true;
            while (progress)
            {
                progress = false;
                while (RuntimeOutput.Messages.TryDequeue(out var response)) { Assert.True(Editor.Receive(response)); progress = true; }
                while (EditorOutput.TryDequeue(out var command)) { Assert.True(Runtime.Receive(command, "editor-owner")); progress = true; }
            }
        }
        public void Dispose() => Runtime.Dispose();
    }

    private sealed class QueueTransport : IRuntimeCaptureTransport
    {
        public Queue<WireMap> Messages { get; } = new();
        public void Send(WireMap message) => Messages.Enqueue(message);
    }

    private sealed class FakeBackend(CaptureModes modes) : IRuntimeCaptureBackend
    {
        public RuntimeBackendCapabilities Capabilities { get; } = new(modes, true, 2_000_000, 4096, "test", "test");
        public bool IsActive { get; private set; }
        public bool Disposed { get; private set; }
        public List<RuntimeSourceBatch> Pending { get; } = [];
        public bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error)
        { _ = configuration; _ = owner; error = null; if (IsActive) return false; IsActive = true; return true; }
        public IReadOnlyList<RuntimeSourceBatch> Drain() { var result = Pending.ToArray(); Pending.Clear(); return result; }
        public IReadOnlyList<RuntimeSourceBatch> Stop() { if (!IsActive) return []; IsActive = false; return Drain(); }
        public void Dispose() { Stop(); Disposed = true; }
    }
}
