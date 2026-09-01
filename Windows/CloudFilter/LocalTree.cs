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
            if (IsLocalOnly(entry)) kept.Add(entry);
        }
    }

    /// <summary>
    /// Whether one file holds bytes the portal does not. No identity means the portal never gave
    /// it a row; dirty means it has one and the bytes on disk are newer than the ones behind it.
    /// An editor's working file (<see cref="LocalNames.IsTransient"/>) is neither — it is state,
    /// not a document — and is never worth keeping. Where the filter will not answer, the
    /// dehydrated attribute is the one on-disk fact that still settles the question the safe way.
    /// </summary>
    public static bool IsLocalOnly(string file)
    {
        if (LocalNames.IsTransient(Path.GetFileName(file))) return false;
        try
        {
            return Placeholders.TryGetState(file) is not { } state || state.DataDirty;
        }
        catch (Exception e)
        {
            if (Dehydrated(file)) return false;
            Console.Error.WriteLine($"could not read the state of {file} ({e.Message}); keeping it");
            return true;
        }
    }

    /// <summary>
    /// Copies <paramref name="keep"/> out to <see cref="KeptAsideFrom"/>, leaving the originals
    /// where they are. For the mirror, which cannot move anything: a move out of a live sync root
    /// is a rename the filter would be asked about, and what is about to happen to the originals
    /// is a delete the portal asked for. Answers where the copies went, or null if nothing was
    /// copied — and a copy that fails is said aloud rather than thrown, since the caller's delete
    /// then has to be the one that does not happen.
    /// </summary>
    public static string? CopyAside(string root, IReadOnlyList<string> keep, out bool complete)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var keptAt = KeptAsideFrom(root);
        var anyKept = false;
        complete = true;
        foreach (var path in keep)
        {
            try
            {
                if (!File.Exists(path))
                {
                    // A directory that could not be read is on the list as a whole; a copy of it
                    // is exactly what cannot be made, so it counts as not set aside.
                    complete = false;
                    continue;
                }
                var landing = Unused(Path.Combine(keptAt, Path.GetRelativePath(full, path)));
                Directory.CreateDirectory(Path.GetDirectoryName(landing)!);
                File.Copy(path, landing);
                anyKept = true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"could not set {path} aside ({e.Message})");
                complete = false;
            }
        }
        return anyKept ? keptAt : null;
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

        // Entry by entry rather than one recursive delete, which stops at the first thing it
        // cannot remove — a document still open in Word — and leaves everything after it in
        // place, snapshot gone and registration gone, for the next mount to find as a tree of
        // ordinary files wearing the portal's names. Removing everything that will go leaves only
        // the locked file and the directories above it, which the next pass takes up again.
        var left = DeleteTree(full);
        if (left == 0) Console.WriteLine($"- {full} (signed out)");
        else Console.Error.WriteLine(
            $"{left} entries under {full} could not be removed — open in another program, most likely. " +
            "The rest of the tree has gone; those are taken up again by the next mount, or can be deleted by hand.");
        return anyKept ? keptAt : null;
    }

    /// <summary>Removes what it can under <paramref name="directory"/>, and answers how much it could not.</summary>
    static int DeleteTree(string directory)
    {
        var left = 0;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not read {directory} ({e.Message})");
            return 1;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    // A junction or a symlink inside the tree is removed as the link it is, never
                    // walked: what it points at is not the drive's to delete. A placeholder
                    // directory is a reparse point too, but not a link, and LinkTarget says so.
                    if (new DirectoryInfo(entry).LinkTarget is not null) Directory.Delete(entry);
                    else left += DeleteTree(entry);
                }
                else File.Delete(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"could not remove {entry} ({e.Message})");
                left++;
            }
        }

        if (left == 0)
        {
            try { Directory.Delete(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"could not remove {directory} ({e.Message})");
                left++;
            }
        }
        return left;
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
