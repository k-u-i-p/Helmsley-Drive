using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// The portal, behind the seam the engine talks to. A thin adapter over <see cref="HelmsleyApi"/>:
/// the engine's two calls today are a listing and a whole-file fetch, and the interface grows as
/// writes arrive.
/// </summary>
public sealed class HelmsleyRemoteStore : IRemoteStore
{
    readonly HelmsleyApi _api = HelmsleyApi.Shared;

    public async Task<IReadOnlyList<RemoteItem>> List(string? folderId)
    {
        var destination = folderId is null ? Destination.Root : Destination.Item(folderId);
        var listing = await _api.List(destination);

        var items = new List<RemoteItem>(listing.Folders.Count + listing.Files.Count);
        foreach (var folder in listing.Folders)
            items.Add(new RemoteItem(folder.Id, folder.Name, true, 0, Modified(folder.ModifiedDate, folder.UploadDate)));
        // A trashed row keeps the folder it was in, so listings still carry it — but it belongs in
        // a bin this engine does not have yet, not in the folder it came out of.
        foreach (var file in listing.Files.Where(f => !f.IsTrashed))
            items.Add(new RemoteItem(file.Id, file.Filename, false, file.Size ?? 0, Modified(file.ModifiedDate, file.UploadDate)));
        return items;
    }

    public Task<byte[]> Fetch(string fileId) => _api.DownloadContents(fileId);

    /// <summary>
    /// The epoch stands in where the portal sent no instants — a folder it has declared but never
    /// written has none, and never will.
    /// </summary>
    static DateTimeOffset Modified(string? modifiedDate, string? uploadDate) =>
        Timestamp.Date(modifiedDate) ?? Timestamp.Date(uploadDate) ?? DateTimeOffset.UnixEpoch;
}
