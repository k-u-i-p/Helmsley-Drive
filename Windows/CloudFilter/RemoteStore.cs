namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// One portal row. Identity is the row id and nothing else — not where the item sits and not what
/// it is called — matching what an identifier means on the Mac side (Shared/ItemIdentity.swift).
/// </summary>
public sealed record RemoteItem(string Id, string Name, bool IsFolder, long Size, DateTimeOffset Modified);

/// <summary>
/// The slice of the portal the engine needs. The real implementation is a port of
/// Mac/Shared/HelmsleyAPI.swift — list a folder, follow the 302 into the bucket for bytes.
/// </summary>
public interface IRemoteStore
{
    /// <param name="folderId">A row id, or null for the root of the tree.</param>
    Task<IReadOnlyList<RemoteItem>> List(string? folderId);

    /// <summary>Whole-file fetch. Ranged requests can come later; hydration hands us the range.</summary>
    Task<byte[]> Fetch(string fileId);
}
