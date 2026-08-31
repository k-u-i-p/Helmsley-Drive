using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>A live connection to the filter, opaque outside the engine.</summary>
public sealed class SyncConnection
{
    internal CF_CONNECTION_KEY Key;

    // The filter calls through these delegates for as long as this connection is up, and a
    // delegate the GC can reach nothing from is a marshalling stub the filter jumps into after it
    // has been collected. They hang off the connection rather than off a static slot so that a
    // second root — a probe beside the real one, which the registration scheme exists to allow —
    // cannot unroot the first one's simply by connecting.
    internal CF_CALLBACK_REGISTRATION[]? Callbacks;
}

/// <summary>
/// The sync root's lifecycle: registered per user and per path — idempotently, so every mount may
/// ask — and connected once per run. Registration is what makes a folder cloud-backed in
/// Explorer's eyes; connection is what routes its callbacks — hydration above all — to this process.
/// </summary>
public static unsafe class SyncRoot
{
    /// <summary>
    /// A path folded to sixteen hex digits. It is the account leg of the registration id and the
    /// name of that root's snapshot, which is why it lives here rather than in two places: Windows
    /// keys the registration on it, so it is permanent for a given path, and a second copy of the
    /// expression is a second chance to change one of them.
    /// </summary>
    public static string KeyFor(string path) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.Unicode.GetBytes(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant())))[..16];

    /// <summary>
    /// The WinRT registration id, which is what everything is keyed on: the mandated shape is
    /// provider!SID!account, and the account leg is a hash of the root path so a second root — the
    /// harness's, a probe's — registers beside the real one instead of replacing it. Permanent for
    /// a given path, exactly as it once was for the provider GUID the cldapi-only registration used.
    /// </summary>
    static string SyncRootId(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-0-0";
        return $"HelmsleyDrive!{sid}!{KeyFor(path)}";
    }

    /// <summary>
    /// Registers through the shell's storage-provider manager rather than the raw filter: the same
    /// cloud-files plumbing underneath, plus what CfRegisterSyncRoot alone never gave — the drive
    /// as its own entry in Explorer's navigation pane, with the cloud status column beside it.
    /// </summary>
    public static void Register(string path)
    {
        var id = SyncRootId(path);
        var info = new StorageProviderSyncRootInfo
        {
            Id = id,
            Path = StorageFolder.GetFolderFromPathAsync(path).GetAwaiter().GetResult(),
            DisplayNameResource = "Helmsley Drive",
            // A stock cloud-folder glyph until the app has an icon of its own to point at.
            IconResource = "%SystemRoot%\\system32\\imageres.dll,-1043",
            Version = "1.0",
            // What comes back on every callback as SyncRootIdentity. One root per process makes it
            // redundant today and the registration scheme deliberately allows a second, at which
            // point it is the only thing that says which root a callback belongs to.
            Context = CryptographicBuffer.ConvertStringToBinary(id, BinaryStringEncoding.Utf8),
            ShowSiblingsAsGroup = false,
            // Full hydration: a file is materialised in one fetch, not progressively — the portal
            // serves whole objects out of the bucket, so there is nothing to stream partially.
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            // On-demand population: a directory's entries are fetched the first time anything
            // looks inside it (Populator answers FETCH_PLACEHOLDERS), so opening the drive costs
            // nothing rather than a walk of the whole portal. "Full" is the on-demand setting;
            // "AlwaysFull" is the eager one this replaced.
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            // Everything tracked, matching the CF_INSYNC_POLICY_TRACK_ALL the engine was proven
            // against — and the reason a rename clears in-sync, which Mirror compensates for.
            InSyncPolicy =
                StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.FileLastWriteTime |
                StorageProviderInSyncPolicy.FileHiddenAttribute | StorageProviderInSyncPolicy.FileReadOnlyAttribute |
                StorageProviderInSyncPolicy.FileSystemAttribute |
                StorageProviderInSyncPolicy.DirectoryCreationTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime |
                StorageProviderInSyncPolicy.DirectoryHiddenAttribute | StorageProviderInSyncPolicy.DirectoryReadOnlyAttribute |
                StorageProviderInSyncPolicy.DirectorySystemAttribute,
        };

        try
        {
            StorageProviderSyncRootManager.Register(info);
        }
        catch (Exception e) when (e is COMException or ArgumentException or UnauthorizedAccessException)
        {
            // The path may still carry an older build's cldapi-only registration, which the shell
            // manager refuses to sit on top of. Take that one away and register properly. Said out
            // loud, because if the retry fails too this line is the only account of why the first
            // attempt did — and the projection turns some HRESULTs into CLR types rather than a
            // COMException, which is why the filter above is wider than it looks like it needs.
            Console.Error.WriteLine($"registration refused ({e.Message}); retrying without the old filter registration");
            PInvoke.CfUnregisterSyncRoot(path);
            StorageProviderSyncRootManager.Register(info);
        }
    }

    public static void Unregister(string path)
    {
        var unregistered = false;
        try
        {
            StorageProviderSyncRootManager.Unregister(SyncRootId(path));
            unregistered = true;
        }
        catch (Exception e) when (e is COMException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shell unregistration refused ({e.Message}); trying the filter's own");
        }
        // An older build registered with the filter alone; if that is what this path carries,
        // take it away too. Harmless when the shell unregistration above already did the work.
        var legacy = PInvoke.CfUnregisterSyncRoot(path);
        if (!unregistered) legacy.ThrowOnFailure();
    }

    // Every live connection's delegates, kept reachable for exactly as long as the connection is.
    static readonly ConcurrentDictionary<SyncConnection, byte> Live = new();

    public static SyncConnection Connect(string path, IRemoteStore store, Mirror mirror)
    {
        Hydrator.Store = store;
        Populator.Store = store;
        Populator.Mirror = mirror;
        LocalChanges.Mirror = mirror;

        var connection = new SyncConnection
        {
            Callbacks = new CF_CALLBACK_REGISTRATION[]
            {
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                    Callback = Hydrator.OnFetchData,
                },
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
                    Callback = Hydrator.OnCancelFetchData,
                },
                // Population on demand: a directory's first enumeration asks for its entries.
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                    Callback = Populator.OnFetchPlaceholders,
                },
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS,
                    Callback = Populator.OnCancelFetchPlaceholders,
                },
                // The local write path: saves and creates announce themselves as closes, and renames
                // and deletes are held by the filter until LocalChanges answers for the portal.
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,
                    Callback = LocalChanges.OnCloseCompletion,
                },
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME,
                    Callback = LocalChanges.OnRename,
                },
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION,
                    Callback = LocalChanges.OnRenameCompletion,
                },
                new()
                {
                    Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
                    Callback = LocalChanges.OnDelete,
                },
                new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
            },
        };

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(path, connection.Callbacks, null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO |
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            &key).ThrowOnFailure();

        connection.Key = key;
        Live[connection] = 0;
        return connection;
    }

    public static void Disconnect(SyncConnection connection)
    {
        try
        {
            PInvoke.CfDisconnectSyncRoot(connection.Key).ThrowOnFailure();
        }
        finally
        {
            // Only once the filter has been told to stop calling: until then the delegates and the
            // stores behind them are still live targets.
            Live.TryRemove(connection, out _);
            connection.Callbacks = null;
            if (Live.IsEmpty)
            {
                Hydrator.Store = null;
                Populator.Store = null;
                Populator.Mirror = null;
                LocalChanges.Mirror = null;
            }
        }
    }
}
