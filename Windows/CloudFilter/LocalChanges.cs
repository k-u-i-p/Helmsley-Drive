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

    static bool IsOwnEvent(CF_CALLBACK_INFO* info) =>
        info->ProcessInfo is not null && info->ProcessInfo->ProcessId == OwnProcessId;

    /// <summary>VolumeDosName + NormalizedPath: the filter names files volume-relative.</summary>
    static string FullPath(CF_CALLBACK_INFO* info, PCWSTR volumeRelative)
    {
        var volume = info->VolumeDosName.ToString();
        var path = volumeRelative.ToString();
        return path.StartsWith('\\') ? volume + path : volume + '\\' + path;
    }

    // MARK: - Close: the save and the create

    internal static void OnCloseCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            Note(info, "close", FullPath(info, info->NormalizedPath));
            if (IsOwnEvent(info) || Mirror is not { } mirror) return;
            if ((parameters->Anonymous.CloseCompletion.Flags
                & CF_CALLBACK_CLOSE_COMPLETION_FLAGS.CF_CALLBACK_CLOSE_COMPLETION_FLAG_DELETED) != 0) return;

            // Off the filter's thread: an upload can run for minutes, and holding the callback would
            // hold every other notification with it. Nothing here is acknowledged, so nothing waits.
            mirror.Spawn(FullPath(info, info->NormalizedPath), mirror.OnClosed);
        }
        catch (Exception e) { Callbacks.Fell("close-completion", e); }
    }

    // MARK: - Rename: the rename, the move, and the drag to the bin

    internal static void OnRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var allowed = true;
        try
        {
            Note(info, "rename", $"{FullPath(info, info->NormalizedPath)} -> {FullPath(info, parameters->Anonymous.Rename.TargetPath)}");
            if (!IsOwnEvent(info) && Mirror is { } mirror)
            {
                var source = FullPath(info, info->NormalizedPath);
                var target = FullPath(info, parameters->Anonymous.Rename.TargetPath);

                // The platform's own answer to "is this inside the drive", rather than a prefix
                // test on the path: it has already expanded short names and resolved mount points,
                // and a junction or a subst'd drive is exactly where a string comparison and the
                // filter would disagree — with a plain rename read as a drag out of the drive, and
                // the row binned for it.
                var flags = parameters->Anonymous.Rename.Flags;
                var sourceIn = (flags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_SOURCE_IN_SCOPE) != 0;
                var targetIn = (flags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE) != 0;

                allowed = mirror.OnRenaming(source, target, Callbacks.Identity(info), sourceIn, targetIn);
            }
        }
        catch (Exception e)
        {
            Callbacks.Fell("rename", e);
            allowed = false;
        }
        // Outside the catch: the filter is holding the user's rename, and the one thing worse than
        // refusing it is never answering at all.
        Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, allowed);
    }

    internal static void OnRenameCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        try
        {
            Note(info, "renamed", $"{FullPath(info, parameters->Anonymous.RenameCompletion.SourcePath)} -> {FullPath(info, info->NormalizedPath)}");
            if (IsOwnEvent(info) || Mirror is not { } mirror) return;

            // The one case the pre-rename callback cannot answer for: something arriving from outside
            // the root. A move closes no handles, so this completion is its only announcement.
            var source = FullPath(info, parameters->Anonymous.RenameCompletion.SourcePath);
            var target = FullPath(info, info->NormalizedPath);
            if (mirror.IsUnderRoot(source) || !mirror.IsUnderRoot(target)) return;
            mirror.Spawn(target, mirror.OnArrival);
        }
        catch (Exception e) { Callbacks.Fell("rename-completion", e); }
    }

    // MARK: - Delete

    internal static void OnDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var allowed = true;
        try
        {
            Note(info, "delete", FullPath(info, info->NormalizedPath));
            if (!IsOwnEvent(info) && Mirror is { } mirror)
            {
                var path = FullPath(info, info->NormalizedPath);
                var undelete = (parameters->Anonymous.Delete.Flags
                    & CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_UNDELETE) != 0;
                allowed = undelete
                    ? mirror.OnUndeleting(path, Callbacks.Identity(info))
                    : mirror.OnDeleting(path, Callbacks.Identity(info));
            }
        }
        catch (Exception e)
        {
            Callbacks.Fell("delete", e);
            allowed = false;
        }
        Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, allowed);
    }

    // MARK: - The acknowledgement the held operations wait on

    static void Acknowledge(CF_CALLBACK_INFO* info, CF_OPERATION_TYPE type, bool allowed)
    {
        var operation = Callbacks.OperationOn(info, type);

        var parameters = new CF_OPERATION_PARAMETERS();
        // A refusal has to be said in the platform's own vocabulary: anything outside the
        // STATUS_CLOUD_FILE_ range is flattened to "unsuccessful" before Explorer words it, so a
        // plain access-denied and a plain failure reach the user as the same shrug.
        var status = new NTSTATUS(allowed ? 0 : Callbacks.STATUS_CLOUD_FILE_ACCESS_DENIED);
        if (type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME)
        {
            parameters.ParamSize = Callbacks.ParamSize(&parameters, &parameters.Anonymous.AckRename);
            parameters.Anonymous.AckRename.CompletionStatus = status;
        }
        else
        {
            parameters.ParamSize = Callbacks.ParamSize(&parameters, &parameters.Anonymous.AckDelete);
            parameters.Anonymous.AckDelete.CompletionStatus = status;
        }

        Callbacks.Executed(type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME ? "ack-rename" : "ack-delete",
            in operation, ref parameters);
    }
}
