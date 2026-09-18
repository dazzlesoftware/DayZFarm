using System.Text.RegularExpressions;

namespace GameFarm.VMware;

/// <summary>
/// Minimal reader/writer for VMware's .vmx format -- a flat text file of `key = "value"` lines,
/// one setting per line, order-preserving. vmrun.exe has no cmdlet-equivalent for things like
/// CPU count/memory size/network adapter settings (unlike Hyper-V's Set-VMProcessor/
/// Set-VMMemory/Set-VMNetworkAdapter) -- those are configured by editing the .vmx directly, which
/// is the standard, documented way VMware itself expects this to be done for automation.
/// </summary>
internal static class VmxFile
{
    private static readonly Regex LinePattern = new("^(?<key>[^#\\s][^=]*?)\\s*=\\s*\"?(?<value>.*?)\"?$", RegexOptions.Compiled);

    public static Dictionary<string, string> Read(string path)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var match = LinePattern.Match(line);
            if (match.Success)
                settings[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value;
        }
        return settings;
    }

    /// <summary>Sets (or inserts, if absent) each given key/value pair and writes the file back,
    /// preserving every other existing line and its original order.</summary>
    public static void Set(string path, IReadOnlyDictionary<string, string> updates)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var remaining = new Dictionary<string, string>(updates, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var match = LinePattern.Match(lines[i]);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value.Trim();
            if (remaining.TryGetValue(key, out var newValue))
            {
                lines[i] = $"{key} = \"{newValue}\"";
                remaining.Remove(key);
            }
        }

        foreach (var (key, value) in remaining)
            lines.Add($"{key} = \"{value}\"");

        File.WriteAllLines(path, lines);
    }
}
