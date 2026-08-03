import AppKit
import SwiftUI

/// 独立的 AI 生图窗口（菜单栏 → Generate Image）。
@MainActor
final class ImageGenWindowController: NSObject, NSWindowDelegate {
    static let shared = ImageGenWindowController()

    private var window: NSWindow?

    var hasOpenWindow: Bool { window != nil }

    func show() {
        if let window, window.isVisible {
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            return
        }
        window?.close()
        window = nil

        // 生图窗口需要键盘输入和 Dock 图标，与 Settings 一致使用 .regular。
        NSApp.setActivationPolicy(.regular)

        let host = NSHostingView(rootView: ImageGenView())
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 720),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered, defer: false
        )
        window.title = "Generate Image"
        window.delegate = self
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.center()
        self.window = window

        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
        }
    }

    func windowWillClose(_ notification: Notification) {
        window = nil
        // 仅当 Settings 窗口也不在时恢复 accessory（无 Dock 图标）。
        if !SettingsWindowController.shared.hasOpenWindow {
            NSApp.setActivationPolicy(.accessory)
        }
    }
}
