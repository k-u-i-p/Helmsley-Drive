import Foundation

// MARK: - Wire types

/// One document, as `/api/files` describes it. `version` is the content hash: it changes when and
/// only when the bytes do, which is what the file provider needs and what a date would not give.
struct RemoteFile: Codable, Sendable, Equatable {
    let id: Int
    let filename: String
    let title: String
    let type: String?
    let mime: String?
    let size: Int64?
    let version: String
    let uploadDate: String?
}

/// One subfolder. `segment` is what goes back in a path; `name` is what a person reads. They differ
/// wherever the tree fans out over rows — a client's folder is segmented by id and named by the
/// client — so both are kept, and renaming a client in the portal never invalidates a stored path.
struct RemoteFolder: Codable, Sendable, Equatable {
    let segment: String
    let name: String
    let writable: Bool
}

struct Listing: Codable, Sendable {
    let folders: [RemoteFolder]
    let files: [RemoteFile]
    let writable: Bool
    let accept: [String]
}

struct Admin: Codable, Sendable {
    let id: Int
    let name: String?
    let email: String?
    let role: String?
}

// MARK: - Errors

enum APIError: LocalizedError {
    case http(status: Int, message: String)
    case malformedResponse
    case uploadRejected(status: Int)

    var errorDescription: String? {
        switch self {
        case .http(let status, let message): return message.isEmpty ? "The server returned HTTP \(status)." : message
        case .malformedResponse: return "The server sent a response this app could not read."
        case .uploadRejected(let status): return "Cloud Storage refused the upload (HTTP \(status))."
        }
    }

    /// The distinction the file provider acts on: gone means remove the item, everything else means
    /// report a transient failure and let the system retry.
    var isNotFound: Bool {
        if case .http(let status, _) = self { return status == 404 }
        return false
    }
}

// MARK: - Client

/// Everything the app and the extension ask of the portal.
///
/// Not an actor: each call is independent and `URLSession` is already safe to use concurrently, so
/// serialising them would only stop the extension fetching two documents at once. What does need
/// serialising — minting an access token — is `TokenProvider`'s job.
struct HelmsleyAPI: Sendable {

    static let shared = HelmsleyAPI()

    private var base: URL { Configuration.baseURL.appendingPathComponent("api/files") }

    // MARK: Reads

    func list(path: [String]) async throws -> Listing {
        var components = URLComponents(url: base.appendingPathComponent("list"), resolvingAgainstBaseURL: false)!
        // Repeated `path`, in order — the same shape the dashboard's own /browse takes, and the
        // reason a segment may safely contain a slash or a space.
        components.queryItems = path.map { URLQueryItem(name: "path", value: $0) }
        return try await get(components.url!)
    }

    func document(id: Int) async throws -> RemoteFile {
        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await get(base.appendingPathComponent("documents/\(id)"))
        return wrapper.file
    }

    func whoami() async throws -> Admin {
        struct Wrapper: Decodable { let admin: Admin }
        let wrapper: Wrapper = try await get(base.appendingPathComponent("whoami"))
        return wrapper.admin
    }

    /// Downloads a document's bytes to a temporary file, which the caller owns and must move or
    /// delete. Streamed to disk rather than held in memory: documents run to hundreds of megabytes,
    /// and an extension that buffers one is an extension the system kills.
    func downloadContents(id: Int) async throws -> URL {
        var request = URLRequest(url: base.appendingPathComponent("documents/\(id)/content"))
        request.setValue("Bearer \(try await TokenProvider.shared.accessToken())", forHTTPHeaderField: "Authorization")

        let (url, response) = try await URLSession.shared.download(for: request, delegate: RedirectSanitiser.shared)
        guard let http = response as? HTTPURLResponse else {
            try? FileManager.default.removeItem(at: url)
            throw APIError.malformedResponse
        }
        guard (200..<300).contains(http.statusCode) else {
            try? FileManager.default.removeItem(at: url)
            throw APIError.http(status: http.statusCode, message: "Could not download document \(id).")
        }
        return url
    }

    // MARK: Writes

    /// Files a new document into `path`, in the three steps the portal's upload has always taken:
    /// a ticket, a PUT straight to Cloud Storage, then a finalise that writes the row.
    ///
    /// The bytes never pass through the portal — App Engine caps a request at 32MB — so the middle
    /// step talks to a completely different host, using a URL signed for exactly this one object.
    func upload(path: [String], filename: String, mime: String?, fileURL: URL) async throws -> RemoteFile {
        struct Ticket: Decodable {
            let uploadId: String
            let uploadUrl: String
            let contentType: String
            let maxBytes: Int64
        }

        // NSNull, not a missing key: the server reads null as "this drop declares no content type"
        // and falls back to octet-stream, which is a different thing from the key being absent.
        let ticket: Ticket = try await post(
            base.appendingPathComponent("upload-ticket"),
            body: ["path": path, "contentType": mime ?? NSNull()]
        )

        var put = URLRequest(url: URL(string: ticket.uploadUrl)!)
        put.httpMethod = "PUT"
        // Both headers were signed into the URL, so they must be sent back exactly: Cloud Storage
        // recomputes the signature over them and refuses the PUT if either differs.
        put.setValue(ticket.contentType, forHTTPHeaderField: "Content-Type")
        put.setValue("0,\(ticket.maxBytes)", forHTTPHeaderField: "x-goog-content-length-range")

        // No Authorization header of ours goes to the bucket: the signature in the URL is the whole
        // credential, and the portal's bearer token has no business on another host.
        let (_, putResponse) = try await URLSession.shared.upload(for: put, fromFile: fileURL)
        let putStatus = (putResponse as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(putStatus) else { throw APIError.uploadRejected(status: putStatus) }

        struct Wrapper: Decodable { let file: RemoteFile }
        let wrapper: Wrapper = try await post(
            base.appendingPathComponent("finalise"),
            body: ["path": path, "uploadId": ticket.uploadId, "filename": filename]
        )
        return wrapper.file
    }

    func delete(id: Int) async throws {
        var request = URLRequest(url: base.appendingPathComponent("documents/\(id)"))
        request.httpMethod = "DELETE"
        _ = try await send(request) as Empty
    }

    // MARK: Plumbing

    private struct Empty: Decodable {}

    private func get<T: Decodable>(_ url: URL) async throws -> T {
        try await send(URLRequest(url: url))
    }

    private func post<T: Decodable>(_ url: URL, body: [String: Any]) async throws -> T {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await send(request)
    }

    private func send<T: Decodable>(_ request: URLRequest) async throws -> T {
        var authorised = request
        authorised.setValue("Bearer \(try await TokenProvider.shared.accessToken())", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: authorised, delegate: RedirectSanitiser.shared)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            let message = Self.serverMessage(data) ?? ""
            Log.api.error("\(authorised.httpMethod ?? "GET", privacy: .public) \(authorised.url?.path ?? "?", privacy: .public) -> \(status, privacy: .public) \(message, privacy: .public)")
            throw APIError.http(status: status, message: message)
        }
        if T.self == Empty.self { return Empty() as! T }
        do {
            return try JSONDecoder().decode(T.self, from: data)
        } catch {
            throw APIError.malformedResponse
        }
    }

    /// The portal answers every refusal with `{ error: "..." }`, and that sentence is written to be
    /// read by a person — so it is what Finder shows rather than a status code.
    private static func serverMessage(_ data: Data) -> String? {
        struct Failure: Decodable { let error: String? }
        return (try? JSONDecoder().decode(Failure.self, from: data))?.error
    }
}

/// Strips the `Authorization` header when a request is redirected to another host.
///
/// A document download is a 302 into Cloud Storage carrying a signed URL, and `URLSession` follows
/// redirects by replaying the original headers — which would hand the portal's bearer token to a
/// completely unrelated host. The signature is the only credential the bucket wants anyway.
final class RedirectSanitiser: NSObject, URLSessionTaskDelegate, @unchecked Sendable {

    static let shared = RedirectSanitiser()

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        let sameHost = request.url?.host == task.originalRequest?.url?.host
        guard !sameHost else { return completionHandler(request) }

        var stripped = request
        stripped.setValue(nil, forHTTPHeaderField: "Authorization")
        completionHandler(stripped)
    }
}
