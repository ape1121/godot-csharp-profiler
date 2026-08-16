using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HarmonyProofFixtures
{

public class MethodFixture
{
    public MethodFixture()
    {
        Value = 3;
    }

    public int Value { get; set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Ordinary(int value)
    {
        var next = value + 1;
        return next;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Overloaded(int value)
    {
        return value * 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Overloaded(string value)
    {
        return value + value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public T Generic<T>(T value)
    {
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Recursive(int depth)
    {
        return depth <= 0 ? 1 : 1 + Recursive(depth - 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Throwing()
    {
        throw new FixtureException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AggressivelyInlined(int value) => value + 2;

    public int Trivial() => 1;

    public async Task<int> Async(int value)
    {
        await Task.Yield();
        return value + 1;
    }

    public IEnumerable<int> Iterator(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return index;
        }
    }
}

public sealed class FixtureException : Exception;

public abstract class UnsupportedFixture
{
    public abstract int AbstractMethod();

    [DllImport("definitely_missing_harmony_proof_library", EntryPoint = "missing")]
    public static extern int NativeExtern();
}

public sealed class UnselectedFixture
{
    public int MustNeverAppear() => 42;
}
}
namespace GodotCSharpProfiler.Internal
{
    public sealed class ProfilerOwnedFixture
    {
        public int InternalWork(int value) => value + 1;
    }
}
