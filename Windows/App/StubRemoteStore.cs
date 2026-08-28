using System.Text;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// A hand-drawn corner of the portal tree, standing in for the HelmsleyAPI port. It exists so the
/// engine can be exercised — placeholders, hydration, the lot — before the portal is wired up.
/// </summary>
public sealed class StubRemoteStore : IRemoteStore
{
    sealed record Node(RemoteItem Item, string? ParentId, string? Content);

    readonly Dictionary<string, Node> _nodes = new();

    public StubRemoteStore()
    {
        var when = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

        void Folder(string id, string? parent, string name) =>
            _nodes[id] = new(new RemoteItem(id, name, true, 0, when), parent, null);
        void File(string id, string? parent, string name, string content) =>
            _nodes[id] = new(new RemoteItem(id, name, false, Encoding.UTF8.GetByteCount(content), when), parent, content);

        Folder("1", null, "News");
        Folder("2", null, "Shared");
        Folder("3", null, "Staff");
        Folder("4", "3", "Ben Allright");
        File("5", null, "Welcome.txt",
            "Helmsley Drive on Windows.\r\n\r\nThis file was a placeholder until you opened it; " +
            "the open is what fetched it. The real tree arrives when the portal is wired up.\r\n");
        File("6", "2", "About this folder.txt",
            "The office's shared folder — every admin's to change.\r\n");
    }

    public Task<IReadOnlyList<RemoteItem>> List(string? folderId) =>
        Task.FromResult<IReadOnlyList<RemoteItem>>(
            _nodes.Values.Where(n => n.ParentId == folderId).Select(n => n.Item).ToList());

    public Task<byte[]> Fetch(string fileId) =>
        _nodes.TryGetValue(fileId, out var node) && node.Content is not null
            ? Task.FromResult(Encoding.UTF8.GetBytes(node.Content))
            : Task.FromException<byte[]>(new FileNotFoundException(fileId));
}
