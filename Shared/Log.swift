import Foundation
import OSLog

/// Where this app says what it is doing.
///
/// The extension has no window and no console: when it fails, Finder shows an empty folder and
/// nothing else, and there is no way to tell an authentication failure from an empty portal from a
/// crash. So every path that can fail says so here, and
///
///     log stream --predicate 'subsystem == "uk.co.helmsley.HelmsleyDrive"' --info
///
/// is the whole diagnostic story.
enum Log {
    private static let subsystem = "uk.co.helmsley.HelmsleyDrive"

    static let app = Logger(subsystem: subsystem, category: "app")
    static let api = Logger(subsystem: subsystem, category: "api")
    static let auth = Logger(subsystem: subsystem, category: "auth")
    static let provider = Logger(subsystem: subsystem, category: "provider")
    static let enumeration = Logger(subsystem: subsystem, category: "enumeration")
}

extension Array where Element == String {
    /// A path as it reads in a log line. The root is a folder too, and "/" says so where an empty
    /// string would look like a missing value.
    var logPath: String { isEmpty ? "/" : "/" + joined(separator: "/") }
}
