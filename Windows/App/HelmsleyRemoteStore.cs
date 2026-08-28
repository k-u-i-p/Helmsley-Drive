using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// The portal, behind the seam the engine talks to. A thin adapter over <see cref="HelmsleyApi"/>:
/// listings and fetches on the way down, and the engine's write calls mapped onto the portal's on
/// the way up. Mime types are not declared on upload — the portal falls back to octet-stream, and
/// corrects nothing the volume relies on.
/// </summary>
public sealed class HelmsleyRemoteStore : IRemoteStore
{
    readonly HelmsleyApi _api = HelmsleyApi.Shared;

    public async Task<IReadOnlyList<RemoteItem>> List(string? folderId)
    {
        var listing = await _api.List(Destination(folderId));

        var items = new List<RemoteItem>(listing.Folders.Count + listing.Files.Count);
        foreach (var folder in listing.Folders)
            items.Add(Item(folder));
        // A trashed row keeps the folder it was in, so listings still carry it — but it belongs in
        // a bin this engine does not have yet, not in the folder it came out of.
        foreach (var file in listing.Files.Where(f => !f.IsTrashed))
            items.Add(Item(file));
        return items;
    }

    public Task<byte[]> Fetch(string fileId) => _api.DownloadContents(fileId);

    public async Task<RemoteItem> Upload(string? folderId, string filename, string localPath) =>
        Item(await _api.Upload(Destination(folderId), filename, mime: null, localPath));

    public async Task<RemoteItem> ReplaceContents(string fileId, string localPath) =>
        Item(await _api.ReplaceContents(fileId, mime: null, localPath));

    public async Task<RemoteItem> CreateFolder(string? parentId, string name) =>
        Item(await _api.CreateFolder(Destination(parentId), name));

    public Task<string> Rename(string id, string newName) => _api.Rename(id, newName);

    public Task<string> Move(string id, string? folderId) => _api.Move(id, Destination(folderId));

    public Task Trash(string id) => _api.Trash(id);

    public Task<string> Restore(string id, string? folderId) =>
        _api.Restore(id, folderId is null ? null : App.Destination.Item(folderId));

    public bool IsNotFound(Exception error) => error is ApiException { IsNotFound: true };

    static Destination Destination(string? folderId) =>
        folderId is null ? App.Destination.Root : App.Destination.Item(folderId);

    static RemoteItem Item(RemoteFile file) =>
        new(file.Id, file.Filename, file.IsFolder, file.Size ?? 0,
            Modified(file.ModifiedDate, file.UploadDate), file.IsFolder ? "" : file.Version);

    static RemoteItem Item(RemoteFolder folder) =>
        new(folder.Id, folder.Name, true, 0, Modified(folder.ModifiedDate, folder.UploadDate), "");

    /// <summary>
    /// The epoch stands in where the portal sent no instants — a folder it has declared but never
    /// written has none, and never will.
    /// </summary>
    static DateTimeOffset Modified(string? modifiedDate, string? uploadDate) =>
        Timestamp.Date(modifiedDate) ?? Timestamp.Date(uploadDate) ?? DateTimeOffset.UnixEpoch;
}
