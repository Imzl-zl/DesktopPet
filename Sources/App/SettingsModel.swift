import AppKit
import Foundation
@preconcurrency import UserNotifications
import DesktopPetCore

/// Backs the Settings window: notification permission status with the actions
/// to change it. (The original agent-hook management was removed in the
/// pure-pet edition.)
@MainActor
final class SettingsModel: ObservableObject {
    static let shared = SettingsModel()

    enum NotificationState: Equatable {
        case unavailable   // running as bare binary, no bundle
        case notDetermined
        case enabled
        case denied
    }

    @Published private(set) var notificationState: NotificationState = .notDetermined

    /// In-app notification toggle: lets users mute alerts even after granting
    /// the macOS permission. Defaults to on.
    @Published var notificationsEnabled: Bool {
        didSet { UserDefaults.standard.set(notificationsEnabled, forKey: NotificationManager.enabledKey) }
    }

    init() {
        notificationsEnabled = (UserDefaults.standard.object(forKey: NotificationManager.enabledKey) as? Bool) ?? true
    }

    func refresh() {
        refreshNotificationState()
    }

    func enableNotifications() {
        guard NotificationManager.shared.isAvailable else { return }
        Task { @MainActor in
            _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
            self.refreshNotificationState()
        }
    }

    /// Opens System Settings to DesktopPet's notification pane (used when denied).
    func openSystemNotificationSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.notifications") {
            NSWorkspace.shared.open(url)
        }
    }

    private func refreshNotificationState() {
        guard NotificationManager.shared.isAvailable else {
            notificationState = .unavailable
            return
        }
        Task { @MainActor in
            let settings = await UNUserNotificationCenter.current().notificationSettings()
            switch settings.authorizationStatus {
            case .authorized, .provisional, .ephemeral:
                self.notificationState = .enabled
            case .denied:
                self.notificationState = .denied
            default:
                self.notificationState = .notDetermined
            }
        }
    }
}
