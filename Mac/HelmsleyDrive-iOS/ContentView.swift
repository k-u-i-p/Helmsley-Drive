import SwiftUI

struct ContentView: View {

    @StateObject private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        NavigationStack {
            List {
                banner
                status
                actions
                if let message = model.errorMessage { problem(message) }
            }
            .listStyle(.insetGrouped)
            .navigationTitle("Helmsley Drive")
            .task { await model.refresh() }
            // Re-checked on every return to the foreground, because the extension signs out in a
            // different process: without this the screen goes on saying whatever was true when it
            // last drew.
            .onChange(of: scenePhase) { _, phase in
                guard phase == .active else { return }
                Task { await model.refresh() }
            }
        }
    }

    // MARK: - Sections

    private var banner: some View {
        Section {
            HStack(spacing: 14) {
                Image("AppMark")
                    .resizable()
                    .frame(width: 54, height: 54)
                    // The asset is the macOS tile, which already carries its own rounded corners;
                    // the clip is what stops the square edges showing at this size.
                    .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text("The client portal's documents")
                        .font(.headline)
                    Text("Browse them in Files, under Locations.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.vertical, 6)
        }
    }

    @ViewBuilder
    private var status: some View {
        Section("Status") {
            if model.isSignedIn {
                LabeledContent {
                    Text(model.admin?.email ?? "")
                        .foregroundStyle(.secondary)
                } label: {
                    Label(model.admin?.name ?? "Administrator", systemImage: "checkmark.seal.fill")
                        .foregroundStyle(.primary)
                }

                if model.isMounted {
                    // The row says where the documents are, so it may as well take you there. The
                    // arrow is the ordinary mark for a row that leaves the app, and the tint is what
                    // says the row is a button at all — the rest of this section is not.
                    Button {
                        Task { await model.openInFiles() }
                    } label: {
                        HStack {
                            Label("Available in Files", systemImage: "folder.fill")
                            Spacer()
                            Image(systemName: "arrow.up.forward.app")
                                .foregroundStyle(.secondary)
                        }
                    }
                } else {
                    Label("Not added to Files yet", systemImage: "folder.badge.questionmark")
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("Sign in with your Helmsley administrator account to browse the document tree in the Files app.")
                    .foregroundStyle(.secondary)
                Text("You will be asked for your password and the code texted to you, exactly as on the portal.")
                    .font(.footnote)
                    .foregroundStyle(.tertiary)
            }
        }
    }

    @ViewBuilder
    private var actions: some View {
        Section {
            if !model.isSignedIn {
                Button {
                    Task { await model.connect() }
                } label: {
                    Label("Sign In and Add to Files", systemImage: "person.badge.key")
                }
                .disabled(model.isWorking)
            } else {
                // Only when the credential is good but the domain is not registered — a reinstall
                // will do that, and it is not a reason to make someone sign in again.
                if !model.isMounted {
                    Button {
                        Task { await model.mount() }
                    } label: {
                        Label("Add to Files", systemImage: "folder.badge.plus")
                    }
                    .disabled(model.isWorking)
                }

                Button(role: .destructive) {
                    Task { await model.disconnect() }
                } label: {
                    Label("Sign Out and Remove", systemImage: "person.badge.minus")
                }
                .disabled(model.isWorking)
            }

            if model.isWorking {
                HStack(spacing: 10) {
                    ProgressView()
                    Text("Working…").foregroundStyle(.secondary)
                }
            }
        }
    }

    private func problem(_ message: String) -> some View {
        Section {
            Label {
                Text(message).font(.callout)
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            }
        }
    }
}
