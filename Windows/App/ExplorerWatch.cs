using System.Runtime.InteropServices;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// Which folders of the drive Explorer is showing, asked of the shell every couple of seconds
/// and told to the mirror, which refreshes a folder the moment it is navigated into and, on the
/// poll, re-lists only the ones still showing.
///
/// The shell is asked rather than the filter because the filter cannot say. A directory is
/// populated once, and from then on the filter marks it fully populated and never asks about it
/// again; keeping it on demand instead makes the provider the authority for every name lookup
/// under it — measured in the harness as a callback storm and "the name of the file cannot be
/// resolved" on a plain create — and the open and close notifications are for files, never for
/// a directory handle. Explorer, though, publishes its windows through <c>Shell.Application</c>'s
/// <c>Windows()</c> collection, each with the folder it is showing; tabs are windows there too.
///
/// What this does not see: a folder open in another program's Open or Save dialog. Its first
/// look populates it, which is a fresh listing, and after that it goes without the poll until
/// Explorer shows it. Accepted for now.
/// </summary>
public sealed class ExplorerWatch : IDisposable
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    readonly string _root;
    readonly Mirror _mirror;
    readonly CancellationTokenSource _stopping = new();
    Thread? _thread;
    bool _complained;

    public ExplorerWatch(string root, Mirror mirror)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _mirror = mirror;
    }

    /// <summary>
    /// Its own thread, and a single-threaded apartment: the shell objects are apartment-threaded
    /// and the collection lives in explorer.exe, and an STA is the apartment they are written to
    /// be called from. Nothing calls back into this thread, so it needs no message pump.
    /// </summary>
    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ExplorerWatch" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Loop()
    {
        var quit = _stopping.Token;
        while (!quit.IsCancellationRequested)
        {
            try { _mirror.SetOpen(FoldersShown()); }
            catch (Exception e)
            {
                if (!_complained)
                    Console.Error.WriteLine($"could not ask Explorer what it is showing ({e.Message}); the poll will not follow it");
                _complained = true;
            }
            if (quit.WaitHandle.WaitOne(Tick)) break;
        }
    }

    /// <summary>
    /// The folders under the root that Explorer windows are showing right now. Anything the shell
    /// shows that is not a folder of the drive — This PC, a library, a search — is left out, and
    /// a window that closes mid-enumeration is skipped rather than allowed to fail the tick.
    /// </summary>
    List<string> FoldersShown()
    {
        var shown = new List<string>();
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application is not registered");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic windows = shell.Windows();
            try
            {
                int count = windows.Count;
                for (var i = 0; i < count; i++)
                {
                    dynamic? window = null;
                    try
                    {
                        window = windows.Item(i);
                        if (window is null) continue;
                        string? url = window.LocationURL;
                        if (string.IsNullOrEmpty(url)) continue;
                        if (UnderRoot(url) is { } folder && !shown.Contains(folder, StringComparer.OrdinalIgnoreCase))
                            shown.Add(folder);
                    }
                    catch (COMException) { }
                    finally { if (window is not null) Marshal.FinalReleaseComObject(window); }
                }
            }
            finally { Marshal.FinalReleaseComObject(windows); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return shown;
    }

    /// <summary>The window's location as a directory of the drive, or null — a file: URL of a folder at or below the root, normalised to a path.</summary>
    string? UnderRoot(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)) return path;
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }
}
