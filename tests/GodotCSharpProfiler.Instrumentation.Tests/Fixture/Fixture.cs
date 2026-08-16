using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InstrumentationFixture;

public sealed class Fixture<T>
{
    public int Property { get; set; }
    public int MultiReturn(int value) { if (value < 0) return -1; if (value == 0) return 0; return value + 1; }
    public int Recursive(int value) => value == 0 ? 0 : 1 + Recursive(value - 1);
    public U Generic<U>(U value) { ArgumentNullException.ThrowIfNull(value); return value; }
    public int NestedHandlers(int value)
    {
        try { try { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); return 10 / value; } catch (DivideByZeroException) { return 0; } }
        finally { Property++; }
    }
    public void Throwing() => throw new InvalidOperationException("fixture");
    public async Task<int> Async() { await Task.Yield(); return 1; }
    public IEnumerable<int> Iterator() { yield return 1; }
    public void Trivial() { }
}
