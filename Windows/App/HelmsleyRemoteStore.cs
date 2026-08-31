using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// The portal, behind the seam the engine talks to. A thin adapter over <see cref="HelmsleyApi"/>:
/// listings and fetches on the way down, and the engine's write calls mapped onto the portal's on
/// the way up. Mime types are not declared on upload — the portal falls back to octet-stream, and
/// corrects nothing the volume relies on.
///
/// Every name crossing in this direction is legalised here, reads and writes alike. A name is not
/// a path, and one that arrives from a write — the row a save or a mkdir answered with — reaches
/// the snapshot and, a pass later, <c>Path.Combine</c>: <see cref="LocalNames"/> is what keeps a
/// row called <c>C:\Windows</c> from addressing exactly that.
/// </summary>
public sealed class HelmsleyRemoteStore : IRemoteStore
{
    readonly HelmsleyApi _api = HelmsleyApi.Shared;

    public async Task<IReadOnlyList<RemoteItem>> List(string? folderId)
    {
        var listing = await _api.List(Destination(folderId)).ConfigureAwait(false);

        var items = new List<RemoteItem>(listing.Folders.Count + listing.Files.Count);
        foreach (var folder in listing.Folders)
            items.Add(Item(folder));
        // A trashed row keeps the folder it was in, so listings still carry it — but it belongs in
        // a bin this engine does not have yet, not in the folder it came out of.
        foreach (var file in listing.Files.Where(f => !f.IsTrashed))
            items.Add(Item(file));
        return items;
    }

    public Task<Stream> Fetch(string fileId) => _api.DownloadContents(fileId);

    public async Task<RemoteItem> Upload(string? folderId, string filename, string localPath) =>
        Item(await _api.Upload(Destination(folderId), filename, mime: null, localPath).ConfigureAwait(false));

    public async Task<RemoteItem> ReplaceContents(string fileId, string localPath) =>
        Item(await _api.ReplaceContents(fileId, mime: null, localPath).ConfigureAwait(false));

    public async Task<RemoteItem> CreateFolder(string? parentId, string name) =>
        Item(await _api.CreateFolder(Destination(parentId), name).ConfigureAwait(false));

    public Task<string> Rename(string id, string newName) => _api.Rename(id, newName);

    public Task<string> Move(string id, string? folderId) => _api.Move(id, Destination(folderId));

    public Task Trash(string id) => _api.Trash(id);

    /// <summary>
    /// Null is the root of the tree here, as it is on every other method — which is why the empty
    /// path goes with it. Sending no destination at all is the portal's way of being told nothing,
    /// and it answers that by putting the row back where it came from: a different sentence, and
    /// the one <see cref="RestoreWhereItWas"/> is for.
    /// </summary>
    public Task<string> Restore(string id, string? folderId) => _api.Restore(id, Destination(folderId));

    public Task<string> RestoreWhereItWas(string id) => _api.Restore(id, destination: null);

    public bool IsNotFound(Exception error) => error is ApiException { IsNotFound: true };

    static Destination Destination(string? folderId) =>
        folderId is null ? App.Destination.Root : App.Destination.Item(folderId);

    static RemoteItem Item(RemoteFile file) =>
        new(file.Id, LocalNames.Legal(file.Filename), file.IsFolder, file.Size ?? 0,
            Created(file.UploadDate, file.ModifiedDate), Modified(file.ModifiedDate, file.UploadDate),
            file.IsFolder ? "" : file.Version);

    static RemoteItem Item(RemoteFolder folder) =>
        new(folder.Id, LocalNames.Legal(folder.Name), true, 0,
            Created(folder.UploadDate, folder.ModifiedDate), Modified(folder.ModifiedDate, folder.UploadDate), "");

    /// <summary>
    /// When the row was written. Falls back to the modification instant rather than to the epoch,
    /// because a Date Created of 1970 in Explorer is a wrong answer where the last save is merely
    /// an imprecise one.
    /// </summary>
    static DateTimeOffset Created(string? uploadDate, string? modifiedDate) =>
        Timestamp.Date(uploadDate) ?? Timestamp.Date(modifiedDate) ?? DateTimeOffset.UnixEpoch;

    /// <summary>
    /// The epoch stands in where the portal sent no instants — a folder it has declared but never
    /// written has none, and never will.
    /// </summary>
    static DateTimeOffset Modified(string? modifiedDate, string? uploadDate) =>
        Timestamp.Date(modifiedDate) ?? Timestamp.Date(uploadDate) ?? DateTimeOffset.UnixEpoch;
}
