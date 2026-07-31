import AppKit
import SwiftUI
import DesktopPetCore

/// Owns the menu bar status item and a native `NSPopover` (the pattern used by
/// polished menu bar apps): smooth open/close animation, a real arrow pointing
/// at the icon, and transient auto-dismiss on outside clicks.
@MainActor
final class StatusBarController: NSObject, ObservableObject {
    static let shared = StatusBarController()

    private var statusItem: NSStatusItem?
    private let popover = NSPopover()

    /// Whether to show the pet's chat line next to the menu bar icon (default off).
    @Published var showChatOnMenuBar: Bool {
        didSet {
            UserDefaults.standard.set(showChatOnMenuBar, forKey: "desktoppet.showChatMenuBar")
            refreshChatBubble()
        }
    }

    override init() {
        showChatOnMenuBar = (UserDefaults.standard.object(forKey: "desktoppet.showChatMenuBar") as? Bool) ?? false
        super.init()
    }

    /// Recomputes the menu bar title (called when the chat line changes).
    func refreshTitle() { refreshChatBubble() }

    func start() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = Self.menuBarImage()
        item.button?.imagePosition = .imageLeading
        item.button?.target = self
        item.button?.action = #selector(toggle)
        statusItem = item

        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        popover.appearance = NSAppearance(named: .darkAqua)
        let host = NSHostingController(rootView: MenuContentView(dismiss: { [weak self] in
            self?.popover.performClose(nil)
        }))
        host.sizingOptions = [.preferredContentSize]
        popover.contentViewController = host
    }

    /// Closes the popover when the user clicks anywhere outside it (including
    /// other apps / the desktop), which a transient popover can miss for a
    /// non-activating menu bar app.
    private var outsideClickMonitor: Any?

    @objc private func toggle() {
        guard let button = statusItem?.button else { return }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
    }

    /// The menu bar image: the paw alone.
    private static func menuBarImage() -> NSImage? {
        guard let paw = NSImage(systemSymbolName: "pawprint.fill", accessibilityDescription: "DesktopPet") else { return nil }
        paw.isTemplate = true
        return paw
    }

    // MARK: - Chat bubble dropping from the menu bar

    private var chatPanel: NSPanel?
    private var chatHideTimer: Timer?
    private var lastShownChat = ""

    private func refreshChatBubble() {
        let chat = PetController.shared.chatLine
        guard showChatOnMenuBar, !chat.isEmpty else {
            hideChatBubble()
            return
        }
        guard chat != lastShownChat else { return }
        lastShownChat = chat
        showChatBubble(chat)
    }

    private func showChatBubble(_ text: String) {
        guard let button = statusItem?.button, let buttonWindow = button.window else { return }

        let host = NSHostingView(rootView: MenuBarChatBubble(text: text))
        host.setFrameSize(host.fittingSize)
        let size = host.fittingSize

        let panel = chatPanel ?? {
            let p = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
            p.level = .popUpMenu
            p.isOpaque = false
            p.backgroundColor = .clear
            p.hasShadow = false
            p.ignoresMouseEvents = true
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            chatPanel = p
            return p
        }()
        panel.contentView = host
        panel.setContentSize(size)

        let frame = buttonWindow.convertToScreen(button.convert(button.bounds, to: nil))
        let originX = frame.midX - size.width / 2
        panel.setFrameOrigin(NSPoint(x: originX, y: frame.minY - size.height + 2))
        panel.orderFrontRegardless()

        chatHideTimer?.invalidate()
        chatHideTimer = Timer.scheduledTimer(withTimeInterval: 4, repeats: false) { _ in
            Task { @MainActor [weak self] in self?.hideChatBubble() }
        }
    }

    private func hideChatBubble() {
        chatHideTimer?.invalidate()
        chatPanel?.orderOut(nil)
        lastShownChat = ""
    }

    /// Shows the same popover anchored to an arbitrary view (e.g. the floating
    /// pet on right-click).
    func showPopover(relativeTo rect: NSRect, of view: NSView, edge: NSRectEdge) {
        if popover.isShown { popover.performClose(nil) }
        popover.show(relativeTo: rect, of: view, preferredEdge: edge)
    }

    // MARK: - Deferred close actions

    /// Action to run once the popover finishes its close animation.
    /// Use this instead of `DispatchQueue.main.asyncAfter` so the action fires
    /// at the exact moment the popover delegate confirms it is closed.
    private var pendingCloseAction: (() -> Void)?

    /// Closes the popover and invokes `action` only after the close animation
    /// has fully completed (via `NSPopoverDelegate.popoverDidClose`).
    func closeAndThen(_ action: @escaping () -> Void) {
        pendingCloseAction = action
        if popover.isShown {
            popover.performClose(nil)
        } else {
            // Already closed — fire immediately.
            let pending = pendingCloseAction
            pendingCloseAction = nil
            pending?()
        }
    }
}

extension StatusBarController: NSPopoverDelegate {
    func popoverDidShow(_ notification: Notification) {
        outsideClickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            self?.popover.performClose(nil)
        }
    }

    func popoverDidClose(_ notification: Notification) {
        if let monitor = outsideClickMonitor {
            NSEvent.removeMonitor(monitor)
            outsideClickMonitor = nil
        }
        // Fire any deferred action now that the close animation has finished.
        let pending = pendingCloseAction
        pendingCloseAction = nil
        pending?()
    }
}
