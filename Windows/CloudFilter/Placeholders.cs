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
public sealed record PlaceholderState(string Id, bool InSync, bool HasModifiedBytes)
{
    /// <summary>Local bytes the portal has not seen. The one state that means an upload.</summary>
    public bool DataDirty => HasModifiedBytes && !InSync;
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

    /// <summary>
    /// One entry as the filter wants it described — for CfCreatePlaceholders and for a
    /// TRANSFER_PLACEHOLDERS population alike. A folder is left unpopulated: its own entries are
    /// fetched the first time anything looks inside it, which is the whole economy of the mirror.
    /// The caller owns the pinned name and identity for as long as the struct is in use.
    /// </summary>
    internal static CF_PLACEHOLDER_CREATE_INFO Describe(RemoteItem item, char* name, char* identity)
    {
        var info = new CF_PLACEHOLDER_CREATE_INFO
        {
            RelativeFileName = name,
            // The identity blob is the UTF-16 bytes of the row id — what FETCH_DATA and
            // FETCH_PLACEHOLDERS get handed back, and all either needs to name it to the portal.
            FileIdentity = identity,
            FileIdentityLength = (uint)(item.Id.Length * sizeof(char)),
            Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
        };
        info.FsMetadata = MetadataFor(item);
        return info;
    }

    /// <summary>The size and instants an entry wears on disk, from the row the portal described.</summary>
    static CF_FS_METADATA MetadataFor(RemoteItem item)
    {
        var metadata = new CF_FS_METADATA { FileSize = item.IsFolder ? 0 : item.Size };
        metadata.BasicInfo.CreationTime = FileTime(item.Created);
        metadata.BasicInfo.LastWriteTime = FileTime(item.Modified);
        metadata.BasicInfo.LastAccessTime = FileTime(item.Modified);
        metadata.BasicInfo.ChangeTime = FileTime(item.Modified);
        metadata.BasicInfo.FileAttributes = item.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        return metadata;
    }

    /// <summary>
    /// An instant Windows can hold. The portal's own instants are all well inside the range, but a
    /// snapshot written by an older build carries none at all, and the default
    /// <see cref="DateTimeOffset"/> predates the FILETIME epoch by more than a millennium —
    /// <c>ToFileTime</c> throws on it rather than clamping.
    /// </summary>
    static long FileTime(DateTimeOffset at) =>
        at < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch.ToFileTime() : at.ToFileTime();

    /// <summary>
    /// Creates one placeholder, or brings an existing one up to date — which is what a re-run of
    /// the mirror, or a create racing a local save's conversion, turns creation into.
    /// </summary>
    public static void CreateOne(string directory, RemoteItem item)
    {
        fixed (char* dir = directory)
        fixed (char* name = item.Name)
        fixed (char* identity = item.Id)
        {
            var info = Describe(item, name, identity);

            uint processed;
            // The entry's own result is read before the call's, because they are the same failure
            // said twice: with no STOP_ON_ERROR the API answers with the first error it met and
            // carries on, so throwing on the return value first would make the refresh below
            // unreachable — and every re-run over an existing tree would fail every entry.
            var called = PInvoke.CfCreatePlaceholders(dir, &info, 1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);

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
            called.ThrowOnFailure();
            info.Result.ThrowOnFailure();
        }
    }

    /// <summary>
    /// Rewrites a placeholder's metadata and identity from the portal row, optionally dropping its
    /// bytes — which is what a changed content version means: what is cached is no longer what the
    /// portal holds, and the next open should fetch rather than trust it.
    ///
    /// A placeholder holding an unsynced local write is left alone and the call answers false: those
    /// bytes are the newest anywhere, and the close-completion upload is what reconciles them. The
    /// caller has to know, or it records a version the disk never took.
    /// </summary>
    public static bool Update(string path, RemoteItem item, bool dehydrate)
    {
        var applied = false;
        SharingRetries(() => applied = UpdateOnce(path, item, dehydrate));
        return applied;
    }

    static bool UpdateOnce(string path, RemoteItem item, bool dehydrate)
    {
        // Dehydration is the one operation the platform asks for an exclusive oplock on, and does
        // not check: "the caller must acquire an exclusive handle when specifying this flag or data
        // corruptions can occur". Everything else here coexists with the indexer, which is what the
        // rest of this file is built around.
        var wants = dehydrate && !item.IsFolder
            ? CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS
            : CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS;

        using var handle = ProtectedHandle.Open(path, wants);
        if (ReadState(handle).DataDirty) return false;

        var metadata = MetadataFor(item);
        var flags = CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC;
        if (dehydrate && !item.IsFolder) flags |= CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE;

        // Not CF_UPDATE_FLAG_VERIFY_IN_SYNC, which reads like the right guard and is not. It
        // refuses the update unless the placeholder is in sync *now* — and under TRACK_ALL a rename
        // clears in-sync as readily as a write does, so a folder listing that renamed a file and
        // changed its bytes in the same pass would have its dehydrate refused, leave the old bytes
        // on disk, and then read the size disagreement as a local edit to push back. The DataDirty
        // check above is the precise version of the same question, and it is asked under this same
        // oplock, which is what makes it a guard rather than a guess.
        fixed (char* identity = item.Id)
        {
            PInvoke.CfUpdatePlaceholder(handle.Win32Handle, &metadata,
                identity, (uint)(item.Id.Length * sizeof(char)),
                null, 0, flags, null).ThrowOnFailure();
            return true;
        }
    }

    /// <summary>
    /// Stamps a row's identity on an ordinary file or directory, making it a placeholder — the
    /// last step of a local create, once the portal has answered with the row it minted. The bytes
    /// stay on disk; they are the portal's bytes now too, which is what in-sync asserts.
    /// </summary>
    public static void Convert(string path, string id) => SharingRetries(() =>
    {
        using var handle = ProtectedHandle.Open(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
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
        using var handle = ProtectedHandle.Open(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
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
            using var handle = ProtectedHandle.Open(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE);
            return ReadState(handle);
        }
        catch (COMException e) when (e.HResult == ERROR_NOT_A_CLOUD_FILE)
        {
            return null;
        }
    }

    // The identity blob the platform will carry is capped at 4KB; the fixed part of the standard
    // info runs to 60 bytes, so this is the largest answer there can be and the query never has to
    // be made twice.
    const int StandardInfoBytes = 60 + 4096;

    static PlaceholderState ReadState(ProtectedHandle handle)
    {
        // CF_PLACEHOLDER_STANDARD_INFO, read at documented offsets rather than through the
        // generated struct, whose trailing variable-length identity does not project into C#
        // usefully: OnDiskDataSize 0, ValidatedDataSize 8, ModifiedDataSize 16, PropertiesSize 24,
        // PinState 32, InSyncState 36, FileId 40, SyncRootFileId 48, FileIdentityLength 56,
        // FileIdentity from 60 — the UTF-16 bytes CreateOne stamped.
        var buffer = stackalloc byte[StandardInfoBytes];
        uint returned;
        PInvoke.CfGetPlaceholderInfo(handle.Win32Handle,
            CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD,
            buffer, StandardInfoBytes, &returned).ThrowOnFailure();
        if (returned < 60) throw new InvalidDataException("The placeholder's standard info came back short.");

        var modified = *(long*)(buffer + 16);
        var inSync = *(int*)(buffer + 36) == (int)CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
        // Clamped against what was actually written rather than trusted: the length is a field
        // inside the buffer being described, and reading past the buffer on its word is how a
        // stack read becomes a row id sent to the portal.
        var identityLength = Math.Min(*(uint*)(buffer + 56), returned - 60);
        var id = new string((char*)(buffer + 60), 0, (int)(identityLength / sizeof(char)));
        return new PlaceholderState(id, inSync, modified > 0);
    }

    /// <summary>
    /// A handle from CfOpenFileWithOplock and the plain Win32 handle the query and update calls
    /// want of it. The oplock is the point: it is what keeps the entry from changing under an
    /// update.
    ///
    /// Which oplock is the caller's to say. Write access is what almost everything here wants —
    /// the search indexer and the malware scan keep long-lived shared read handles on anything
    /// fresh, and an exclusivity demand loses to every one of them. Dehydration is the exception,
    /// because the platform documents exclusivity as its precondition and does not enforce it.
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

        public static ProtectedHandle Open(string path, CF_OPEN_FILE_FLAGS flags)
        {
            PInvoke.CfOpenFileWithOplock(path, flags, out var handle).ThrowOnFailure();
            var protectedHandle = new ProtectedHandle { _handle = handle };
            try
            {
                protectedHandle.Win32Handle =
                    PInvoke.CfGetWin32HandleFromProtectedHandle((HANDLE)handle.DangerousGetHandle());
            }
            catch
            {
                // Opened but never wrapped: without this the only thing left holding it is the
                // SafeFileHandle's finalizer, which closes the wrong thing and locks the file
                // until the process exits.
                protectedHandle.Dispose();
                throw;
            }
            return protectedHandle;
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
