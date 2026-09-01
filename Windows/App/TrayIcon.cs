using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace HelmsleyDrive.App;

/// <summary>
/// The tray icon, and with it the answer to the one thing this app's shape got wrong on Windows.
/// There is no extension process: the engine is in this window's process, so closing the window
/// used to stop the drive — and the person who had merely tidied it away was left with a folder of
/// placeholders that would not open and nothing on screen to say why. The window now closes to
/// here, and Quit is the only thing that stops anything.
///
/// WinForms for the icon and the menu, which is what <c>UseWindowsForms</c> in the project file is
/// for. WPF has never had a tray icon, and hand-rolling Shell_NotifyIcon means owning a
/// message-only window, a taskbar-restart message and the menu's own dismissal behaviour — three
/// things this type gets right by not writing them.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly AppModel _model;
    readonly MainWindow _window;
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _openFolder;
    bool _saidItIsStillRunning;

    public TrayIcon(AppModel model, MainWindow window)
    {
        _model = model;
        _window = window;

        var open = new Forms.ToolStripMenuItem("Open Helmsley Drive", null, (_, _) => Reveal());
        // Bold marks the default: it is what a click on the icon already does, and the menu is
        // where that becomes discoverable rather than something you have to try.
        // Qualified: System.Windows has a FontStyle of its own and this file can see both.
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _openFolder = new Forms.ToolStripMenuItem("Open the Drive Folder", null, (_, _) => OpenFolder());

        var menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add(open);
        menu.Items.Add(_openFolder);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Helmsley Drive", null, (_, _) => Quit())
        {
            ToolTipText = "Stops the drive. Files already downloaded stay where they are; "
                + "nothing else opens until Helmsley Drive is running again.",
        });

        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = menu, Visible = true };
        // Left button only: the right one is the menu's, and NotifyIcon raises this for both.
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Reveal(); };

        // The close box means "put it away", and it is the whole point of this type. Cancelled
        // rather than obeyed — except when Quit set the flag, which is the one close that is real.
        _window.Closing += (_, e) =>
        {
            if (_window.IsQuitting) return;
            e.Cancel = true;
            _window.Hide();
            SayItIsStillRunning();
        };

        _model.PropertyChanged += (_, _) => _window.Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    /// <summary>
    /// The window, back on screen and in front. Public because a second launch of the app asks for
    /// exactly this (see <see cref="ShowRequest"/>) — and it arrives on that listener's thread,
    /// which is why everything here goes through the dispatcher rather than assuming it is already
    /// on it.
    /// </summary>
    public void Reveal() => _window.Dispatcher.InvokeAsync(() =>
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        Foreground.Raise(new WindowInteropHelper(_window).Handle);
    });

    /// <summary>
    /// Once per run, on the first close. Any less and somebody wonders where the app went; any
    /// more and it is a notification about nothing, every time they tidy the window away.
    /// </summary>
    void SayItIsStillRunning()
    {
        if (_saidItIsStillRunning) return;
        _saidItIsStillRunning = true;
        _icon.ShowBalloonTip(5000, "Helmsley Drive is still running",
            "The drive stays in File Explorer while this icon is here. Quit from the icon to stop it.",
            Forms.ToolTipIcon.Info);
    }

    void Refresh()
    {
        // Sixty-three characters is all Windows keeps of this, so it says the one thing that is
        // not already obvious from the icon being there at all.
        _icon.Text = !_model.IsSignedIn ? "Helmsley Drive — signed out"
            : _model.IsMounted ? "Helmsley Drive — mounted"
            : "Helmsley Drive — not mounted";
        _openFolder.Enabled = Directory.Exists(_model.Root);
    }

    void OpenFolder()
    {
        // UseShellExecute, because the thing being started is a folder rather than a program.
        try { Process.Start(new ProcessStartInfo(_model.Root) { UseShellExecute = true }); }
        catch (Exception e) { Console.Error.WriteLine($"could not open {_model.Root}: {e.Message}"); }
    }

    void Quit() => _window.Dispatcher.InvokeAsync(() =>
    {
        // The icon goes first: Shutdown runs the engine down, which takes as long as a quiesce
        // takes, and an icon still sitting there through it invites a second click.
        _icon.Visible = false;
        _window.IsQuitting = true;
        Application.Current.Shutdown();
    });

    /// <summary>
    /// The icon beside the executable, at the size this monitor's tray actually wants — the .ico
    /// carries every size for that reason, and letting Windows pick one and stretch it is what
    /// makes a tray icon look approximately right and never quite right.
    ///
    /// The executable's own embedded copy is the fallback, for the build whose loose .ico somebody
    /// has moved; there is no case where both are missing, but SystemIcons keeps this total.
    /// </summary>
    static Icon LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "HelmsleyDrive.ico");
        if (File.Exists(path))
        {
            try { return new Icon(path, Forms.SystemInformation.SmallIconSize); }
            catch (ArgumentException e) { Console.Error.WriteLine($"the tray icon would not load: {e.Message}"); }
        }
        return (Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>
    /// Putting the window in front, which Activate does not do on its own — and this is measured
    /// rather than assumed. A process that is not already the foreground one may not take the
    /// foreground, so a window summoned from the tray icon or by a second launch came up correct,
    /// visible, and entirely behind whatever was in front of it. For the one gesture whose whole
    /// purpose is "show me the window", that is indistinguishable from nothing having happened.
    /// A topmost toggle does not fix it either: both values land before the compositor sees
    /// either, and the z-order never moves.
    ///
    /// Sharing an input queue with the foreground window's thread is what makes Windows count this
    /// process as the foreground one for the length of the call. It is the long-standing way round
    /// the restriction, and it is bounded — attached for one call and detached in a finally.
    /// </summary>
    static class Foreground
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public static void Raise(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            var inFront = GetForegroundWindow();
            if (inFront == window) return;

            var theirs = GetWindowThreadProcessId(inFront, IntPtr.Zero);
            var ours = GetCurrentThreadId();
            // Attaching a thread to itself fails, and there is nothing to borrow when the window
            // in front is already one of ours.
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try { SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(ours, theirs, false); }
        }
    }
}
