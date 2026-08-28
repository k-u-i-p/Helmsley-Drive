using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// The sync root's lifecycle: registered once per machine, connected once per run. Registration is
/// what makes a folder cloud-backed in Explorer's eyes; connection is what routes its callbacks —
/// hydration above all — to this process.
/// </summary>
/// <summary>A live connection to the filter, opaque outside the engine.</summary>
public sealed class SyncConnection
{
    internal CF_CONNECTION_KEY Key;
}

public static unsafe class SyncRoot
{
    // One provider identity, forever. Windows keys the sync root's registration on it.
    static readonly Guid ProviderId = new("f2c3a7e4-8b1d-4c5e-9f6a-0d7b8c9e1a2b");

    public static void Register(string path)
    {
        fixed (char* name = "Helmsley Drive")
        fixed (char* version = "0.1")
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = name,
                ProviderVersion = version,
                ProviderId = ProviderId,
            };

            // Full hydration: a file is materialised in one fetch, not progressively — the portal
            // serves whole objects out of the bucket, so there is nothing to stream partially.
            // Always-full population: the engine creates every placeholder itself, so the system
            // never asks for a directory to be populated on demand.
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_ALL,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            };
            policies.Hydration.Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL;
            policies.Population.Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL;

            PInvoke.CfRegisterSyncRoot(path, in registration, in policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE).ThrowOnFailure();
        }
    }

    public static void Unregister(string path) => PInvoke.CfUnregisterSyncRoot(path).ThrowOnFailure();

    // The filter calls through these delegates for as long as the root is connected; a static
    // reference is what keeps the GC from collecting them out from under it.
    static CF_CALLBACK_REGISTRATION[]? _callbacks;

    public static SyncConnection Connect(string path, IRemoteStore store, Mirror mirror)
    {
        Hydrator.Store = store;
        LocalChanges.Mirror = mirror;

        _callbacks = new CF_CALLBACK_REGISTRATION[]
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
        };

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(path, _callbacks, null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO |
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            &key).ThrowOnFailure();
        return new SyncConnection { Key = key };
    }

    public static void Disconnect(SyncConnection connection) =>
        PInvoke.CfDisconnectSyncRoot(connection.Key).ThrowOnFailure();
}
