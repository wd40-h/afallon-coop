namespace AfallonCoop.Protocol;

public static class StableHash
{
    /// <summary>FNV-1a; identical on every machine (string.GetHashCode is randomized per process).</summary>
    public static int Of(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
