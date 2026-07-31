import Foundation
import SwiftUI
import DesktopPetCore

// MARK: - BubbleSettings

@MainActor
final class BubbleSettings: ObservableObject {
    static let shared = BubbleSettings()

    enum FontSize: String, CaseIterable, Codable {
        case small, medium, large
        var primaryPt: CGFloat   { switch self { case .small: 10; case .medium: 12; case .large: 14 } }
        var secondaryPt: CGFloat { switch self { case .small: 9;  case .medium: 10.5; case .large: 12 } }
    }

    enum Theme: String, CaseIterable, Codable {
        case light, dark, system
        var displayName: String { NSLocalizedString(rawValue.capitalized, comment: "bubble theme") }
    }

    // MARK: Published properties

    @Published var fontSize: FontSize {
        didSet { ud.set(fontSize.rawValue, forKey: Keys.fontSize) }
    }
    @Published var opacity: Double {
        didSet { ud.set(opacity, forKey: Keys.opacity) }
    }
    @Published var theme: Theme {
        didSet { ud.set(theme.rawValue, forKey: Keys.theme) }
    }

    // MARK: Private

    private let ud = UserDefaults.standard

    private enum Keys {
        static let fontSize = "desktoppet.bubble.fontSize"
        static let opacity  = "desktoppet.bubble.opacity"
        static let theme    = "desktoppet.bubble.theme"
    }

    init() {
        fontSize = FontSize(rawValue: ud.string(forKey: Keys.fontSize) ?? "") ?? .medium
        opacity  = ud.object(forKey: Keys.opacity) as? Double ?? 1.0
        theme    = Theme(rawValue: ud.string(forKey: Keys.theme) ?? "") ?? .system
    }
}
