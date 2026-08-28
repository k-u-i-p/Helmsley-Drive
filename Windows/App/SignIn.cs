using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace HelmsleyDrive.App;

/// <summary>
/// The interactive leg of OAuth — the one part the Mac does differently, where
/// <c>ASWebAuthenticationSession</c> claims the custom scheme for the duration of the sign-in.
/// Here the scheme is registered under HKCU, so the browser's redirect launches a second instance
/// of this app with the callback URL in <c>argv</c>; that instance hands the URL down a named pipe
/// to the one that is waiting, and exits.
/// </summary>
public static class SignIn
{
    const string PipeName = "helmsley-drive-oauth";

    public static async Task Run()
    {
        RegisterUriScheme();

        var pkce = new OAuth.Pkce();
        var state = OAuth.Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = OAuth.AuthorizeUrl(pkce, state);

        // The pipe must be listening before the browser opens: the redirect races the wait
        // otherwise, and a relay that finds no server gives up rather than retrying.
        await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);

        Console.WriteLine("Opening the browser to sign in to Helmsley…");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string callback;
        try
        {
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server);
            callback = (await reader.ReadToEndAsync(timeout.Token)).Trim();
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Sign-in timed out: no browser redirect arrived within five minutes.");
        }

        var query = ParseQuery(new Uri(callback));
        if (!query.TryGetValue("state", out var returned) || returned != state)
            throw new InvalidOperationException("The sign-in response did not match the request that started it.");
        if (!query.TryGetValue("code", out var code))
            throw new InvalidOperationException(
                query.TryGetValue("error", out var error)
                    ? $"The sign-in server refused: {error}"
                    : "The sign-in response carried no authorization code.");

        TokenProvider.Shared.Store(await OAuth.Exchange(code, pkce.Verifier));
        Console.WriteLine("Signed in.");
    }

    /// <summary>What the browser-launched instance does with the callback URL: pass it on and die.</summary>
    public static void RelayCallback(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(url);
        }
        catch (TimeoutException)
        {
            // No sign-in is waiting — a stale bookmark, or the wait already timed out. Nothing to do.
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
