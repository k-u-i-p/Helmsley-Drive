using System.Runtime.InteropServices;
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
/// The listing also lands in the snapshot, so the folder is materialised and polled from then on —
/// but only once the platform has confirmed it took every entry. A transfer with a failed entry in
/// it does not earn the fully-populated mark, so the filter will ask again, and a folder recorded
/// as materialised on the strength of a listing that half-landed would be a folder nothing ever
/// completes.
/// </summary>
public static unsafe class Populator
{
    internal static IRemoteStore? Store;
    internal static Mirror? Mirror;

    internal static void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            var folderId = Callbacks.Identity(info);
            if (LocalChanges.Trace) Console.WriteLine($"  [fetch-placeholders] {folderId ?? "/"}");

            IReadOnlyList<RemoteItem> listing;
            try
            {
                // Blocking the callback is the design: whoever opened the folder is waiting on this
                // listing, and Explorer shows them the wait.
                listing = Store!.ListLocally(folderId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // A refusal, and crucially no "fully populated" mark: the next look at the folder
                // asks again, which is the retry.
                Console.Error.WriteLine($"population of {folderId ?? "/"} failed: {e.Message}");
                Transfer(info, null, 0, 0, Callbacks.STATUS_CLOUD_FILE_UNSUCCESSFUL);
                return;
            }

            // The names and identities must stay pinned until CfExecute has copied the entries out.
            var natives = new IntPtr[listing.Count * 2];
            var entries = new CF_PLACEHOLDER_CREATE_INFO[listing.Count];
            uint created;
            bool taken;
            try
            {
                for (var i = 0; i < listing.Count; i++)
                {
                    natives[i * 2] = Marshal.StringToHGlobalUni(listing[i].Name);
                    natives[i * 2 + 1] = Marshal.StringToHGlobalUni(listing[i].Id);
                    entries[i] = Placeholders.Describe(listing[i], (char*)natives[i * 2], (char*)natives[i * 2 + 1]);
                }

                fixed (CF_PLACEHOLDER_CREATE_INFO* array = entries)
                {
                    taken = Transfer(info, array, listing.Count, listing.Count, 0, out created);
                }
            }
            finally
            {
                foreach (var native in natives)
                    if (native != IntPtr.Zero) Marshal.FreeHGlobal(native);
            }

            if (!taken) return;

            // The fully-populated mark is only honoured when every entry in the transfer was
            // created. One that was not — a name an ordinary file already holds — leaves the
            // directory on demand, so the filter asks again, and saying it is materialised here
            // would tell the poll to keep a folder true that the population never finished.
            var refused = 0;
            var first = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Result.Succeeded) continue;
                if (refused++ == 0) first = entries[i].Result.Value;
            }
            if (created != listing.Count || refused > 0)
            {
                Console.Error.WriteLine(
                    $"population of {folderId ?? "/"} placed {created} of {listing.Count} entries, " +
                    $"{refused} refused (first 0x{first:X8}); leaving the folder on demand");
                return;
            }

            Mirror?.NoteListed(folderId, listing);
            Console.WriteLine($"= {folderId ?? "/"} populated ({listing.Count} entries)");
        }
        catch (Exception e) { Callbacks.Fell("fetch-placeholders", e); }
    }

    internal static void OnCancelFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // A listing is one round trip; there is no transfer worth abandoning midway.
    }

    static bool Transfer(CF_CALLBACK_INFO* info, CF_PLACEHOLDER_CREATE_INFO* entries, int count, int total, int status) =>
        Transfer(info, entries, count, total, status, out _);

    static bool Transfer(
        CF_CALLBACK_INFO* info, CF_PLACEHOLDER_CREATE_INFO* entries, int count, int total, int status, out uint created)
    {
        var operation = Callbacks.OperationOn(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS);

        var parameters = new CF_OPERATION_PARAMETERS();
        parameters.ParamSize = Callbacks.ParamSize(&parameters, &parameters.Anonymous.TransferPlaceholders);
        parameters.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS(status);
        parameters.Anonymous.TransferPlaceholders.PlaceholderArray = entries;
        parameters.Anonymous.TransferPlaceholders.PlaceholderCount = (uint)count;
        parameters.Anonymous.TransferPlaceholders.PlaceholderTotalCount = total;
        if (status == 0)
            // The whole listing goes in one transfer, so the directory can be fully populated and
            // the filter need not ask again — remote changes are the poll's to deliver from here.
            // The platform honours this only if every entry lands, which is checked above.
            parameters.Anonymous.TransferPlaceholders.Flags =
                CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;

        var taken = Callbacks.Executed("transfer-placeholders", in operation, ref parameters);
        created = taken ? parameters.Anonymous.TransferPlaceholders.EntriesProcessed : 0;
        return taken;
    }
}
