using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelmsleyDrive.App;

// MARK: - Wire types (Mac/Shared/HelmsleyAPI.swift)

/// <summary>
/// The portal's <c>created_at</c> and <c>updated_at</c>: ISO 8601 in UTC, with fractional seconds
/// where the instant has them. <c>updated_at</c> moves only on a save — never on a rename, a move
/// or a trip through the bin — which is what makes it a Date Modified.
/// </summary>
public static class Timestamp
{
    public static DateTimeOffset? Date(string? value) =>
        // AssumeUniversal, because RoundtripKind does nothing for a string carrying neither a Z nor
        // an offset and the fallback is local time — which would date every row by the reader's
        // timezone rather than the writer's.
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
}

/// <summary>
/// What the portal will let this admin do to one row, decided by the row's whole ancestor chain.
/// Asked rather than guessed at, because the rules are statements about where a row sits and only
/// the server knows.
/// </summary>
public sealed class Permissions
{
    /// <summary>Whether things can be put *into* this folder — a new file, or a new folder.</summary>
    [JsonPropertyName("writable")] public bool Writable { get; set; }
    [JsonPropertyName("renamable")] public bool Renamable { get; set; }
    [JsonPropertyName("movable")] public bool Movable { get; set; }
    [JsonPropertyName("deletable")] public bool Deletable { get; set; }

    /// <summary>
    /// What a portal that predates these flags leaves the client to assume: everything the folder's
    /// own <c>writable</c> allows. Wrong only in the direction of offering something the server
    /// then refuses — with its own sentence — rather than hiding something that would have worked.
    /// </summary>
    public static Permissions AssumedFrom(bool writable) => new()
    {
        Writable = writable, Renamable = writable, Movable = writable, Deletable = writable,
    };

    /// <summary>
    /// What the Mac folds into an item's metadata version, so a folder that becomes read-only
    /// under somebody is re-reported rather than left offering writes it no longer has. Nothing
    /// here reads it yet: a Windows placeholder carries no permission state and its version is the
    /// content hash alone, so a permission change does not propagate. Kept for when it does.
    /// </summary>
    [JsonIgnore]
    public string Signature =>
        $"{(Writable ? "w" : "-")}{(Renamable ? "r" : "-")}{(Movable ? "m" : "-")}{(Deletable ? "d" : "-")}";
}

/// <summary>
/// One row of the tree, as <c>/api/files</c> describes it. <c>Version</c> is the content hash: it
/// changes when and only when the bytes do. <c>Id</c> is opaque and has to stay that way — a
/// serial for a row, and a <c>v&lt;parent&gt;_&lt;type&gt;</c> reference for a folder the portal
/// has declared but not yet written; nothing here may assume which.
/// </summary>
public sealed class RemoteFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("mime")] public string? Mime { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    [JsonPropertyName("uploadDate")] public string? UploadDate { get; set; }
    [JsonPropertyName("modifiedDate")] public string? ModifiedDate { get; set; }

    /// <summary>Which folder holds it, or null for a row directly under the tree's root.</summary>
    [JsonPropertyName("parent")] public string? Parent { get; set; }

    // Nullable so that a listing from a portal that predates each flag still decodes; a missing
    // value reads as false, which is what it meant.
    [JsonPropertyName("isDir")] public bool? IsDir { get; set; }
    [JsonPropertyName("trashed")] public bool? Trashed { get; set; }

    /// <summary>
    /// Whether something *above* this row was thrown away. Not the same as being trashed: a
    /// covered row still hangs under the folder it always did, but takes no writes.
    /// </summary>
    [JsonPropertyName("covered")] public bool? Covered { get; set; }

    [JsonPropertyName("permissions")] public Permissions? Permissions { get; set; }

    [JsonIgnore] public bool IsFolder => IsDir == true;
    [JsonIgnore] public bool IsTrashed => Trashed == true;
    [JsonIgnore] public bool IsCovered => Covered == true;
}

/// <summary>
/// One subfolder. <c>Id</c> addresses it; <c>Name</c> is separate because a folder is *labelled*
/// rather than named — renaming a client in the portal renames their folder, and nothing anybody
/// holds stops resolving.
/// </summary>
public sealed class RemoteFolder
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("writable")] public bool Writable { get; set; }
    [JsonPropertyName("permissions")] public Permissions? Permissions { get; set; }

    // Nullable for one reason more than the file's: a folder the portal has declared but not yet
    // written has no row, so it has no instants to send and never will.
    [JsonPropertyName("uploadDate")] public string? UploadDate { get; set; }
    [JsonPropertyName("modifiedDate")] public string? ModifiedDate { get; set; }
}

public sealed class Listing
{
    [JsonPropertyName("folders")] public List<RemoteFolder> Folders { get; set; } = new();
    [JsonPropertyName("files")] public List<RemoteFile> Files { get; set; } = new();

    /// <summary>Whether things can be put into the folder that was listed.</summary>
    [JsonPropertyName("writable")] public bool Writable { get; set; }

    /// <summary>
    /// Whether that folder is in the bin — itself thrown away, or under something that was. Not
    /// inferable from <c>Writable</c>, which is false wherever a folder is merely read-only.
    /// </summary>
    [JsonPropertyName("trashed")] public bool? Trashed { get; set; }

    [JsonIgnore] public bool IsTrashed => Trashed == true;

    /// <summary>
    /// What to credit a *file* in this folder with when the portal did not say: a file is never
    /// one of the folders the tree placed, so it follows the folder's writability.
    /// </summary>
    [JsonIgnore] public Permissions AssumedForFiles => Permissions.AssumedFrom(Writable);
}

public sealed class Admin
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
}

// MARK: - Errors

public sealed class ApiException(int status, string message) : Exception(
    message.Length > 0 ? message : status > 0 ? $"The server returned HTTP {status}." : "The server sent a response this app could not read.")
{
    public int Status { get; } = status;

    public static ApiException Malformed => new(0, "");

    /// <summary>
    /// The bucket refused the bytes. Deliberately carries no status: <see cref="IsNotFound"/> is a
    /// statement about the *portal's* row, and Cloud Storage answering 404 for a signed URL says
    /// nothing about whether that row exists. Conflating the two had a refused PUT read as "the row
    /// is gone" and mint a second row for a file that already had one — the fork this client exists
    /// to prevent.
    /// </summary>
    public static ApiException UploadRejected(int status) => new(0, $"Cloud Storage refused the upload (HTTP {status}).");

    /// <summary>
    /// The distinction the engine acts on: gone means remove the item, everything else means
    /// report a transient failure and retry.
    /// </summary>
    public bool IsNotFound => Status == 404;
}

// MARK: - Destination (Mac/Shared/ItemIdentity.swift)

/// <summary>
/// Where a read or a write is aimed. Two shapes because the tree has two kinds of address, not
/// because it has two trees: everything is a row and carries an id, except the top, which is what
/// the empty path means. The server takes either and resolves both to the same directory.
/// </summary>
public readonly struct Destination
{
    readonly string? _id;
    Destination(string? id) => _id = id;

    public static Destination Root => default;
    public static Destination Item(string id) => new(id);

    /// <summary>
    /// A request body naming this destination, plus whatever else the call carries. The root sends
    /// an empty path rather than nothing at all: a restore reads the presence of either field as
    /// "the caller said where this should land".
    /// </summary>
    public Dictionary<string, object?> Body(Dictionary<string, object?>? extra = null)
    {
        var body = extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);
        if (_id is null) body["path"] = Array.Empty<string>();
        else body["parent"] = _id;
        return body;
    }

    /// <summary>The same, as a query string for the reads. Empty for the root: no <c>item</c> is what the server walks from the top for.</summary>
    public string Query => _id is null ? "" : "?item=" + Uri.EscapeDataString(_id);

    public override string ToString() => _id ?? "/";
}

// MARK: - Client

/// <summary>
/// Everything this app asks of the portal — the port of Mac/Shared/HelmsleyAPI.swift. It reaches
/// the engine through <see cref="HelmsleyRemoteStore"/>: the reads for population and hydration,
/// and the writes for the close, rename and delete callbacks <c>LocalChanges</c> maps onto them.
///
/// Two load-bearing properties, easy to lose in translation:
/// <list type="bullet">
/// <item>Bytes never go through the portal. A download is answered with a 302 into Cloud Storage
/// and uploads PUT to a signed URL — App Engine caps a request at 32MB against a 500MB file limit,
/// so proxying is not a style choice that could be reversed later.</item>
/// <item><c>Authorization</c> must be stripped on any cross-origin redirect. <c>HttpClient</c>
/// follows redirects by replaying headers, which would hand the portal's bearer token to Google —
/// so auto-redirect is off and <see cref="Follow"/> takes each hop deliberately.</item>
/// </list>
/// </summary>
public sealed class HelmsleyApi
{
    public static readonly HelmsleyApi Shared = new();

    // Everything hangs off api/files.
    static readonly Uri Base = new(Configuration.BaseUri, "api/files/");

    // No total-elapsed timeout: HttpClient's default hundred seconds covers the body as well as
    // the handshake, which against a 500MB file limit is a cap on how large a file may be opened at
    // all rather than a guard against a stall. A connection that stops delivering is what the OS
    // times out. PooledConnectionLifetime is for the other end of the same run: this process is
    // meant to stay up for days, and a pooled connection would otherwise go on resolving to an
    // address the portal has moved off.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // MARK: Reads

    /// <summary>One directory, named by the id of the folder or — for the top of the tree, which has no id — by nothing.</summary>
    public Task<Listing> List(Destination destination) =>
        Get<Listing>("list" + destination.Query);

    /// <summary>
    /// One row by id — the only lookup that answers for a directory as well as a file, and the
    /// only one that says where the item sits and whether it is in the bin.
    /// </summary>
    public async Task<RemoteFile> Item(string id) =>
        (await Get<ItemWrapper>(ItemPath(id)).ConfigureAwait(false)).Item;

    /// <summary>
    /// What the bin holds — the top of each thing thrown away, not everything marked: a directory
    /// takes its contents with it. Ported ahead of a caller: Windows has no view of the bin yet,
    /// so a deleted item is recoverable only from the portal.
    /// </summary>
    public async Task<IReadOnlyList<RemoteFile>> Trashed() =>
        (await Get<ItemsWrapper>("trash").ConfigureAwait(false)).Items;

    public async Task<Admin> Whoami() =>
        (await Get<AdminWrapper>("whoami").ConfigureAwait(false)).Admin;

    /// <summary>
    /// A file's bytes. The request is answered with a redirect into Cloud Storage, so the transfer
    /// is between this machine and the bucket; the portal never sees the bytes at all.
    /// </summary>
    public async Task<Stream> DownloadContents(string id)
    {
        var response = await Follow(() => Authorised(HttpMethod.Get, DocumentPath(id) + "/content")).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                throw new ApiException((int)response.StatusCode, $"Could not download file {id}.");
            return new Body(response, await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The bytes as they arrive, holding the response open until the reader is done — the port of
    /// the Mac's download-to-a-temporary-file, for the same reason it exists there: files run to
    /// hundreds of megabytes and buffering one whole on a filter callback thread is a large-object
    /// allocation per open.
    /// </summary>
    sealed class Body(HttpResponseMessage response, Stream body) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => body.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            body.ReadAsync(buffer, cancel);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { body.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // MARK: Writes

    /// <summary>
    /// Files a new file into <paramref name="destination"/>: a ticket for the folder it is landing
    /// in, the bytes, and a finalise that writes the row.
    /// </summary>
    public async Task<RemoteFile> Upload(Destination destination, string filename, string? mime, string filePath)
    {
        var uploadId = await Stage(destination.Body(), mime, filePath).ConfigureAwait(false);
        return await Row("finalise", destination.Body(new() { ["uploadId"] = uploadId, ["filename"] = filename }));
    }

    /// <summary>
    /// Replaces one file's bytes under the same row — a save rather than a drop into a folder. The
    /// row keeps its id; what changes is its content hash, which is its version, and its
    /// <c>updated_at</c>, which is its Date Modified.
    /// </summary>
    public async Task<RemoteFile> ReplaceContents(string id, string? mime, string filePath)
    {
        var uploadId = await Stage(new() { ["replaces"] = id }, mime, filePath).ConfigureAwait(false);
        return await Row(DocumentPath(id) + "/content", new() { ["uploadId"] = uploadId });
    }

    /// <summary>What the portal answers a request for somewhere to put bytes with.</summary>
    sealed class Ticket
    {
        [JsonPropertyName("uploadId")] public string UploadId { get; set; } = "";
        [JsonPropertyName("uploadUrl")] public string UploadUrl { get; set; } = "";
        [JsonPropertyName("contentType")] public string ContentType { get; set; } = "";
        [JsonPropertyName("maxBytes")] public long MaxBytes { get; set; }
    }

    /// <summary>
    /// The first two steps of a drop and of a save alike: ask where the bytes go, then PUT them
    /// there. <paramref name="target"/> is what the ticket is for — the folder a drop lands in, or
    /// the file a save replaces — and is what the portal checks before a byte moves.
    /// </summary>
    async Task<string> Stage(Dictionary<string, object?> target, string? mime, string filePath)
    {
        // Null rather than a missing key, so the body says outright that this transfer declares no
        // content type. Either way the portal falls back to octet-stream.
        target["contentType"] = mime;
        var ticket = await Post<Ticket>("upload-ticket", target).ConfigureAwait(false);

        await using var stream = File.OpenRead(filePath);
        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl);
        request.Content = new StreamContent(stream);
        // Both headers were signed into the URL, so they must be sent back exactly: Cloud Storage
        // recomputes the signature over them and refuses the PUT if either differs. No
        // Authorization header of ours goes to the bucket — the signature is the whole credential.
        // Added rather than parsed: MediaTypeHeaderValue re-serialises what it reads — spacing,
        // quoting, parameter order — and Cloud Storage recomputes the signature over the exact
        // bytes. It also throws outright on the empty string a ticket may legitimately carry.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", ticket.ContentType);
        request.Headers.TryAddWithoutValidation("x-goog-content-length-range", $"0,{ticket.MaxBytes}");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw ApiException.UploadRejected((int)response.StatusCode);
        return ticket.UploadId;
    }

    /// <summary>
    /// The last step of both, which answers with the row the bytes landed on — read back after the
    /// write, so it describes the file as it now stands rather than as the request left it.
    /// </summary>
    async Task<RemoteFile> Row(string path, Dictionary<string, object?> body) =>
        (await Post<FileWrapper>(path, body).ConfigureAwait(false)).File;

    /// <summary>
    /// The permanent delete. Ported for completeness and deliberately unreachable from the engine:
    /// every local delete maps to the bin instead, Shift+Delete included, because nothing local
    /// should be able to destroy the only copy of the bytes.
    /// </summary>
    public Task Delete(string id) => SendExpectingNothing(HttpMethod.Delete, DocumentPath(id), null);

    // MARK: Structure

    /// <summary>
    /// Makes a directory in <paramref name="destination"/>. A name already in use is numbered
    /// rather than refused — which is what a filesystem does — so the folder to show is the one
    /// that comes back, not the one that was asked for.
    /// </summary>
    public async Task<RemoteFolder> CreateFolder(Destination destination, string name) =>
        (await Post<FolderWrapper>("folders", destination.Body(new() { ["name"] = name })).ConfigureAwait(false)).Folder;

    /// <summary>
    /// Renames one item, and answers the name it now has. Unlike a move, a taken name is refused
    /// rather than numbered: a rename is someone typing a name they mean.
    /// </summary>
    public async Task<string> Rename(string id, string name) =>
        (await Post<Named>(ItemPath(id, "rename"), new() { ["name"] = name }).ConfigureAwait(false)).Name;

    /// <summary>
    /// Moves one item into <paramref name="destination"/>, and answers the name it landed under —
    /// not always the name it left with, since a collision in the target is numbered.
    /// </summary>
    public async Task<string> Move(string id, Destination destination) =>
        (await Post<Named>(ItemPath(id, "move"), destination.Body()).ConfigureAwait(false)).Name;

    /// <summary>
    /// Into the bin. The bytes stay and the row keeps the folder it was in, which is what makes
    /// putting it back a matter of undoing this rather than of remembering where it came from.
    /// </summary>
    public Task Trash(string id) => SendExpectingNothing(HttpMethod.Post, ItemPath(id, "trash"), new Dictionary<string, object?>());

    /// <summary>
    /// Out again — into <paramref name="destination"/> where one is named, and back where it was
    /// otherwise. The name may come back numbered: while the row sat in the bin its name was not
    /// in use, so something else may have taken it in the meantime.
    /// </summary>
    public async Task<string> Restore(string id, Destination? destination) =>
        (await Post<Named>(ItemPath(id, "restore"), destination?.Body() ?? new()).ConfigureAwait(false)).Name;

    // MARK: Plumbing

    sealed class ItemWrapper { [JsonPropertyName("item")] public RemoteFile Item { get; set; } = new(); }
    sealed class ItemsWrapper { [JsonPropertyName("items")] public List<RemoteFile> Items { get; set; } = new(); }
    sealed class AdminWrapper { [JsonPropertyName("admin")] public Admin Admin { get; set; } = new(); }
    sealed class FileWrapper { [JsonPropertyName("file")] public RemoteFile File { get; set; } = new(); }
    sealed class FolderWrapper { [JsonPropertyName("folder")] public RemoteFolder Folder { get; set; } = new(); }

    /// <summary>What a rename, a move and a restore all answer with: the name the item ended up under.</summary>
    sealed class Named { [JsonPropertyName("name")] public string Name { get; set; } = ""; }

    // Both take the id escaped rather than trusted to be URL-safe: an identifier is whatever the
    // server chose to make it.

    /// <summary>
    /// The bytes, and the permanent delete. Still spelled <c>documents</c> on the server, from when
    /// this client served the document table — the route is the route, and renaming it would break
    /// every installed build.
    /// </summary>
    static string DocumentPath(string id) => "documents/" + Uri.EscapeDataString(id);

    static string ItemPath(string id) => "items/" + Uri.EscapeDataString(id);
    static string ItemPath(string id, string action) => ItemPath(id) + "/" + action;

    async Task<T> Get<T>(string path) => await Send<T>(HttpMethod.Get, path, null).ConfigureAwait(false);

    async Task<T> Post<T>(string path, Dictionary<string, object?> body) => await Send<T>(HttpMethod.Post, path, body).ConfigureAwait(false);

    async Task<T> Send<T>(HttpMethod method, string path, Dictionary<string, object?>? body)
    {
        using var response = await Follow(() => Authorised(method, path, body)).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        ThrowOnFailure(method, path, response, data);
        try
        {
            return JsonSerializer.Deserialize<T>(data) ?? throw ApiException.Malformed;
        }
        catch (JsonException)
        {
            throw ApiException.Malformed;
        }
    }

    async Task SendExpectingNothing(HttpMethod method, string path, Dictionary<string, object?>? body)
    {
        using var response = await Follow(() => Authorised(method, path, body)).ConfigureAwait(false);
        ThrowOnFailure(method, path, response, await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    }

    static void ThrowOnFailure(HttpMethod method, string path, HttpResponseMessage response, byte[] data)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        var message = ServerMessage(data) ?? "";
        Console.Error.WriteLine($"{method} {path} -> {status} {message}");
        throw new ApiException(status, message);
    }

    async Task<HttpRequestMessage> Authorised(HttpMethod method, string path, Dictionary<string, object?>? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(Base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenProvider.Shared.AccessToken().ConfigureAwait(false));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// Sends a request and follows any redirect itself — the port of the Mac side's
    /// <c>RedirectSanitiser</c>. Each hop is a fresh GET carrying none of the original headers, so
    /// the bearer token stops at the origin it was minted for; the signed URL is the only
    /// credential the bucket wants anyway. Origin is scheme as well as host, case-insensitively:
    /// an https → http redirect back to the portal would otherwise put the token on the wire.
    /// </summary>
    const int MaxHops = 5;

    static async Task<HttpResponseMessage> Follow(Func<Task<HttpRequestMessage>> make)
    {
        using var first = await make().ConfigureAwait(false);
        var origin = first.RequestUri!;
        HttpResponseMessage? response =
            await Http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        try
        {
            for (var hop = 0; ; hop++)
            {
                // Tested before the count, so the hop that finally answers is the answer rather
                // than one redirect too many.
                if (response.StatusCode is not (>= (HttpStatusCode)300 and < (HttpStatusCode)400)) return response;
                if (hop == MaxHops) throw new ApiException(0, "Too many redirects.");

                var location = response.Headers.Location
                    ?? throw new ApiException((int)response.StatusCode, "The server redirected without saying where.");
                var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
                if (target.Scheme != Uri.UriSchemeHttps)
                    throw new ApiException(0, $"The server redirected to an insecure address ({target.Scheme}).");

                response.Dispose();
                response = null;

                // Origin is scheme, host *and* port: a service sharing the portal's hostname on
                // another port is as much a different party as one on another name, and it is the
                // bearer token that would be handed to it.
                var sameOrigin = string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(target.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
                    && target.Port == origin.Port;
                using var next = new HttpRequestMessage(HttpMethod.Get, target);
                if (sameOrigin)
                    next.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", await TokenProvider.Shared.AccessToken().ConfigureAwait(false));
                else
                    // Logged because it is a security property nobody can otherwise observe: without
                    // this line there is no way to tell a stripped redirect from one that quietly
                    // carried the token on.
                    Console.WriteLine($"redirect to {target.Host} — Authorization stripped");
                response = await Http.SendAsync(next, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
        }
        catch
        {
            // A response left undisposed on the way out is a pooled connection held open for as
            // long as it takes the GC to notice — and every download that fails this way costs one.
            response?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The portal answers every refusal with <c>{ error: "..." }</c>, and that sentence is written
    /// to be read by a person — so it is what gets shown rather than a status code.
    /// </summary>
    static string? ServerMessage(byte[] data)
    {
        try
        {
            using var parsed = JsonDocument.Parse(data);
            return parsed.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
