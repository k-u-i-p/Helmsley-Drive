namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// The portal accepts names Windows will not: <c>Mr Graham Hall*</c> and
/// <c>… Ref: Y401160</c> are real rows, and a placeholder create for either fails — silently, as
/// a gap in the tree. So every name that arrives from the portal passes through here before it
/// becomes a filename, each illegal character standing down for an underscore.
///
/// Identity is untouched — an item is its row id, and the row keeps its real name; this is only
/// what the entry is called on disk. The one leak is deliberate: renaming such an item locally
/// sends the underscored name to the portal, because the local name is all a rename has to say.
/// </summary>
public static class LocalNames
{
    static readonly System.Buffers.SearchValues<char> Illegal = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Legal(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || Illegal.Contains(chars[i])) chars[i] = '_';
        }

        // A trailing dot or space is silently dropped by the filesystem, so a name kept verbatim
        // would never match the entry it created. Underscores keep the length honest instead.
        var end = chars.Length;
        while (end > 0 && chars[end - 1] is ' ' or '.') chars[--end] = '_';

        var cleaned = new string(chars);
        if (cleaned.Length == 0) return "_";

        // CON, NUL and their kin are devices whatever the extension says.
        var stem = cleaned.Split('.')[0];
        return Reserved.Contains(stem) ? "_" + cleaned : cleaned;
    }

    /// <summary>
    /// A listing as the local disk can hold it. Two rows whose names collapse to the same legal
    /// name would fight over one entry; the loser is dropped here, loudly, rather than mirrored
    /// into an entry that flickers between two identities.
    /// </summary>
    public static IReadOnlyList<RemoteItem> Legalise(IReadOnlyList<RemoteItem> items)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legal = new List<RemoteItem>(items.Count);
        foreach (var item in items)
        {
            var name = Legal(item.Name);
            if (!taken.Add(name))
            {
                Console.Error.WriteLine($"two rows share the local name {name}; keeping the first, skipping {item.Id}");
                continue;
            }
            legal.Add(name == item.Name ? item : item with { Name = name });
        }
        return legal;
    }
}
