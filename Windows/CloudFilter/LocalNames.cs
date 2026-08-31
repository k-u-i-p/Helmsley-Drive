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
///
/// It is also the boundary that keeps a portal name from being a path. A row named
/// <c>C:\Users\Ben\Documents</c> or <c>..\..\secrets</c> is a filename to the server and a
/// traversal to <see cref="Path.Combine(string,string)"/>, which returns its second argument
/// whole when that argument is rooted — so the separators go the way of the wildcards, and
/// <see cref="RemoteStoreExtensions.ListLocally"/> is where every listing meets this.
/// </summary>
public static class LocalNames
{
    static readonly System.Buffers.SearchValues<char> Illegal = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
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

        // "." and ".." survive the pass above — they hold no illegal character and end in no space
        // — and both name a directory rather than an entry in one.
        if (cleaned == "." || cleaned == "..") return cleaned.Replace('.', '_');

        // CON, NUL and their kin are devices whatever the extension says.
        var stem = cleaned.Split('.')[0];
        return Reserved.Contains(stem) ? "_" + cleaned : cleaned;
    }

    /// <summary>
    /// A listing as the local disk can hold it. Two rows whose names collapse to the same legal
    /// name would fight over one entry; the loser is dropped here, loudly, rather than mirrored
    /// into an entry that flickers between two identities.
    ///
    /// Ordered by id before the choice is made, because "the first one" is only a stable answer if
    /// the order is: a portal that reindexed between two passes would otherwise hand the entry to
    /// the other row, and re-stamp a placeholder Explorer is holding with a different identity.
    /// </summary>
    public static IReadOnlyList<RemoteItem> Legalise(IReadOnlyList<RemoteItem> items)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var legal = new List<RemoteItem>(items.Count);
        foreach (var item in items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            // One id, one entry: a listing that named a row twice would otherwise abort the whole
            // pass from inside a dictionary the diff builds.
            if (!ids.Add(item.Id))
            {
                Console.Error.WriteLine($"the listing named {item.Id} twice; keeping the first");
                continue;
            }
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
