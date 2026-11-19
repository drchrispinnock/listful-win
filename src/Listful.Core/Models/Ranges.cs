namespace Listful;

/// <summary>A half-open range of indices, <c>Lower..&lt;Upper</c> (Swift's <c>Range&lt;Int&gt;</c>).</summary>
public readonly record struct IntRange(int Lower, int Upper)
{
    public int Count => Upper - Lower;
    public bool IsEmpty => Upper <= Lower;
    public bool Contains(int i) => i >= Lower && i < Upper;
    public IEnumerable<int> Indices() { for (var i = Lower; i < Upper; i++) yield return i; }
    public override string ToString() => $"{Lower}..<{Upper}";
}

/// <summary>A closed range, <c>Lower...Upper</c> (Swift's <c>ClosedRange&lt;Int&gt;</c>).</summary>
public readonly record struct ClosedIntRange(int Lower, int Upper)
{
    public bool Contains(int i) => i >= Lower && i <= Upper;
    public override string ToString() => $"{Lower}...{Upper}";
}
