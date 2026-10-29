import SwiftUI

@MainActor
final class FeigeAppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var onTermination: (() -> Void)?
    var onMainWindowClosed: (() -> Void)?
    var openMainWindow: (() -> Void)?
    weak var mainWindow: NSWindow?
    /// Set while the updater replaces the bundle so a pending "still running" tip is not recorded.
    var isUpdating = false
    private var closingMainWindow = false
    func applicationWillFinishLaunching(_ notification: Notification) {
        // Swift Package executables need an explicit foreground activation policy
        // so their windows can become key and receive text input.
        NSApplication.shared.setActivationPolicy(.regular)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showMainWindow()
        return false
    }

    func showMainWindow() {
        if let mainWindow, mainWindow.isVisible || mainWindow.isMiniaturized {
            if mainWindow.isMiniaturized { mainWindow.deminiaturize(nil) }
            mainWindow.makeKeyAndOrderFront(nil)
        } else {
            // A closed SwiftUI scene may have released its NSWindow. Recreate it
            // through the scene action instead of reviving a detached AppKit window.
            openMainWindow?()
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func applicationWillTerminate(_ notification: Notification) { onTermination?() }

    /// Only a user-visible close counts: hiding for an update or terminating must stay silent.
    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow, window === mainWindow, !closingMainWindow, !isUpdating else { return }
        closingMainWindow = true
        defer { closingMainWindow = false }
        onMainWindowClosed?()
    }
}

@main
struct FeigeMacApp: App {
    @NSApplicationDelegateAdaptor(FeigeAppDelegate.self) private var appDelegate
    @StateObject private var model = TransferModel()

    var body: some Scene {
        Window("飞鸽快传", id: "main") {
            MainWindowView(appDelegate: appDelegate)
                .environmentObject(model)
                .frame(minWidth: 960, minHeight: 680)
                .task { await model.start() }
                .onAppear {
                    appDelegate.onTermination = { model.shutdown() }
                    appDelegate.onMainWindowClosed = { model.markBackgroundNotice() }
                }
        }
        .windowStyle(.titleBar)
        .commands { FeigeCommands(model: model, showMainWindow: { appDelegate.showMainWindow() }) }

        Window("偏好设置", id: "settings") {
            SetupView(isFirstRun: false)
                .environmentObject(model)
        }
        .defaultSize(width: 620, height: 760)
        .windowResizability(.contentSize)
    }
}

private struct MainWindowView: View {
    @Environment(\.openWindow) private var openWindow
    let appDelegate: FeigeAppDelegate

    var body: some View {
        ContentView()
            .background(MainWindowReader { appDelegate.mainWindow = $0 })
            .onAppear {
                // Keep the scene opener on the application delegate after this view closes.
                appDelegate.openMainWindow = { [openWindow] in openWindow(id: "main") }
            }
    }
}

private struct MainWindowReader: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> WindowReaderView {
        let view = WindowReaderView()
        view.onWindow = onWindow
        return view
    }

    func updateNSView(_ view: WindowReaderView, context: Context) {
        view.onWindow = onWindow
        if let window = view.window {
            window.delegate = view.delegate
            onWindow(window)
        }
    }

    final class WindowReaderView: NSView {
        var onWindow: ((NSWindow) -> Void)?
        let delegate = CloseObserver()
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window else { return }
            delegate.onClose = { [weak window] in
                guard let window, let appDelegate = NSApplication.shared.delegate as? FeigeAppDelegate else { return }
                appDelegate.windowWillClose(Notification(name: NSWindow.willCloseNotification, object: window))
            }
            window.delegate = delegate
            onWindow?(window)
        }
    }
}

private struct FeigeCommands: Commands {
    @Environment(\.openWindow) private var openWindow
    let model: TransferModel
    let showMainWindow: () -> Void

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("显示主窗口", action: showMainWindow)
                .keyboardShortcut("1", modifiers: .command)
        }
        CommandGroup(after: .appInfo) {
            Button("偏好设置…") { openWindow(id: "settings") }
                .keyboardShortcut(",", modifiers: .command)
            Button("检查更新…") {
                if model.updates?.canCheck == true { model.updates?.check() }
                openWindow(id: "settings")
            }
        }
    }
}

/// Forwards the main window's close event without taking ownership of the window delegate logic.
final class CloseObserver: NSObject, NSWindowDelegate {
    var onClose: (() -> Void)?

    func windowWillClose(_ notification: Notification) { onClose?() }
}
