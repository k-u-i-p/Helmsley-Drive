using System.Runtime.InteropServices;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// Which folders of the drive Explorer is showing, asked of the shell every second and told to
/// the mirror, which refreshes a folder the moment it is navigated into and, on the poll,
/// re-lists only the ones still showing.
///
/// Navigation is read per window: each window's folders this tick against the same window's
/// folders last tick, so going up and coming back down is a navigation even while another window
/// still shows the folder, and a tab switch — the tabs of one window share its handle and are all
/// listed at once — is not. Judged against the whole set instead, a folder that never left it was
/// never entered, which is how the first cut missed exactly the gesture a user makes to look again.
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
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    readonly string _root;
    readonly Mirror _mirror;
    readonly CancellationTokenSource _stopping = new();
    Thread? _thread;
    bool _complained;
    bool _greeted;

    // What each window (by handle) showed last tick. A window that has gone drops out; a new one
    // is a navigation into everything it shows.
    Dictionary<long, HashSet<string>> _shownByWindow = new();

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
            try
            {
                var shown = FoldersShown();
                if (!_greeted) { Console.WriteLine("watching Explorer"); _greeted = true; }
                var navigated = new List<string>();
                foreach (var (window, folders) in shown)
                {
                    _shownByWindow.TryGetValue(window, out var before);
                    navigated.AddRange(folders.Where(f => before is null || !before.Contains(f)));
                }
                _shownByWindow = shown;
                _mirror.SetOpen(shown.Values.SelectMany(f => f), navigated);
            }
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
    /// The folders under the root that Explorer windows are showing right now, by window handle.
    /// Anything the shell shows that is not a folder of the drive — This PC, a library, a search —
    /// is left out, and a window that closes mid-enumeration is skipped rather than allowed to
    /// fail the tick. A window whose handle cannot be read goes under handle zero.
    /// </summary>
    Dictionary<long, HashSet<string>> FoldersShown()
    {
        var shown = new Dictionary<long, HashSet<string>>();
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
                        if (UnderRoot(url) is not { } folder) continue;
                        long handle;
                        try { handle = (long)window.HWND; }
                        catch (Exception) { handle = 0; }
                        if (!shown.TryGetValue(handle, out var folders))
                            shown[handle] = folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        folders.Add(folder);
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
