import SwiftUI

struct ContentView: View {

    @StateObject private var model = AppModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            header

            Divider()

            if model.isSignedIn {
                connected
            } else {
                disconnected
            }

            if let message = model.errorMessage {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer()

            footer
        }
        .padding(24)
        .frame(width: 460, height: 340)
        .task { await model.refresh() }
        .sheet(isPresented: $model.isEditingServer) { settings }
    }

    private var header: some View {
        HStack(spacing: 12) {
            // The app's own icon rather than a stand-in symbol: it is the portal's mark, and this
            // window is the one place a person confirms they are looking at the real Helmsley app
            // before typing a password into the sheet it opens.
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .frame(width: 52, height: 52)
            VStack(alignment: .leading) {
                Text("Helmsley Drive").font(.title2).bold()
                Text("The client portal's documents, in Finder.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var connected: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label {
                VStack(alignment: .leading) {
                    Text("Signed in as \(model.admin?.name ?? model.admin?.email ?? "an administrator")")
                    if let email = model.admin?.email, model.admin?.name != nil {
                        Text(email).font(.caption).foregroundStyle(.secondary)
                    }
                }
            } icon: {
                Image(systemName: "checkmark.seal.fill").foregroundStyle(.green)
            }

            Label(
                model.isMounted
                    ? "Mounted — look for “\(Configuration.domainDisplayName)” in the Finder sidebar, under Locations."
                    : "Not mounted yet.",
                systemImage: model.isMounted ? "folder.fill" : "folder.badge.questionmark"
            )
            .font(.callout)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)

            HStack {
                // Only when the credential is good but the volume is not registered — see
                // AppModel.mount(). Prominent because in that state it is the one thing to do.
                if !model.isMounted {
                    Button("Mount in Finder") {
                        Task { await model.mount() }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.isWorking)
                }

                Button("Sign Out and Unmount") {
                    Task { await model.disconnect() }
                }
                .disabled(model.isWorking)
            }
        }
    }

    private var disconnected: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Sign in with your Helmsley administrator account to mount the document tree as a volume in Finder.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Text("You will be asked for your password and the code texted to you, exactly as on the portal.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)

            Button("Sign In and Mount") {
                Task { await model.connect() }
            }
            .buttonStyle(.borderedProminent)
            .disabled(model.isWorking)
        }
    }

    private var footer: some View {
        HStack {
            if model.isWorking { ProgressView().controlSize(.small) }
            Spacer()
            Button("Server…") { model.isEditingServer = true }
                .buttonStyle(.link)
                .disabled(model.isWorking)
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Portal address").font(.headline)
            Text("Changing this signs you out — an access token issued by one server means nothing to another.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("https://helmsley-clients.co.uk", text: $model.baseURLText)
                .textFieldStyle(.roundedBorder)

            HStack {
                Spacer()
                Button("Cancel") {
                    model.baseURLText = Configuration.baseURL.absoluteString
                    model.isEditingServer = false
                }
                Button("Save") {
                    Task { await model.saveBaseURL() }
                }
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}
