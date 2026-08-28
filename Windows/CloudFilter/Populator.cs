using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Answers FETCH_PLACEHOLDERS: something looked inside a directory whose entries have never been
/// created. The filter names the directory by the identity stamped on its placeholder — the portal
/// folder id, or nothing at all for the sync root — and this lists that one folder and hands its
/// entries back through CfExecute. It is the other half of the mirror's economy: the sync pass
/// only re-lists folders that have been looked at, and this is what "looked at" means.
///
/// The listing also lands in the snapshot, so the folder is materialised and polled from then on.
/// </summary>
public static unsafe class Populator
{
    internal static IRemoteStore? Store;
    internal static Mirror? Mirror;

    internal static void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        string? folderId = null;
        if (info->FileIdentity is not null && info->FileIdentityLength > 0)
            folderId = new string((char*)info->FileIdentity, 0, (int)(info->FileIdentityLength / sizeof(char)));
        if (LocalChanges.Trace) Console.WriteLine($"  [fetch-placeholders] {folderId ?? "/"}");

        IReadOnlyList<RemoteItem> listing;
        try
        {
            // Blocking the callback is the design: whoever opened the folder is waiting on this
            // listing, and Explorer shows them the wait.
            listing = Store!.List(folderId).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // STATUS_UNSUCCESSFUL, and crucially no "fully populated" mark: the next look at the
            // folder asks again, which is the retry.
            Console.Error.WriteLine($"population of {folderId ?? "/"} failed: {e.Message}");
            Transfer(info, null, 0, unchecked((int)0xC0000001));
            return;
        }

        // The names and identities must stay pinned until CfExecute has copied the entries out.
        var natives = new IntPtr[listing.Count * 2];
        try
        {
            var entries = new CF_PLACEHOLDER_CREATE_INFO[listing.Count];
            for (var i = 0; i < listing.Count; i++)
            {
                natives[i * 2] = Marshal.StringToHGlobalUni(listing[i].Name);
                natives[i * 2 + 1] = Marshal.StringToHGlobalUni(listing[i].Id);
                entries[i] = Placeholders.Describe(listing[i], (char*)natives[i * 2], (char*)natives[i * 2 + 1]);
            }

            fixed (CF_PLACEHOLDER_CREATE_INFO* array = entries)
            {
                Transfer(info, array, listing.Count, 0);
            }
        }
        finally
        {
            foreach (var native in natives)
                if (native != IntPtr.Zero) Marshal.FreeHGlobal(native);
        }

        Mirror?.NoteListed(folderId, listing);
        Console.WriteLine($"= {folderId ?? "/"} populated ({listing.Count} entries)");
    }

    internal static void OnCancelFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // A listing is one round trip; there is no transfer worth abandoning midway.
    }

    static void Transfer(CF_CALLBACK_INFO* info, CF_PLACEHOLDER_CREATE_INFO* entries, int count, int status)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };

        var parameters = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        parameters.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS(status);
        parameters.Anonymous.TransferPlaceholders.PlaceholderArray = entries;
        parameters.Anonymous.TransferPlaceholders.PlaceholderCount = (uint)count;
        parameters.Anonymous.TransferPlaceholders.PlaceholderTotalCount = count;
        if (status == 0)
        {
            // The whole listing went in one transfer, so the directory is now fully populated and
            // the filter must not ask again — remote changes are the poll's to deliver from here.
            parameters.Anonymous.TransferPlaceholders.Flags =
                CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;
        }

        PInvoke.CfExecute(in op, ref parameters).ThrowOnFailure();
    }
}
