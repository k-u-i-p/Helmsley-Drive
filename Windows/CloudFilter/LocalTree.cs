namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Taking the mirrored tree away, which is what a sign-out does with it.
///
/// The Mac removes the domain and the system removes the replica with it; here the tree is
/// ordinary directories in the user's profile and somebody has to delete them. Leaving them was
/// the old behaviour and it was worse than it looked: the placeholders stay dehydrated under a
/// registration that has gone, so their bytes are unreachable, and the next account's first pass
/// meets a tree that was never theirs.
///
/// One thing does not go. A file the portal has no row for — a local create whose upload never
/// landed, a save still waiting to go up — exists nowhere else, and this engine's whole posture is
/// that nothing local may destroy the only copy of any bytes: a delete maps to the portal's bin,
/// Shift+Delete included. So those are moved aside first, under a name that says why they are
/// there, and only what the portal can hand back is deleted.
/// </summary>
public static class LocalTree
{
    /// <summary>Where the files the portal never saw are put: beside the drive, and named for the reason.</summary>
    public static string KeptAsideFrom(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + " (not uploaded)";

    /// <summary>
    /// Everything under the tree that the portal does not hold, as full paths.
    ///
    /// Asked *while a provider is still connected to the sync root*, and separate from
    /// <see cref="Discard"/> for exactly that reason. Telling a hydrated placeholder from a local
    /// create is a question only the filter can answer — by name, size and attributes the two are
    /// identical — and once there is nobody behind the sync root it will not answer it:
    /// <c>CfOpenFileWithOplock</c> denies a cloud file whose provider has gone. So this runs before
    /// the disconnect, not merely before the unregistration.
    ///
    /// Where it is asked anyway with nothing connected — a <c>--sign-out</c> from a console, where
    /// the drive was never mounted in this process — a dehydrated placeholder still answers the
    /// only question that matters by its attributes: its bytes are in the bucket and nowhere else.
    /// Everything else is kept, because from there "hydrated" and "holding a save that never went
    /// up" look the same, and the cautious answer is the only one this engine is allowed.
    /// </summary>
    public static IReadOnlyList<string> LocalOnly(string root)
    {
        var kept = new List<string>();
        if (!Directory.Exists(root)) return kept;
        Walk(root, kept);
        return kept;
    }

    static void Walk(string directory, List<string> kept)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable, so unclassifiable. Keeping the whole directory is the cautious answer:
            // deleting what cannot be examined is how the only copy of something goes.
            Console.Error.WriteLine($"could not read {directory} ({e.Message}); keeping it");
            kept.Add(directory);
            return;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry)) { Walk(entry, kept); continue; }
            try
            {
                // No identity means the portal never gave it a row. Dirty means it has one and the
                // bytes on disk are newer than the ones behind it.
                if (Placeholders.TryGetState(entry) is not { } state || state.DataDirty) kept.Add(entry);
            }
            catch (Exception e)
            {
                if (Dehydrated(entry)) continue;
                Console.Error.WriteLine($"could not read the state of {entry} ({e.Message}); keeping it");
                kept.Add(entry);
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="keep"/> out to <see cref="KeptAsideFrom"/> and removes the tree.
    /// Answers where anything was kept, or null when there was nothing to keep.
    ///
    /// Run after the registration has gone, so that nothing here is a filter operation: the moves
    /// would otherwise leave the sync root — which is a rename the engine reads as a drag to the
    /// bin — and the deletes would be deletes it maps onto the portal's.
    /// </summary>
    public static string? Discard(string root, IReadOnlyList<string> keep)
    {
        if (!Directory.Exists(root)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var keptAt = KeptAsideFrom(root);
        var anyKept = false;

        foreach (var path in keep)
        {
            try
            {
                var landing = Path.Combine(keptAt, Path.GetRelativePath(full, path));
                Directory.CreateDirectory(Path.GetDirectoryName(landing)!);
                if (Directory.Exists(path)) Directory.Move(path, Unused(landing));
                else File.Move(path, Unused(landing));
                anyKept = true;
            }
            catch (Exception e)
            {
                // It stays where it is, and so does the tree around it: better an incomplete
                // sign-out the user can see than bytes nothing else holds, quietly gone.
                Console.Error.WriteLine($"could not set {path} aside ({e.Message}); leaving the tree in place");
                return anyKept ? keptAt : null;
            }
        }

        try
        {
            Directory.Delete(full, recursive: true);
            Console.WriteLine($"- {full} (signed out)");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"could not remove {full} ({e.Message}); it can be deleted by hand");
        }
        return anyKept ? keptAt : null;
    }

    /// <summary>
    /// Whether the entry's bytes are only in the bucket. The attribute is on-disk state rather than
    /// something the filter has to be asked for, which makes it the one thing still legible about a
    /// cloud file once its provider has gone.
    /// </summary>
    static bool Dehydrated(string path)
    {
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        try { return (File.GetAttributes(path) & RecallOnDataAccess) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A name nothing already answers to, so setting one file aside cannot cost another.</summary>
    static string Unused(string wanted)
    {
        if (!Path.Exists(wanted)) return wanted;
        var directory = Path.GetDirectoryName(wanted)!;
        var stem = Path.GetFileNameWithoutExtension(wanted);
        var extension = Path.GetExtension(wanted);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }
}
