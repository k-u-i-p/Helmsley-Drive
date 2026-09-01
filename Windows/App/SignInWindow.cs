using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HelmsleyDrive.App;

/// <summary>
/// The sign-in sheet: the portal's own pages, hosted in a window this app owns — which is the
/// whole point, because a window it owns is a window it can close.
///
/// The default browser cannot be closed. A redirect to <c>helmsley-drive:</c> hands the URL to the
/// OS and leaves the document exactly where it was, so the tab is left sitting on the consent page
/// with its Connect button still showing, and nothing any process can say will shut it: browsers
/// refuse <c>window.close()</c> for a tab the user navigated to, and a native app has no handle on
/// the tab at all. This is <c>ASWebAuthenticationSession</c>'s answer ported rather than argued
/// with — the sheet belongs to the app, so the app takes it away when the code arrives.
///
/// The redirect itself never leaves this window. <c>NavigationStarting</c> sees the callback URL
/// and cancels it, which also spares the HKCU handler a launch: letting it through would start a
/// second instance of this app to relay a code the process that asked for it is already holding.
/// </summary>
public sealed class SignInWindow : Window
{
    readonly WebView2 _web = new();

    // RunContinuationsAsynchronously so that whoever awaits this is not resumed inside Close() —
    // the continuation goes on to mount a drive, and it must not do that from the middle of a
    // window teardown.
    readonly TaskCompletionSource<Uri> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly string _authorizeUrl;

    /// <summary>
    /// Opens the sheet and completes with the callback URL the portal redirected to. Owned rather
    /// than modal: the status window behind it has already disabled its buttons for the duration,
    /// and a nested message loop under a click handler is a hazard this does not need.
    /// </summary>
    public static Task<Uri> Show(Window owner, string authorizeUrl)
    {
        var sheet = new SignInWindow(authorizeUrl) { Owner = owner };
        sheet.Show();
        return sheet._finished.Task;
    }

    SignInWindow(string authorizeUrl)
    {
        _authorizeUrl = authorizeUrl;

        Title = "Sign in to Helmsley";
        Width = 520;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.WindowBrush;
        Content = _web;

        // The control's own Loaded, not the window's: what WebView2 needs before it will start is
        // a window handle to put the browser in, and that belongs to the control rather than to the
        // window around it. Both orderings happen to work on a desktop; this is the one that says
        // what is actually being waited for.
        _web.Loaded += async (_, _) => await Start();

        // Closed, not Closing: the success path sets the result and then asks for the close, so by
        // the time this runs the result is already there and TrySetException is a no-op. What it
        // catches is the other ending — the user shutting the sheet, or the owner window going —
        // which would otherwise leave Connect awaiting a task nobody will ever complete.
        Closed += (_, _) =>
        {
            _finished.TrySetException(new InvalidOperationException("Sign-in was cancelled."));
            _web.Dispose();
        };
    }

    async Task Start()
    {
        try
        {
            // The profile goes beside the token store, never in WebView2's default spot next to the
            // executable: installed under Program Files that folder is not writable, and the sheet
            // would fail to start on exactly the machines a shipped build runs on.
            Directory.CreateDirectory(Configuration.BrowserProfileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Configuration.BrowserProfileDirectory);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e)
        {
            // No runtime, or a profile that cannot be written. Not a failed sign-in — the caller
            // has a browser to fall back on — so it is reported as its own kind of refusal.
            _finished.TrySetException(new SignInSheetUnavailableException(e));
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.NavigationStarting += (_, e) => Intercept(e.Uri, () => e.Cancel = true);

        // Belt and braces on the one navigation that matters. A scheme the OS has a handler for can
        // also arrive at LaunchingExternalUriScheme, and which of the two a given runtime raises
        // first is not a thing to stake the sign-in on. Both routes end in the same TrySetResult,
        // so whichever fires first wins and the other finds the work done.
        core.LaunchingExternalUriScheme += (_, e) => Intercept(e.Uri, () => e.Cancel = true);

        // Anything the portal would open in a new tab — a password reset, a help link — stays in
        // this window. Unhandled, WebView2 puts it in a chromeless popup with no address bar and no
        // way back, which is a worse place to be asked for a password than this is.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        core.Navigate(_authorizeUrl);
    }

    void Intercept(string uri, Action cancel)
    {
        if (!SignIn.IsCallback(uri, out var callback)) return;
        cancel();
        _finished.TrySetResult(callback);
        // Not Close() from inside the event: WebView2 is mid-callback into its own navigation
        // machinery, and tearing the control out from under it there is how a clean sign-in ends
        // in a crash dialog. The next dispatcher turn is soon enough.
        Dispatcher.InvokeAsync(Close);
    }
}

/// <summary>
/// The sheet could not start, which says nothing about the sign-in itself — so the caller opens
/// the default browser instead rather than reporting a failure the user could not act on.
/// </summary>
public sealed class SignInSheetUnavailableException(Exception cause)
    : Exception(cause.Message, cause);
