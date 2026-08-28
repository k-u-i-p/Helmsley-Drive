import SwiftUI

/// The container app. Its only jobs are signing in and registering the file provider domain — the
/// documents themselves are Finder's business, serviced by the extension bundled inside this app.
///
/// It still has to exist, and has to stay installed: an extension is loaded out of its host app's
/// bundle, so deleting this app unmounts the volume.
@main
struct HelmsleyDriveApp: App {
    var body: some Scene {
        Window("Helmsley Drive", id: "main") {
            ContentView()
        }
        .windowResizability(.contentSize)
        // Nothing here opens documents or has a document-shaped menu, so the default New/Open
        // items would all be dead.
        .commands { CommandGroup(replacing: .newItem) {} }
    }
}
