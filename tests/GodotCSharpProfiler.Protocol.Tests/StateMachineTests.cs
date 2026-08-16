#if PROTOCOL_TESTS
using Apeworks.GodotCSharpProfiler.Protocol;

namespace GodotCSharpProfiler.Protocol.Tests;

public sealed class StateMachineTests
{
    private const string Token = "runtime-1";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void HappyPathIsDeterministic()
    {
        var machine = new CaptureStateMachine();
        Assert.Equal(CaptureState.Disconnected, machine.State);
        Assert.True(machine.Connect());
        Assert.Equal(CaptureState.Negotiating, machine.State);
        Assert.True(machine.AcceptHello(new HelloMessage(1, 0, Token, "client", 4096)));
        Assert.Equal(CaptureState.Ready, machine.State);
        Assert.True(machine.Configure(new ConfigureMessage(1, 0, Token, 1, Fingerprint,
            CaptureModes.Sampling | CaptureModes.ManualScopes, 100, 64)));
        Assert.True(machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "editor-A"));
        Assert.Equal(CaptureState.Starting, machine.State);
        Assert.True(machine.AcceptState(State(1, CaptureState.Capturing, 1)));
        Assert.Equal(CaptureState.Capturing, machine.State);
        Assert.True(machine.AcceptBatch(Batch(1, 2)));
        Assert.True(machine.Stop(new StopMessage(1, 0, Token, 1, 3, Fingerprint), "editor-A"));
        Assert.Equal(CaptureState.Stopping, machine.State);
        Assert.True(machine.AcceptState(State(1, CaptureState.Complete, 4, CaptureCompleteness.Complete)));
        Assert.Equal(CaptureState.Complete, machine.State);
        Assert.Null(machine.LeaseOwner);
    }

    [Fact]
    public void OneLeaseOwnerAndBusyState()
    {
        var machine = ReadyConfigured();
        Assert.True(machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "owner"));
        var beforeGeneration = machine.Generation;
        Assert.False(machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "other"));
        Assert.Equal(CaptureState.Busy, machine.State);
        Assert.Equal("owner", machine.LeaseOwner);
        Assert.Equal(beforeGeneration, machine.Generation);
        Assert.False(machine.Stop(new StopMessage(1, 0, Token, 1, 1, Fingerprint), "other"));
        Assert.Equal(CaptureState.Busy, machine.State);
    }

    [Fact]
    public void StaleGapAndDuplicateFailWithoutMutation()
    {
        var machine = ReadyConfigured();
        machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "owner");
        machine.AcceptState(State(1, CaptureState.Capturing, 1));
        Assert.True(machine.AcceptBatch(Batch(1, 2)));

        var snapshot = machine.Snapshot;
        Assert.False(machine.AcceptBatch(Batch(0, 3))); // stale generation
        Assert.Equal(snapshot, machine.Snapshot);
        Assert.False(machine.AcceptBatch(Batch(1, 2))); // duplicate
        Assert.Equal(snapshot, machine.Snapshot);
        Assert.False(machine.AcceptBatch(Batch(1, 4))); // gap
        Assert.Equal(snapshot, machine.Snapshot);
        Assert.False(machine.AcceptBatch(Batch(1, 3, "wrongfingerprintwrongfingerprint")));
        Assert.Equal(snapshot, machine.Snapshot);
    }

    [Fact]
    public void InvalidTransitionsFailClosed()
    {
        var machine = new CaptureStateMachine();
        var snapshot = machine.Snapshot;
        Assert.False(machine.Configure(new ConfigureMessage(1, 0, Token, 1, Fingerprint,
            CaptureModes.Sampling, 100, 10)));
        Assert.Equal(snapshot, machine.Snapshot);
        Assert.False(machine.AcceptBatch(Batch(1, 1)));
        Assert.Equal(snapshot, machine.Snapshot);
    }

    [Fact]
    public void QualityCountersAccumulateAndPartialReasonIsRecorded()
    {
        var machine = ReadyConfigured();
        machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "owner");
        machine.AcceptState(State(1, CaptureState.Capturing, 1));
        Assert.True(machine.AcceptBatch(Batch(1, 2, Fingerprint, 10, 2, 1, 3)));
        Assert.Equal(new QualityCounters(10, 2, 1, 3), machine.Quality);
        Assert.True(machine.Stop(new StopMessage(1, 0, Token, 1, 3, Fingerprint), "owner"));
        Assert.True(machine.AcceptState(State(1, CaptureState.Partial, 4,
            CaptureCompleteness.Partial, PartialReason.BufferOverflow, new QualityCounters(10, 2, 1, 3))));
        Assert.Equal(CaptureState.Partial, machine.State);
        Assert.Equal(PartialReason.BufferOverflow, machine.PartialReason);
    }

    [Fact]
    public void FatalErrorTransitionsToErrorAndReleasesLease()
    {
        var machine = ReadyConfigured();
        machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "owner");
        machine.AcceptState(State(1, CaptureState.Capturing, 1));
        Assert.True(machine.AcceptError(new ErrorMessage(1, 0, Token, 1, 2, 7, "failure", true)));
        Assert.Equal(CaptureState.Error, machine.State);
        Assert.Null(machine.LeaseOwner);
    }

    [Fact]
    public void DisconnectResetsRuntimeAndLease()
    {
        var machine = ReadyConfigured();
        machine.Start(new StartMessage(1, 0, Token, 1, Fingerprint), "owner");
        machine.Disconnect();
        Assert.Equal(CaptureState.Disconnected, machine.State);
        Assert.Null(machine.LeaseOwner);
        Assert.Equal(0, machine.Generation);
        Assert.Equal(0, machine.Sequence);
    }

    private static CaptureStateMachine ReadyConfigured()
    {
        var machine = new CaptureStateMachine();
        machine.Connect();
        machine.AcceptHello(new HelloMessage(1, 0, Token, "client", 4096));
        machine.Configure(new ConfigureMessage(1, 0, Token, 1, Fingerprint,
            CaptureModes.Sampling, 100, 64));
        return machine;
    }

    private static StateMessage State(long generation, CaptureState state, long sequence,
        CaptureCompleteness completeness = CaptureCompleteness.InProgress,
        PartialReason reason = PartialReason.None, QualityCounters? quality = null) =>
        new(1, 0, Token, generation, sequence, Fingerprint, state, CaptureSource.Sampling,
            completeness, reason, quality ?? QualityCounters.Zero);

    private static BatchMessage Batch(long generation, long sequence, string fingerprint = Fingerprint,
        long observed = 0, long dropped = 0, long overflowed = 0, long invalid = 0) =>
        new(1, 0, Token, generation, sequence, fingerprint, CaptureSource.Sampling, false, true,
            new QualityCounters(observed, dropped, overflowed, invalid), []);
}
#endif
