using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;

namespace HelmsleyDrive.App;

/// <summary>
/// The interactive leg of OAuth — the one part the Mac does differently, where
/// <c>ASWebAuthenticationSession</c> claims the custom scheme for the duration of the sign-in.
///
/// There are two ways through it here, and the window takes the first one. <see cref="SignInWindow"/>
/// hosts the portal's pages in a sheet this app owns, watches for the redirect to
/// <c>helmsley-drive:</c> and closes itself on it — the Mac's arrangement in all but name, and the
/// only one that leaves nothing standing open afterwards.
///
/// The other is the default browser, which is what <c>--console</c> gets (no desktop to open a
/// sheet on) and what anything without a WebView2 runtime falls back to. There the scheme is
/// registered under HKCU, so the browser's redirect launches a second instance of this app with the
/// callback URL in <c>argv</c>; that instance hands the URL down a named pipe to the one that is
/// waiting, and exits. Its cost is the tab: a browser handed a redirect to a scheme it does not
/// speak launches the handler and leaves the document where it was, so the consent page stays on
/// screen with nothing to say that it is finished.
///
/// Which also means the callback is attacker-reachable: any local process, and any web page that
/// navigates to the scheme, can start a relay carrying whatever it likes. Two things hold. The
/// pipe is current-user-only at both ends, so nothing running as another user can serve it or
/// squat it. And PKCE plus <c>state</c> is what makes a planted code worthless — the verifier
/// never leaves this process, so a code redeemed without it buys nothing.
/// </summary>
public static class SignIn
{
    const string PipeName = "helmsley-drive-oauth";

    static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    /// <param name="owner">
    /// The window to hang the sign-in sheet off. Null is the headless case — <c>--console</c>, which
    /// has no desktop to open one on — and takes the browser instead.
    /// </param>
    public static async Task Run(Window? owner = null)
    {
        // Even when the sheet is going to handle this. It is rewritten on every sign-in so that the
        // registration follows the binary, and it is what the fallback needs to exist beforehand
        // rather than at the moment WebView2 turns out to be missing.
        RegisterUriScheme();

        var pkce = new OAuth.Pkce();
        var state = OAuth.Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = OAuth.AuthorizeUrl(pkce, state);

        var callback = await WaitForCallback(owner, url).ConfigureAwait(false);

        var query = ParseQuery(callback);

        // The refusal first, and with the sentence that came with it: an authorization decision is
        // not a mismatched state, and telling the user their response "did not match the request
        // that started it" when the server simply said no sends them looking for the wrong thing.
        if (query.TryGetValue("error", out var error))
            throw new InvalidOperationException(
                $"The sign-in server refused: {query.GetValueOrDefault("error_description") ?? error}");

        if (!query.TryGetValue("state", out var returned) || returned != state)
            throw new InvalidOperationException("The sign-in response did not match the request that started it.");
        if (!query.TryGetValue("code", out var code))
            throw new InvalidOperationException("The sign-in response carried no authorization code.");

        TokenProvider.Shared.Store(await OAuth.Exchange(code, pkce.Verifier).ConfigureAwait(false));
        Console.WriteLine("Signed in.");
    }

    /// <summary>The sheet if there is a window to hang it off and a runtime to draw it with.</summary>
    static async Task<Uri> WaitForCallback(Window? owner, string url)
    {
        if (owner is not null)
        {
            try { return await SignInWindow.Show(owner, url).ConfigureAwait(false); }
            catch (SignInSheetUnavailableException e)
            {
                // The runtime is evergreen on Windows 11 and absent on some older builds. Nothing
                // about the sign-in has failed, so it goes round the other way rather than telling
                // the user to install something.
                Console.Error.WriteLine($"the sign-in sheet could not start ({e.Message}); using the default browser");
            }
        }

        // The pipe must be listening before the browser opens: the redirect races the wait
        // otherwise, and a relay that finds no server gives up rather than retrying.
        //
        // CurrentUserOnly is both halves of the pipe's security. It puts an owner-only DACL on the
        // server, so no other account can connect; and it makes the *client* check the server's
        // owner, so a process that got there first and squatted the name cannot collect the
        // authorization code the relay is carrying.
        await using var server = new NamedPipeServerStream(
            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);

        Console.WriteLine("Opening the browser to sign in to Helmsley…");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = new CancellationTokenSource(Patience);
        return await WaitForRelay(server, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for something that is actually a callback. Anything may write to the pipe, and a
    /// single unusable payload used to end the sign-in outright — the server had spent its one
    /// connection, so the browser's real redirect arrived to nobody and the user was left with a
    /// finished browser flow and an app that had given up.
    /// </summary>
    static async Task<Uri> WaitForRelay(NamedPipeServerStream server, CancellationToken quit)
    {
        while (true)
        {
            string relayed;
            try
            {
                await server.WaitForConnectionAsync(quit).ConfigureAwait(false);
                using var reader = new StreamReader(server, leaveOpen: true);
                relayed = (await reader.ReadToEndAsync(quit).ConfigureAwait(false)).Trim();
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Sign-in timed out: no browser redirect arrived within five minutes.");
            }
            finally
            {
                if (server.IsConnected) server.Disconnect();
            }

            if (IsCallback(relayed, out var callback)) return callback;
            Console.Error.WriteLine("something wrote to the sign-in pipe that was not a callback; still waiting");
        }
    }

    /// <summary>The shape the portal was told to redirect to, and nothing else.</summary>
    internal static bool IsCallback(string relayed, out Uri callback)
    {
        callback = null!;
        if (!Uri.TryCreate(relayed, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "helmsley-drive", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Authority + uri.AbsolutePath, "oauth/callback", StringComparison.OrdinalIgnoreCase))
            return false;
        callback = uri;
        return true;
    }

    /// <summary>What the browser-launched instance does with the callback URL: pass it on and die.</summary>
    public static void RelayCallback(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(url);
        }
        catch (TimeoutException)
        {
            // No sign-in is waiting — a stale bookmark, or the wait already timed out. Nothing to do.
        }
        catch (UnauthorizedAccessException)
        {
            // Something is serving the pipe name that is not us. CurrentUserOnly is what noticed;
            // the code stays here rather than going to whatever that is.
            Console.Error.WriteLine("the sign-in pipe is owned by another process; the callback was not passed on");
        }
    }

    /// <summary>
    /// Part of signing out. The sheet keeps its own cookie jar, and a portal session in it outlives
    /// the credential unless it is deleted alongside — on a shared machine the next person's Sign In
    /// would open straight onto the consent page as the administrator who had just signed out.
    /// </summary>
    public static void ForgetBrowserSession()
    {
        try
        {
            if (Directory.Exists(Configuration.BrowserProfileDirectory))
                Directory.Delete(Configuration.BrowserProfileDirectory, recursive: true);
        }
        catch (Exception e)
        {
            // WebView2's browser process can outlive its window by a moment and still hold the
            // profile open. Said out loud and not thrown: the credential and the tree are already
            // gone, and failing the sign-out over a cookie would undo none of that.
            Console.Error.WriteLine($"the sign-in sheet's session was not cleared: {e.Message}");
        }
    }

    /// <summary>
    /// Points <c>helmsley-drive:</c> at this executable. HKCU, so no elevation; rewritten on every
    /// sign-in, so the registration follows the binary wherever it is built or installed.
    /// </summary>
    static void RegisterUriScheme()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot register the URI scheme: the process path is unknown.");
        using var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\helmsley-drive");
        scheme.SetValue(null, "URL:Helmsley Drive");
        scheme.SetValue("URL Protocol", "");
        using var command = scheme.CreateSubKey(@"shell\open\command");
        command.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var values = new Dictionary<string, string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[WebUtility.UrlDecode(parts[0])] = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";
        }
        return values;
    }
}
