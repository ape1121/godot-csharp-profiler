namespace CecilFixture;

public class Fixture
{
    public Fixture() { }
    public int Property { get; set; }
    public int Ordinary(int value) => value + 1;
    public int Recursive(int value) => value <= 0 ? 0 : 1 + Recursive(value - 1);
    public string Overloaded(string value) => value + "!";
    public int Overloaded(int value) => value * 2;
    public T Generic<T>(T value) => value;
    public void Throwing() => throw new InvalidOperationException("fixture");
    public async Task<int> Async() { await Task.Yield(); return 7; }
    public IEnumerable<int> Iterator() { yield return 1; yield return 2; }
}
