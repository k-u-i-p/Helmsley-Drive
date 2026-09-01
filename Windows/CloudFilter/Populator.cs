using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Answers FETCH_PLACEHOLDERS: something looked inside a directory whose entries have never been
/// created. The filter names the directory by the identity stamped on its placeholder — the portal
/// folder id, or nothing at all for the sync root — and this lists that one folder and hands its
/// entries back through CfExecute. It is the first look, and the only one the filter reports:
/// the transfer marks the directory fully populated, and from then on what keeps it true is the
/// mirror's refresh of the folders Explorer is showing (<see cref="Mirror.SetOpen"/>).
///
/// The listing also lands in the snapshot, so a refresh has something to diff against — but only
/// once the platform has confirmed it took every entry. A transfer with a failed entry in it does
/// not earn the fully-populated mark, so the filter will ask again, and a folder recorded on the
/// strength of a listing that half-landed would be a folder nothing ever completes.
///
/// Why the directory is not simply left on demand, so that every look reached here: measured in
/// the harness, an on-demand directory makes the provider the authority for every name lookup
/// under it — hundreds of callbacks for one <c>dir</c>, and "the name of the file cannot be
/// resolved" on a plain create. The mark is the platform's design, not an option.
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
            var directory = LocalChanges.FullPath(info, info->NormalizedPath);
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

            // What the directory already holds is not transferred. The platform honours the
            // fully-populated mark only if every entry in the transfer was created, and an entry
            // whose name is already taken is not — so a directory with one leftover in it (a
            // tree removal that stopped at an open file; a local create racing the first look)
            // was asked about again on every look, for the life of the registration. The
            // leftovers are the refresh's to take up: it adopts an ordinary entry wearing a listed
            // name as that row, and keeps a placeholder true as it does any other. This process
            // sees an unpopulated directory raw, so the enumeration here is exactly the disk.
            var standing = Existing(directory);
            var fresh = standing.Count == 0 ? listing : listing.Where(i => !standing.Contains(i.Name)).ToList();
            if (fresh.Count < listing.Count)
                Console.WriteLine($"population of {folderId ?? "/"}: {listing.Count - fresh.Count} of {listing.Count} entries already on disk; the refresh takes those up");

            // The names and identities must stay pinned until CfExecute has copied the entries out.
            var natives = new IntPtr[fresh.Count * 2];
            var entries = new CF_PLACEHOLDER_CREATE_INFO[fresh.Count];
            uint created;
            bool taken;
            try
            {
                for (var i = 0; i < fresh.Count; i++)
                {
                    natives[i * 2] = Marshal.StringToHGlobalUni(fresh[i].Name);
                    natives[i * 2 + 1] = Marshal.StringToHGlobalUni(fresh[i].Id);
                    entries[i] = Placeholders.Describe(fresh[i], (char*)natives[i * 2], (char*)natives[i * 2 + 1]);
                }

                fixed (CF_PLACEHOLDER_CREATE_INFO* array = entries)
                {
                    taken = Transfer(info, array, fresh.Count, fresh.Count, 0, out created);
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
            // directory on demand, so the filter asks again, and recording the listing here would
            // have a refresh diff against work that never happened.
            var refused = 0;
            var first = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Result.Succeeded) continue;
                if (refused++ == 0) first = entries[i].Result.Value;
            }
            if (created != fresh.Count || refused > 0)
            {
                Console.Error.WriteLine(
                    $"population of {folderId ?? "/"} placed {created} of {fresh.Count} entries, " +
                    $"{refused} refused (first 0x{first:X8}); leaving the folder on demand");
                return;
            }

            // The whole listing, leftovers included: the snapshot is what a refresh diffs against,
            // and a row left out of it would be met next time as something that just appeared.
            Mirror?.NoteListed(folderId, directory, listing);
            Console.WriteLine($"= {folderId ?? "/"} populated ({listing.Count} entries)");
        }
        catch (Exception e) { Callbacks.Fell("fetch-placeholders", e); }
    }

    /// <summary>The names the directory holds right now, or none if it cannot be read — in which case every entry is transferred and the platform says which are taken.</summary>
    static HashSet<string> Existing(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                names.Add(Path.GetFileName(entry));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not read {directory} before populating it: {e.Message}");
        }
        return names;
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
            // the filter need not ask again — remote changes are the refresh's to deliver from
            // here. The platform honours this only if every entry lands, which is checked above.
            parameters.Anonymous.TransferPlaceholders.Flags =
                CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;

        var taken = Callbacks.Executed("transfer-placeholders", in operation, ref parameters);
        created = taken ? parameters.Anonymous.TransferPlaceholders.EntriesProcessed : 0;
        return taken;
    }
}
