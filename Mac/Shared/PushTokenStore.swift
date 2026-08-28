import Foundation

/// The APNs device token this install last registered with the portal.
///
/// Written by the extension, which is the process PushKit hands the token to, and read by the app,
/// which is the only one that ever has reason to withdraw it: signing out unmounts the volume, and a
/// portal still pushing at a device that has nothing to show is exactly the push Apple asks senders
/// not to make. Nothing else needs it — a registration is the extension telling the server something
/// the server then remembers.
///
/// In the shared group's defaults rather than the keychain: a device token is not a secret. It names
/// where a push goes, it is useless without the team's signing key, and the portal only accepts one
/// alongside an admin's own credential.
enum PushTokenStore {

    private static let key = "PushDeviceToken"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: Configuration.appGroupIdentifier)
    }

    static var token: String? {
        get { defaults?.string(forKey: key) }
        set {
            guard let newValue else {
                defaults?.removeObject(forKey: key)
                return
            }
            defaults?.set(newValue, forKey: key)
        }
    }
}
