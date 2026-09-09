namespace DayZFarm.Core;

/// <summary>Generates consistent, zero-padded client/VM names (DayZ-001, DayZ-002, ...).</summary>
public static class ClientNaming
{
    public const string Prefix = "DayZ-";
    private const int PadWidth = 3;

    public static string ForIndex(int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index), "Client index must be >= 1.");
        return $"{Prefix}{index.ToString().PadLeft(PadWidth, '0')}";
    }

    public static IEnumerable<string> Range(int start, int count)
    {
        if (start < 1) throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = start; i < start + count; i++)
            yield return ForIndex(i);
    }

    public static bool TryParseIndex(string name, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(name.AsSpan(Prefix.Length), out index);
    }
}
