import SwiftUI

/// The iOS container app. Same two jobs as the Mac one — sign in, and register the file provider
/// domain — and the same `AppModel` behind it; only the presentation differs.
///
/// It has to stay installed: an extension is loaded out of its host app's bundle, so deleting this
/// app takes Helmsley out of the Files app with it.
@main
struct HelmsleyDriveApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}
