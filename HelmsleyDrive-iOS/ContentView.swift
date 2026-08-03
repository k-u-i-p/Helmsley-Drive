import SwiftUI

struct ContentView: View {

    @StateObject private var model = AppModel()

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
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Server") { model.isEditingServer = true }
                        .disabled(model.isWorking)
                }
            }
            .task { await model.refresh() }
            .sheet(isPresented: $model.isEditingServer) { serverSheet }
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

                Label(
                    model.isMounted ? "Available in Files" : "Not added to Files yet",
                    systemImage: model.isMounted ? "folder.fill" : "folder.badge.questionmark"
                )
                .foregroundStyle(model.isMounted ? .primary : .secondary)
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

    // MARK: - Server

    private var serverSheet: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("https://helmsley-clients.co.uk", text: $model.baseURLText)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                } header: {
                    Text("Portal address")
                } footer: {
                    Text("Changing this signs you out — an access token issued by one server means nothing to another.")
                }
            }
            .navigationTitle("Server")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") {
                        model.baseURLText = Configuration.baseURL.absoluteString
                        model.isEditingServer = false
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { Task { await model.saveBaseURL() } }
                }
            }
        }
    }
}
