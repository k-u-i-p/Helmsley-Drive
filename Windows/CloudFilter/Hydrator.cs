using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Answers FETCH_DATA: somebody opened a placeholder whose bytes are still in the bucket. The
/// filter names the file by the identity blob stamped on its placeholder — the portal row id —
/// and this hands the bytes back through CfExecute.
/// </summary>
public static unsafe class Hydrator
{
    internal static IRemoteStore? Store;

    // The callbacks arrive on the filter's own threads, outside any synchronization context, so
    // blocking one on the fetch is safe and is what keeps the range-response ordering simple.
    internal static void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long offset = parameters->Anonymous.FetchData.RequiredFileOffset;
        long length = parameters->Anonymous.FetchData.RequiredLength;

        byte[] bytes;
        try
        {
            var id = new string((char*)info->FileIdentity, 0, (int)(info->FileIdentityLength / sizeof(char)));
            bytes = Store!.Fetch(id).GetAwaiter().GetResult();
        }
        catch
        {
            // STATUS_UNSUCCESSFUL: Explorer reports the file could not be downloaded, and the
            // placeholder stays a placeholder — the honest outcome for a failed fetch.
            Transfer(info, null, offset, length, unchecked((int)0xC0000001));
            return;
        }

        long available = Math.Max(0, Math.Min(length, bytes.LongLength - offset));
        fixed (byte* p = bytes)
        {
            Transfer(info, p + offset, offset, available, 0);
        }
    }

    internal static void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Whole-file fetches at portal sizes finish or fail promptly; there is no transfer worth
        // abandoning midway yet. Ranged fetches will change that.
    }

    static void Transfer(CF_CALLBACK_INFO* info, byte* buffer, long offset, long length, int status)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };

        var parameters = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        parameters.Anonymous.TransferData.CompletionStatus = new NTSTATUS(status);
        parameters.Anonymous.TransferData.Buffer = buffer;
        parameters.Anonymous.TransferData.Offset = offset;
        parameters.Anonymous.TransferData.Length = length;

        PInvoke.CfExecute(in op, ref parameters).ThrowOnFailure();
    }
}
