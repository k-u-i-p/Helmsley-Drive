using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// The boundary every filter callback sits behind, and the three things they all do the same way.
///
/// The filter calls into managed code through delegates on its own threads. There is no frame
/// above them to catch anything, so an exception leaving one does not fail an operation — it
/// unwinds into cldflt and takes the process with it, which the user sees as the drive going
/// silent and every placeholder in Explorer becoming unopenable until the app is restarted.
/// </summary>
static unsafe class Callbacks
{
    /// <summary>
    /// A refusal the platform will actually pass on. Anything outside the STATUS_CLOUD_FILE_ range
    /// is flattened to STATUS_CLOUD_FILE_UNSUCCESSFUL before the user sees it, so a plain
    /// STATUS_ACCESS_DENIED and a plain STATUS_UNSUCCESSFUL arrive as the same shrug.
    /// </summary>
    public const int STATUS_CLOUD_FILE_ACCESS_DENIED = unchecked((int)0xC000CF18);

    public const int STATUS_CLOUD_FILE_UNSUCCESSFUL = unchecked((int)0xC000CF12);

    /// <summary>
    /// What a callback does with an exception instead of letting it out: says so, and returns.
    /// Whatever the filter was waiting for is now waiting on its own timeout, which is a stuck
    /// operation — where letting this through is a dead drive.
    ///
    /// Called from a <c>catch</c> in each callback rather than wrapping their bodies in a lambda,
    /// because the callback parameters are pointers and a closure cannot hold one.
    /// </summary>
    public static void Fell(string what, Exception e)
    {
        try { Console.Error.WriteLine($"{what} callback failed: {e}"); } catch { }
    }

    /// <summary>
    /// The half of the operation that names which request is being answered. The keys come
    /// straight off the callback, correlation vector included — it is what ties a failure in the
    /// platform's own traces back to the provider that caused it.
    /// </summary>
    public static CF_OPERATION_INFO OperationOn(CF_CALLBACK_INFO* info, CF_OPERATION_TYPE type) => new()
    {
        StructSize = (uint)sizeof(CF_OPERATION_INFO),
        Type = type,
        ConnectionKey = info->ConnectionKey,
        TransferKey = info->TransferKey,
        RequestKey = info->RequestKey,
        CorrelationVector = info->CorrelationVector,
    };

    /// <summary>
    /// What <c>ParamSize</c> is documented to be: the offset of the operation's own member plus
    /// that member's size — <c>CF_SIZE_OF_OP_PARAM</c> in the C header — not the size of the whole
    /// union, which is what <c>sizeof</c> gives and which overstates an acknowledgement threefold.
    /// The platform tolerates the overstatement today and promises nothing about tomorrow.
    /// </summary>
    public static uint ParamSize<T>(CF_OPERATION_PARAMETERS* parameters, T* member) where T : unmanaged =>
        (uint)((byte*)member - (byte*)parameters) + (uint)sizeof(T);

    /// <summary>
    /// The row id stamped on whatever the callback is about, or null where the thing carries no
    /// identity — the sync root above all, which is a real directory rather than a placeholder.
    /// </summary>
    public static string? Identity(CF_CALLBACK_INFO* info) =>
        info->FileIdentity is null || info->FileIdentityLength == 0
            ? null
            : new string((char*)info->FileIdentity, 0, (int)(info->FileIdentityLength / sizeof(char)));

    /// <summary>
    /// Issues one operation and answers whether the platform took it. Failure is a lost race —
    /// a request already cancelled, a handle already closed — and there is nobody left to tell,
    /// so it is a line in the log rather than a throw into native frames.
    /// </summary>
    public static bool Executed(string what, in CF_OPERATION_INFO operation, ref CF_OPERATION_PARAMETERS parameters)
    {
        var result = PInvoke.CfExecute(in operation, ref parameters);
        if (result.Succeeded) return true;
        Console.Error.WriteLine($"{what}: the filter refused the operation (0x{result.Value:X8})");
        return false;
    }
}
