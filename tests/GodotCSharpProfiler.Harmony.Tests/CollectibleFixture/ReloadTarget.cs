using System.Runtime.CompilerServices;

namespace HarmonyCollectibleFixture;

public sealed class ReloadTarget
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Compute(int value)
    {
        var doubled = value * 2;
        return doubled + 1;
    }
}
