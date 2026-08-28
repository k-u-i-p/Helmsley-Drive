namespace HelmsleyDrive.App;

/// <summary>
/// Identifiers and defaults, named once. The Mac side (Mac/Shared/Configuration.swift) also settles
/// how two sandboxed processes share a credential; none of that arises here — one process, one
/// reader — so this is the part that survived the crossing.
/// </summary>
public static class Configuration
{
    /// <summary>
    /// Where the portal lives. Fixed at build time: a shipped app talks to the portal and nothing
    /// else, so there is no address for anyone to get wrong.
    /// </summary>
    public static readonly Uri BaseUri = new("https://helmsley-clients.co.uk");

    /// <summary>
    /// The OAuth client this app is registered as, matching <c>mcp.clients[].clientId</c> in the
    /// portal's config.json. A public client: it ships to laptops, so it holds no secret, and PKCE
    /// is what binds an authorization code to the process that asked for it.
    /// </summary>
    public const string OAuthClientId = "helmsley-drive";

    public const string OAuthScope = "mcp";

    /// <summary>
    /// The same custom scheme the Mac uses, already on the portal's allowlist. Windows honours it
    /// through <c>HKCU\Software\Classes\helmsley-drive</c>; the browser launches a second instance
    /// of this app with the callback URL, which relays it to the one that is waiting.
    /// </summary>
    public const string OAuthRedirectUri = "helmsley-drive://oauth/callback";

    /// <summary>Where this app keeps what it must keep — the token set and the mirror's snapshots.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helmsley Drive");

    /// <summary>
    /// How often the whole tree is re-listed. The portal has no change feed and Windows has no
    /// push yet (PORTING.md reserves that question), so polling is the only way remote changes
    /// arrive. The Mac asks every 30 seconds — but only for folders someone is looking at; this
    /// walks everything, one request per folder, so it goes gentler on the portal.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
}
