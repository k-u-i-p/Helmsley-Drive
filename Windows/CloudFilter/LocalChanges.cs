using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// The local write path: what the filter reports when something under the root is closed, renamed
/// or deleted, mapped onto <see cref="Mirror"/>'s handlers.
///
/// Every handler first drops events this process caused itself. The mirror renames and deletes
/// local entries to apply *remote* changes, and even reading a placeholder's state opens a handle
/// whose close comes back through here — without the process check, every echo would be taken for
/// a user action and sent to the portal, and a state check would generate close events forever.
///
/// Rename and delete arrive *before* the operation and are held for an acknowledgement, so the
/// portal call happens inside the callback and its refusal becomes the operation's refusal —
/// Explorer shows the failure instead of letting the trees drift apart.
/// </summary>
public static unsafe class LocalChanges
{
    internal static Mirror? Mirror;

    /// <summary>Prints every notification as it arrives — the harness's eyes, off in the app.</summary>
    public static bool Trace;

    static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    static void Note(CF_CALLBACK_INFO* info, string kind, string detail)
    {
        if (!Trace) return;
        var pid = info->ProcessInfo is null ? 0 : info->ProcessInfo->ProcessId;
        Console.WriteLine($"  [{kind}] pid={pid}{(pid == OwnProcessId ? " (self)" : "")} {detail}");
    }

    const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    static bool IsOwnEvent(CF_CALLBACK_INFO* info) =>
        info->ProcessInfo is not null && info->ProcessInfo->ProcessId == OwnProcessId;

    /// <summary>VolumeDosName + NormalizedPath: the filter names files volume-relative.</summary>
    static string FullPath(CF_CALLBACK_INFO* info, PCWSTR volumeRelative)
    {
        var volume = info->VolumeDosName.ToString();
        var path = volumeRelative.ToString();
        return path.StartsWith('\\') ? volume + path : volume + '\\' + path;
    }

    static string Identity(CF_CALLBACK_INFO* info) =>
        info->FileIdentity is null
            ? ""
            : new string((char*)info->FileIdentity, 0, (int)(info->FileIdentityLength / sizeof(char)));

    // MARK: - Close: the save and the create

    internal static void OnCloseCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        Note(info, "close", FullPath(info, info->NormalizedPath));
        if (IsOwnEvent(info) || Mirror is not { } mirror) return;
        if ((parameters->Anonymous.CloseCompletion.Flags
            & CF_CALLBACK_CLOSE_COMPLETION_FLAGS.CF_CALLBACK_CLOSE_COMPLETION_FLAG_DELETED) != 0) return;

        var path = FullPath(info, info->NormalizedPath);
        // Off the filter's thread: an upload can run for minutes, and holding the callback would
        // hold every other notification with it. Nothing here is acknowledged, so nothing waits.
        _ = Task.Run(() => mirror.OnClosed(path));
    }

    // MARK: - Rename: the rename, the move, and the drag to the bin

    internal static void OnRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        Note(info, "rename", $"{FullPath(info, info->NormalizedPath)} -> {FullPath(info, parameters->Anonymous.Rename.TargetPath)}");
        var allowed = true;
        if (!IsOwnEvent(info) && Mirror is { } mirror)
        {
            var source = FullPath(info, info->NormalizedPath);
            var target = FullPath(info, parameters->Anonymous.Rename.TargetPath);
            allowed = mirror.OnRenaming(source, target, Identity(info));
        }
        Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, allowed);
    }

    internal static void OnRenameCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        Note(info, "renamed", $"{FullPath(info, parameters->Anonymous.RenameCompletion.SourcePath)} -> {FullPath(info, info->NormalizedPath)}");
        if (IsOwnEvent(info) || Mirror is not { } mirror) return;

        // The one case the pre-rename callback cannot answer for: something arriving from outside
        // the root. A move closes no handles, so this completion is its only announcement.
        var source = FullPath(info, parameters->Anonymous.RenameCompletion.SourcePath);
        var target = FullPath(info, info->NormalizedPath);
        if (mirror.IsUnderRoot(source) || !mirror.IsUnderRoot(target)) return;
        _ = Task.Run(() => mirror.OnArrival(target));
    }

    // MARK: - Delete

    internal static void OnDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        Note(info, "delete", FullPath(info, info->NormalizedPath));
        var allowed = true;
        if (!IsOwnEvent(info) && Mirror is { } mirror)
        {
            var path = FullPath(info, info->NormalizedPath);
            var undelete = (parameters->Anonymous.Delete.Flags
                & CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_UNDELETE) != 0;
            allowed = undelete
                ? mirror.OnUndeleting(path, Identity(info))
                : mirror.OnDeleting(path, Identity(info));
        }
        Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, allowed);
    }

    // MARK: - The acknowledgement the held operations wait on

    static void Acknowledge(CF_CALLBACK_INFO* info, CF_OPERATION_TYPE type, bool allowed)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = type,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };

        var parameters = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        var status = new NTSTATUS(allowed ? 0 : STATUS_ACCESS_DENIED);
        if (type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME)
            parameters.Anonymous.AckRename.CompletionStatus = status;
        else
            parameters.Anonymous.AckDelete.CompletionStatus = status;

        PInvoke.CfExecute(in op, ref parameters).ThrowOnFailure();
    }
}
