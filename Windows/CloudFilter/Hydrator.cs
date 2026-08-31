using System.Buffers;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Answers FETCH_DATA: somebody opened a placeholder whose bytes are still in the bucket. The
/// filter names the file by the identity blob stamped on its placeholder — the portal row id —
/// and this streams the bytes back through CfExecute.
///
/// In chunks, and not only to bound the memory a 500MB row would otherwise cost on a callback
/// thread: the platform gives a callback sixty seconds to say something, and every accepted
/// CfExecute restarts that clock. A whole-file download held before the first word is a file that
/// can never be opened over a slow enough link, however many times the user tries.
/// </summary>
public static unsafe class Hydrator
{
    internal static IRemoteStore? Store;

    // Big enough that the per-chunk cost is nothing against the transfer, and a multiple of the
    // 4KB granularity the platform requires of every range that does not end at the file's end.
    const int ChunkBytes = 4 * 1024 * 1024;

    // The callbacks arrive on the filter's own threads, outside any synchronization context, so
    // blocking one on the fetch is safe and is what keeps the range-response ordering simple.
    internal static void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long offset = parameters->Anonymous.FetchData.RequiredFileOffset;
        long length = parameters->Anonymous.FetchData.RequiredLength;
        try
        {
            var id = Callbacks.Identity(info);
            if (id is null)
            {
                // No identity is no row to ask for. It should not happen; saying so is cheaper
                // than a null dereference on a filter thread.
                Console.Error.WriteLine("hydration asked for a placeholder carrying no identity");
                Transfer(info, null, offset, length, Callbacks.STATUS_CLOUD_FILE_UNSUCCESSFUL);
                return;
            }

            try
            {
                Serve(info, id, offset, length);
            }
            catch (Exception e)
            {
                // Explorer's own message says only that the download failed, so this line is the
                // only place the reason survives — a 404, an expired grant, a name that will not
                // resolve all look identical from there.
                Console.Error.WriteLine($"hydration of {id} failed: {e.Message}");
                Transfer(info, null, offset, length, Callbacks.STATUS_CLOUD_FILE_UNSUCCESSFUL);
            }
        }
        catch (Exception e) { Callbacks.Fell("fetch-data", e); }
    }

    static void Serve(CF_CALLBACK_INFO* info, string id, long offset, long length)
    {
        if (length <= 0)
        {
            // Not a range the filter asks for — and the loop below would answer it by saying
            // nothing at all, which leaves the request to its own sixty-second timeout rather than
            // to an error. A refusal is the honest form of "there is nothing here to hand over".
            Transfer(info, null, offset, length, Callbacks.STATUS_CLOUD_FILE_UNSUCCESSFUL);
            return;
        }

        using var bytes = Store!.Fetch(id).GetAwaiter().GetResult();
        Discard(bytes, offset);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long sent = 0;
            while (sent < length)
            {
                // Every chunk but the last is a whole ChunkBytes, so each range handed over stays
                // 4KB-aligned; the last is whatever the filter asked for, and ends at the file's end.
                var want = (int)Math.Min(ChunkBytes, length - sent);
                var got = bytes.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
                if (got == 0) break;
                fixed (byte* p = buffer)
                {
                    if (!Transfer(info, p, offset + sent, got, 0)) return;
                }
                sent += got;
            }

            if (sent < length)
            {
                // The bucket held fewer bytes than the placeholder claims — a truncated upload, or
                // a size the row is stale about. Answering the short read as a success would leave
                // the reader waiting on a range that is never coming.
                Console.Error.WriteLine($"hydration of {id}: the bucket gave {sent} of the {length} bytes asked for");
                Transfer(info, null, offset + sent, length - sent, Callbacks.STATUS_CLOUD_FILE_UNSUCCESSFUL);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Winds the stream forward to where the filter asked to be served from. Whole-file hydration
    /// is what the registration asks for, so this is nothing in the ordinary case; it exists so
    /// that a ranged request is answered correctly rather than answered with the wrong bytes.
    /// </summary>
    static void Discard(Stream bytes, long offset)
    {
        if (offset <= 0) return;
        if (bytes.CanSeek) { bytes.Seek(offset, SeekOrigin.Begin); return; }

        var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (long left = offset; left > 0;)
            {
                var got = bytes.Read(scratch, 0, (int)Math.Min(scratch.Length, left));
                if (got == 0) return;
                left -= got;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    internal static void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Nothing to abandon: the transfer above is a loop of chunks, and the CfExecute that fails
        // once the request is retired is what ends it — reported by Transfer, not thrown.
    }

    static bool Transfer(CF_CALLBACK_INFO* info, byte* buffer, long offset, long length, int status)
    {
        var operation = Callbacks.OperationOn(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA);

        var parameters = new CF_OPERATION_PARAMETERS();
        parameters.ParamSize = Callbacks.ParamSize(&parameters, &parameters.Anonymous.TransferData);
        parameters.Anonymous.TransferData.CompletionStatus = new NTSTATUS(status);
        parameters.Anonymous.TransferData.Buffer = buffer;
        parameters.Anonymous.TransferData.Offset = offset;
        parameters.Anonymous.TransferData.Length = length;

        return Callbacks.Executed("transfer-data", in operation, ref parameters);
    }
}
