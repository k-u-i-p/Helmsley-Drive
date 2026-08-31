namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// One portal row. Identity is the row id and nothing else — not where the item sits and not what
/// it is called — matching what an identifier means on the Mac side (Shared/ItemIdentity.swift).
/// <c>Version</c> is the content hash for a file — it changes when and only when the bytes do —
/// and empty for a folder, which has no bytes to hash; a name change is tracked as a name change,
/// not a version change.
///
/// <c>Created</c> and <c>Modified</c> are kept apart because Explorer shows both and sorts on
/// either: the portal's <c>updated_at</c> moves only on a save, so collapsing the two would date
/// every file in the tree by its last edit and make Date Created meaningless.
/// </summary>
public sealed record RemoteItem(
    string Id, string Name, bool IsFolder, long Size, DateTimeOffset Created, DateTimeOffset Modified, string Version);

/// <summary>
/// The slice of the portal the engine needs, a port of Mac/Shared/HelmsleyAPI.swift — list a
/// folder, follow the 302 into the bucket for bytes, and the writes the local callbacks map onto.
/// </summary>
public interface IRemoteStore
{
    /// <param name="folderId">A row id, or null for the root of the tree.</param>
    Task<IReadOnlyList<RemoteItem>> List(string? folderId);

    /// <summary>
    /// The file's bytes, as a stream the caller owns and disposes. A stream rather than an array
    /// because the portal's ceiling is 500MB a file: hydration copies it through in chunks, and
    /// each chunk handed to the filter is also what resets the platform's sixty-second patience
    /// with a callback that has not answered yet.
    /// </summary>
    Task<Stream> Fetch(string fileId);

    /// <summary>Files a new file into a folder, and answers the row it landed on.</summary>
    Task<RemoteItem> Upload(string? folderId, string filename, string localPath);

    /// <summary>
    /// Replaces one file's bytes under the same row — a save rather than a drop into a folder.
    /// Answers the row as it now stands; its id is unchanged, its version is not.
    /// </summary>
    Task<RemoteItem> ReplaceContents(string fileId, string localPath);

    /// <summary>Makes a directory. A taken name is numbered, so the answer is the folder to show.</summary>
    Task<RemoteItem> CreateFolder(string? parentId, string name);

    /// <summary>Renames one item; answers the name it now has. A taken name is refused.</summary>
    Task<string> Rename(string id, string newName);

    /// <summary>Moves one item; answers the name it landed under, which a collision may number.</summary>
    Task<string> Move(string id, string? folderId);

    /// <summary>Into the portal's bin. Recoverable there, which is why every local delete maps to it.</summary>
    Task Trash(string id);

    /// <summary>
    /// Out of the bin and into the named folder — null being the root of the tree, as it is
    /// everywhere else on this interface, not "wherever it came from".
    /// </summary>
    Task<string> Restore(string id, string? folderId);

    /// <summary>
    /// Out of the bin and back where it was. Distinct from <see cref="Restore"/> because the portal
    /// reads the presence of a destination as "the caller said where this should land": an undelete
    /// means put it back, and restoring to the top of the tree is a different sentence that must
    /// not be spelled the same way.
    /// </summary>
    Task<string> RestoreWhereItWas(string id);

    /// <summary>
    /// The distinction the engine acts on: gone means the item no longer exists to be written to,
    /// so stop holding the local side back for it; everything else is transient and worth a retry.
    /// </summary>
    bool IsNotFound(Exception error);
}

/// <summary>
/// Every listing the engine acts on crosses the seam through here.
///
/// A name from the portal is not yet a filename — <see cref="LocalNames"/> says why — and a
/// listing that skips this step becomes a silent gap in the tree. Putting it on the seam rather
/// than at each call site is what stops the next caller forgetting.
/// </summary>
public static class RemoteStoreExtensions
{
    public static async Task<IReadOnlyList<RemoteItem>> ListLocally(this IRemoteStore store, string? folderId) =>
        LocalNames.Legalise(await store.List(folderId).ConfigureAwait(false));
}
