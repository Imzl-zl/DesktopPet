import AppKit
import DesktopPetCore

/// Plays a sound on the pet's own events: being clicked, and the break
/// reminder firing. Each event has its own on/off and sound choice (a built-in
/// macOS system sound, or a custom file the user uploads). Custom files are
/// copied into `~/.desktoppet/sounds/`.
@MainActor
final class SoundSettings: ObservableObject {
    static let shared = SoundSettings()

    enum Event: String { case click, breakReminder }

    @Published var clickEnabled: Bool { didSet { save() } }
    @Published var breakReminderEnabled: Bool { didSet { save() } }
    /// "" means use the built-in default; otherwise a custom file path.
    @Published var clickCustomPath: String { didSet { save() } }
    @Published var breakReminderCustomPath: String { didSet { save() } }

    /// Built-in macOS system sounds used as defaults.
    static let defaultClick = "Pop"
    static let defaultBreakReminder = "Purr"

    private var soundsDir: URL {
        URL(fileURLWithPath: DesktopPetPaths.baseDir).appendingPathComponent("sounds")
    }

    init() {
        let d = UserDefaults.standard
        clickEnabled = (d.object(forKey: "desktoppet.sound.click.on") as? Bool) ?? true
        breakReminderEnabled = (d.object(forKey: "desktoppet.sound.breakReminder.on") as? Bool) ?? true
        clickCustomPath = d.string(forKey: "desktoppet.sound.click.path") ?? ""
        breakReminderCustomPath = d.string(forKey: "desktoppet.sound.breakReminder.path") ?? ""
    }

    func isEnabled(_ event: Event) -> Bool {
        event == .click ? clickEnabled : breakReminderEnabled
    }

    func customPath(_ event: Event) -> String {
        event == .click ? clickCustomPath : breakReminderCustomPath
    }

    /// Plays the configured sound for an event, if enabled.
    func play(_ event: Event) {
        guard isEnabled(event) else { return }
        let sound: NSSound?
        let path = customPath(event)
        if !path.isEmpty, FileManager.default.fileExists(atPath: path) {
            sound = NSSound(contentsOfFile: path, byReference: true)
        } else {
            sound = NSSound(named: event == .click ? Self.defaultClick : Self.defaultBreakReminder)
        }
        sound?.stop()
        sound?.play()
    }

    /// Prompts for an audio file and sets it as the custom sound for an event.
    func upload(for event: Event) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose a sound file"
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let fm = FileManager.default
        try? fm.createDirectory(at: soundsDir, withIntermediateDirectories: true)
        let dest = soundsDir.appendingPathComponent("\(event.rawValue).\(url.pathExtension)")
        try? fm.removeItem(at: dest)
        do {
            try fm.copyItem(at: url, to: dest)
        } catch {
            return
        }
        setCustomPath(dest.path, for: event)
        play(event)   // preview
    }

    func resetToDefault(_ event: Event) {
        setCustomPath("", for: event)
    }

    private func setCustomPath(_ path: String, for event: Event) {
        if event == .click { clickCustomPath = path } else { breakReminderCustomPath = path }
    }

    private func save() {
        let d = UserDefaults.standard
        d.set(clickEnabled, forKey: "desktoppet.sound.click.on")
        d.set(breakReminderEnabled, forKey: "desktoppet.sound.breakReminder.on")
        d.set(clickCustomPath, forKey: "desktoppet.sound.click.path")
        d.set(breakReminderCustomPath, forKey: "desktoppet.sound.breakReminder.path")
    }
}
