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
public sealed record PlaceholderState(string Id, bool InSync, bool HasModifiedBytes, bool Pinned = false)
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
    const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);
    const int ERROR_CANNOT_BREAK_OPLOCK = unchecked((int)0x80070322);

    /// <summary>
    /// Everything that pounces on a fresh entry — the search indexer, the antimalware scan —
    /// holds handles that live for moments, and a write can lose the race to any of them. A short
    /// retry is the difference between an update that works and one that fails whenever anything
    /// else looks. The exclusive open a dehydrate needs loses the same race with a different
    /// word for it: the oplock it asks for cannot be granted over a handle somebody else holds.
    /// </summary>
    static void SharingRetries(Action attempt)
    {
        for (var tries = 0; ; tries++)
        {
            try { attempt(); return; }
            catch (COMException e) when (tries < 5
                && e.HResult is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION or ERROR_CANNOT_BREAK_OPLOCK)
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
                // Something already answers to the name. If it is a placeholder carrying this row,
                // refresh it. If it is a placeholder carrying *another* row — the name's row was
                // trashed and a replacement uploaded under the same name, or a local create was
                // minted just as a remote one claimed the name — it changes identity, and its
                // bytes have to go with the old one: re-stamped hydrated, it would serve the
                // previous row's content under the new row's id and version, and no later pass
                // would see anything to correct. If it is an ordinary file, leave it: the pass
                // that asked for this create decides what a file wearing a row's name means.
                var path = Path.Combine(directory, item.Name);
                try
                {
                    if (TryGetState(path) is { } standing)
                    {
                        var another = !string.Equals(standing.Id, item.Id, StringComparison.Ordinal);
                        if (another) Console.WriteLine($"~ {path}: {standing.Id} gives way to {item.Id}");
                        Update(path, item, dehydrate: another && !item.IsFolder);
                    }
                }
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
        // A pinned placeholder will not give its bytes up (ERROR_CLOUD_FILE_PINNED), and a changed
        // version has to drop them all the same: they are not the portal's any more. The pin is
        // lifted for the update and put straight back, and it is the pin going back on — an
        // attribute change, which Mirror hears — that fetches the new bytes.
        var pinned = dehydrate && !item.IsFolder && PinOf(path) == Pin.Pinned;
        if (pinned) SetPinned(path, false);
        try { SharingRetries(() => applied = UpdateOnce(path, item, dehydrate)); }
        finally { if (pinned) SetPinned(path, true); }
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
    /// <param name="onDemand">
    /// For a directory only: whether the filter should still ask for its entries the first time
    /// anything looks inside. False for a folder the user made here, whose contents are local
    /// by definition; true for a directory adopted as one of the portal's — a leftover of a tree
    /// removal — which may hold only what the removal could not take, with the rest still in the
    /// portal. Converted without it, a directory counts as fully populated and nothing would
    /// ever fetch the rest.
    /// </param>
    public static void Convert(string path, string id, bool onDemand = false) => SharingRetries(() =>
    {
        using var handle = ProtectedHandle.Open(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
        var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
        if (onDemand) flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION;
        fixed (char* identity = id)
        {
            PInvoke.CfConvertToPlaceholder(handle.Win32Handle,
                identity, (uint)(id.Length * sizeof(char)),
                flags, null).ThrowOnFailure();
        }
    });

    /// <summary>
    /// Puts an emptied directory back on demand, so the filter asks the populator the next time
    /// anything looks inside it — which is what a prune (<see cref="Mirror"/>) turns a stale folder
    /// into. Only ever for a directory holding nothing: one with entries left on demand makes the
    /// provider the authority for every name lookup under it, which PORTING.md records the cost of.
    /// Answers false for a directory that is no placeholder, and leaves it alone.
    /// </summary>
    public static bool EnableOnDemand(string directory)
    {
        try
        {
            SharingRetries(() =>
            {
                using var handle = ProtectedHandle.Open(directory, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
                PInvoke.CfUpdatePlaceholder(handle.Win32Handle, null, null, 0, null, 0,
                    CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION, null).ThrowOnFailure();
            });
            return true;
        }
        catch (COMException e) when (e.HResult == ERROR_NOT_A_CLOUD_FILE)
        {
            return false;
        }
    }

    // MARK: - Pin state: the user's word on where the bytes should be
    //
    // "Always keep on this device" and "Free up space" in Explorer, or attrib +P / +U from a
    // console, set a pin state on the placeholder — and nothing more. The platform does not fetch
    // a pinned file or drop an unpinned one; it records the intent, as two file attributes, and
    // the provider is expected to act on it. Which is what Mirror does, through these.

    /// <summary>What the user has said about an entry's bytes, read off its attributes: no handle, no filter call.</summary>
    public enum Pin { Unspecified, Pinned, Unpinned }

    // FILE_ATTRIBUTE_PINNED and FILE_ATTRIBUTE_UNPINNED, which the platform keeps in step with the
    // placeholder's pin state. Not among the FileAttributes .NET names.
    const FileAttributes AttributePinned = (FileAttributes)0x00080000;
    const FileAttributes AttributeUnpinned = (FileAttributes)0x00100000;

    public static Pin PinOf(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & AttributePinned) != 0) return Pin.Pinned;
            if ((attributes & AttributeUnpinned) != 0) return Pin.Unpinned;
            return Pin.Unspecified;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Pin.Unspecified;
        }
    }

    /// <summary>
    /// Says of an entry what the user said of its folder: a file arriving in a pinned folder is
    /// pinned itself, so Explorer's tick and the prune's exemption both read the same way for it
    /// as for the ones the user pinned by hand.
    /// </summary>
    public static void SetPinned(string path, bool pinned) => SharingRetries(() =>
    {
        using var handle = ProtectedHandle.Open(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
        PInvoke.CfSetPinState(handle.Win32Handle,
            pinned ? CF_PIN_STATE.CF_PIN_STATE_PINNED : CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, null).ThrowOnFailure();
    });

    /// <summary>
    /// Fetches a placeholder's bytes in full, from this process: the platform routes the request
    /// to the provider's own FETCH_DATA callback exactly as it would a foreign read, so this is
    /// <see cref="Hydrator"/> at work, asked for by the provider rather than by a user's open.
    /// A plain read handle, not an oplocked one: the call wants only READ_DATA of it, and nothing
    /// here is changed under the caller that an oplock would be guarding.
    /// </summary>
    public static void Hydrate(string path) => SharingRetries(() =>
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        PInvoke.CfHydratePlaceholder((HANDLE)file.SafeFileHandle.DangerousGetHandle(), 0, -1,
            CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE, null).ThrowOnFailure();
    });

    /// <summary>
    /// Drops a placeholder's bytes back to the bucket, which is what "Free up space" asks for.
    /// Under the exclusive handle the platform documents for it and does not check, and never for
    /// a placeholder holding a save the portal has not taken: those bytes exist nowhere else, and
    /// the call answers false rather than lose them. In-sync is set first where only metadata
    /// motion has cleared it — a rename does, under TRACK_ALL — because the platform refuses to
    /// dehydrate an out-of-sync placeholder however clean its bytes are.
    /// </summary>
    public static bool Dehydrate(string path)
    {
        var freed = false;
        SharingRetries(() =>
        {
            using var handle = ProtectedHandle.Open(path,
                CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS);
            var state = ReadState(handle);
            if (state.DataDirty) return;
            if (!state.InSync)
                PInvoke.CfSetInSyncState(handle.Win32Handle, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                    CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null).ThrowOnFailure();
            PInvoke.CfDehydratePlaceholder(handle.Win32Handle, 0, -1,
                CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null).ThrowOnFailure();
            freed = true;
        });
        return freed;
    }

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
        // "Always keep on this device", which the user asked for and a prune must honour.
        var pinned = *(int*)(buffer + 32) == (int)CF_PIN_STATE.CF_PIN_STATE_PINNED;
        var inSync = *(int*)(buffer + 36) == (int)CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
        // Clamped against what was actually written rather than trusted: the length is a field
        // inside the buffer being described, and reading past the buffer on its word is how a
        // stack read becomes a row id sent to the portal.
        var identityLength = Math.Min(*(uint*)(buffer + 56), returned - 60);
        var id = new string((char*)(buffer + 60), 0, (int)(identityLength / sizeof(char)));
        return new PlaceholderState(id, inSync, modified > 0, pinned);
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
