using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// What a placeholder on disk says about itself: whose row it is, and how it stands. In-sync alone
/// is not a verdict — the platform clears it for a rename as readily as for a write — so whether
/// the *bytes* differ is carried separately, and only the two together mean a save to upload.
/// </summary>
public sealed record PlaceholderState(string Id, bool InSync, bool DataModified)
{
    /// <summary>Local bytes the portal has not seen. The one state that means an upload.</summary>
    public bool DataDirty => DataModified && !InSync;
}

/// <summary>
/// Turns remote listings into placeholders and keeps them true: real directory entries with the
/// right name, size and dates, whose bytes stay in the bucket until <see cref="Hydrator"/> is
/// asked for them — updated in place when the portal's version moves, converted from ordinary
/// files when a local write gives them a row.
/// </summary>
public static unsafe class Placeholders
{
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    const int ERROR_ALREADY_EXISTS = unchecked((int)0x800700B7);
    const int ERROR_NOT_A_CLOUD_FILE = unchecked((int)0x80070178);
    const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);

    /// <summary>
    /// Everything that pounces on a fresh entry — the search indexer, the antimalware scan —
    /// holds handles that live for moments, and a write can lose the race to any of them. A short
    /// retry is the difference between an update that works and one that fails whenever anything
    /// else looks.
    /// </summary>
    static void SharingRetries(Action attempt)
    {
        for (var tries = 0; ; tries++)
        {
            try { attempt(); return; }
            catch (COMException e) when (e.HResult == ERROR_SHARING_VIOLATION && tries < 5)
            {
                Thread.Sleep(200 * (tries + 1));
            }
        }
    }

    public static void Create(string directory, IEnumerable<RemoteItem> items)
    {
        // One placeholder per call: the create-info wants pinned name and identity strings, and
        // pinning a batch of them buys nothing at portal folder sizes.
        foreach (var item in items) CreateOne(directory, item);
    }

    /// <summary>
    /// Creates one placeholder, or brings an existing one up to date — which is what a re-run of
    /// the mirror, or a create racing a local save's conversion, turns creation into.
    /// </summary>
    public static void CreateOne(string directory, RemoteItem item)
    {
        long filetime = item.Modified.ToFileTime();

        fixed (char* dir = directory)
        fixed (char* name = item.Name)
        fixed (char* identity = item.Id)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = name,
                // The identity blob is the UTF-16 bytes of the row id — what FETCH_DATA gets
                // handed back, and all it needs to name the file to the portal.
                FileIdentity = identity,
                FileIdentityLength = (uint)(item.Id.Length * sizeof(char)),
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
            };

            info.FsMetadata.FileSize = item.IsFolder ? 0 : item.Size;
            info.FsMetadata.BasicInfo.CreationTime = filetime;
            info.FsMetadata.BasicInfo.LastWriteTime = filetime;
            info.FsMetadata.BasicInfo.LastAccessTime = filetime;
            info.FsMetadata.BasicInfo.ChangeTime = filetime;
            info.FsMetadata.BasicInfo.FileAttributes = item.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            if (item.IsFolder)
            {
                // The engine fills folders itself (population is always-full), so the filter must
                // not hold this one empty waiting for an on-demand population that never comes.
                info.Flags |= CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
            }

            uint processed;
            PInvoke.CfCreatePlaceholders(dir, &info, 1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed).ThrowOnFailure();

            if (info.Result.Value == ERROR_ALREADY_EXISTS)
            {
                // Something already answers to the name. If it is a placeholder, refresh it —
                // identity included, since a re-run may find entries an older tree stamped. If it
                // is an ordinary file, leave it: a local save is on its way to the portal, and the
                // next pass reconciles whatever the portal made of it.
                try { Update(Path.Combine(directory, item.Name), item, dehydrate: false); }
                catch (COMException e) when (e.HResult == ERROR_NOT_A_CLOUD_FILE) { }
                return;
            }
            info.Result.ThrowOnFailure();
        }
    }

    /// <summary>
    /// Rewrites a placeholder's metadata and identity from the portal row, optionally dropping its
    /// bytes — which is what a changed content version means: what is cached is no longer what the
    /// portal holds, and the next open should fetch rather than trust it.
    ///
    /// A placeholder holding an unsynced local write is left alone: those bytes are the newest
    /// anywhere, and the close-completion upload is what reconciles them.
    /// </summary>
    public static void Update(string path, RemoteItem item, bool dehydrate) =>
        SharingRetries(() => UpdateOnce(path, item, dehydrate));

    static void UpdateOnce(string path, RemoteItem item, bool dehydrate)
    {
        using var handle = ProtectedHandle.Open(path, exclusive: true);
        if (ReadState(handle).DataDirty) return;

        long filetime = item.Modified.ToFileTime();
        var metadata = new CF_FS_METADATA();
        metadata.FileSize = item.IsFolder ? 0 : item.Size;
        metadata.BasicInfo.CreationTime = filetime;
        metadata.BasicInfo.LastWriteTime = filetime;
        metadata.BasicInfo.LastAccessTime = filetime;
        metadata.BasicInfo.ChangeTime = filetime;
        metadata.BasicInfo.FileAttributes = item.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        var flags = CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC;
        if (dehydrate && !item.IsFolder) flags |= CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE;

        fixed (char* identity = item.Id)
        {
            PInvoke.CfUpdatePlaceholder(handle.Win32Handle, &metadata,
                identity, (uint)(item.Id.Length * sizeof(char)),
                null, 0, flags, null).ThrowOnFailure();
        }
    }

    /// <summary>
    /// Stamps a row's identity on an ordinary file or directory, making it a placeholder — the
    /// last step of a local create, once the portal has answered with the row it minted. The bytes
    /// stay on disk; they are the portal's bytes now too, which is what in-sync asserts.
    /// </summary>
    public static void Convert(string path, string id) => SharingRetries(() =>
    {
        using var handle = ProtectedHandle.Open(path, exclusive: true);
        fixed (char* identity = id)
        {
            PInvoke.CfConvertToPlaceholder(handle.Win32Handle,
                identity, (uint)(id.Length * sizeof(char)),
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, null).ThrowOnFailure();
        }
    });

    /// <summary>What a completed upload of local bytes earns the placeholder that held them.</summary>
    public static void MarkInSync(string path) => SharingRetries(() =>
    {
        using var handle = ProtectedHandle.Open(path, exclusive: true);
        PInvoke.CfSetInSyncState(handle.Win32Handle,
            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null).ThrowOnFailure();
    });

    /// <summary>
    /// Whose row this entry is and whether it is in sync — or null for an entry that is no
    /// placeholder at all, which is what a file the user just made looks like.
    /// </summary>
    public static PlaceholderState? TryGetState(string path)
    {
        try
        {
            using var handle = ProtectedHandle.Open(path, exclusive: false);
            return ReadState(handle);
        }
        catch (COMException e) when (e.HResult == ERROR_NOT_A_CLOUD_FILE)
        {
            return null;
        }
    }

    static PlaceholderState ReadState(ProtectedHandle handle)
    {
        // CF_PLACEHOLDER_STANDARD_INFO, read at documented offsets rather than through the
        // generated struct, whose trailing variable-length identity does not project into C#
        // usefully: OnDiskDataSize 0, ValidatedDataSize 8, ModifiedDataSize 16, PropertiesSize 24,
        // PinState 32, InSyncState 36, FileId 40, SyncRootFileId 48, FileIdentityLength 56,
        // FileIdentity from 60 — the UTF-16 bytes CreateOne stamped.
        var buffer = stackalloc byte[512];
        uint returned;
        PInvoke.CfGetPlaceholderInfo(handle.Win32Handle,
            CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD,
            buffer, 512, &returned).ThrowOnFailure();

        var modified = *(long*)(buffer + 16);
        var inSync = *(int*)(buffer + 36) == (int)CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
        var identityLength = *(uint*)(buffer + 56);
        var id = new string((char*)(buffer + 60), 0, (int)(identityLength / sizeof(char)));
        return new PlaceholderState(id, inSync, modified > 0);
    }

    /// <summary>
    /// A handle from CfOpenFileWithOplock and the plain Win32 handle the query and update calls
    /// want of it. The oplock is the point: it is what keeps the entry from changing under an
    /// update, and exclusive is what a write to it needs.
    /// </summary>
    sealed class ProtectedHandle : IDisposable
    {
        // A protected handle is not a file handle but a structure holding one, and only
        // CfCloseHandle takes it apart. CsWin32 hands it out as a plain SafeFileHandle, whose own
        // disposal would CloseHandle the structure pointer — a silent no-op that leaks the real
        // handle inside, and a leaked write handle is a file nothing can touch until the process
        // dies. So it is closed properly here and the SafeFileHandle is told it already happened.
        Microsoft.Win32.SafeHandles.SafeFileHandle? _handle;
        public HANDLE Win32Handle { get; private set; }

        public static ProtectedHandle Open(string path, bool exclusive)
        {
            // Write access rather than an exclusive oplock: the search indexer and the malware
            // scan keep long-lived shared read handles on anything fresh, and an exclusivity
            // demand loses to every one of them. Writing needs none of it.
            var flags = exclusive
                ? CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS
                : CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE;
            PInvoke.CfOpenFileWithOplock(path, flags, out var handle).ThrowOnFailure();
            return new ProtectedHandle
            {
                _handle = handle,
                Win32Handle = PInvoke.CfGetWin32HandleFromProtectedHandle((HANDLE)handle.DangerousGetHandle()),
            };
        }

        public void Dispose()
        {
            if (_handle is null || _handle.IsInvalid) return;
            PInvoke.CfCloseHandle((HANDLE)_handle.DangerousGetHandle());
            _handle.SetHandleAsInvalid();
            _handle = null;
        }
    }
}
