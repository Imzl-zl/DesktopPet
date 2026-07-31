import AppKit
import SwiftUI
import DesktopPetCore

// MARK: - Animations environment key

private struct AnimationsEnabledKey: EnvironmentKey { static let defaultValue = true }
extension EnvironmentValues {
    var animationsEnabled: Bool {
        get { self[AnimationsEnabledKey.self] }
        set { self[AnimationsEnabledKey.self] = newValue }
    }
}

/// Timing for `AnimatedStatusText`'s erase/retype/ellipsis-cycle phases.
private let ERASE_INTERVAL: TimeInterval = 0.080
private let TYPE_INTERVAL: TimeInterval = 0.045
private let DOT_CYCLE_INTERVAL: TimeInterval = 0.400

/// The pet sprite alone (imported pack, reacting to mood). Shows a paw
/// placeholder if no pet is selected yet. The pet id and mood come from the
/// per-window model rather than the global `PetController`.
struct PetView: View {
    @ObservedObject var model: PetWindowModel
    var size: CGFloat = 120
    @ObservedObject private var imagePets = ImagePetStore.shared
    @ObservedObject private var bindings = PetBindingsStore.shared
    @ObservedObject private var pet = PetController.shared

    var body: some View {
        content
            .frame(width: size, height: size)
            .contentShape(Rectangle())
    }

    @ViewBuilder private var content: some View {
        if let id = model.petID, let pack = imagePets.pack(id: id) {
            let clip = bindings.clipIndex(packId: pack.id, clipCount: pack.clipCount, mood: model.mood)
            ImageSpriteView(frames: pack.clip(clip), mood: model.mood,
                            fps: pet.spriteFPS(forMood: model.mood), size: size)
        } else {
            Image(systemName: "pawprint.fill")
                .font(.system(size: size * 0.4))
                .foregroundStyle(.secondary)
        }
    }
}

/// Reports the natural size of the pet + bubble so the window can hug its content.
private struct PetContentSizeKey: PreferenceKey {
    static var defaultValue: CGSize { .zero }
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        let next = nextValue()
        if next.width > 0, next.height > 0 { value = next }
    }
}

/// The full floating window content: a chat bubble above the pet. Per-window
/// fields (mood/petID/sessions/chatLine) come from `model`; global toggles and
/// the tap-interaction state still come from `PetController.shared`.
struct FloatingPetView: View {
    @ObservedObject var model: PetWindowModel
    @ObservedObject private var pet = PetController.shared
    @ObservedObject private var appLang = AppLanguage.shared

    var body: some View {
        VStack(spacing: 2) {
            if pet.showChat && model.petID != nil {
                if !model.chatLine.isEmpty {
                    ChatBubble(text: model.chatLine,
                               projectName: pet.splitPet ? model.projectName : nil)
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .transition(AnyTransition.scale(scale: 0.6).combined(with: .opacity))
                }
            }
            PetView(model: model, size: pet.petPoint)
                .overlay {
                    if model.petTapCount > 0 {
                        PetHearts(size: pet.petPoint)
                            .id(model.petTapCount)
                    }
                }
                .overlay(alignment: .top) {
                    if !model.petReactionLine.isEmpty {
                        Text(model.petReactionLine)
                            .font(.system(size: 13, weight: .medium))
                            .foregroundStyle(.primary.opacity(0.85))
                            .padding(.horizontal, 10)
                            .padding(.vertical, 5)
                            .background(
                                Capsule()
                                    .fill(.regularMaterial)
                            )
                            .overlay(
                                Capsule()
                                    .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
                            )
                            .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                            .offset(y: -16)
                            .transition(.asymmetric(
                                insertion: .scale(scale: 0.5, anchor: .bottom).combined(with: .opacity),
                                removal: .opacity
                            ))
                    }
                }
                .scaleEffect(
                    x: model.isPetted ? 1.12 : 1.0,
                    y: model.isPetted ? 0.82 : 1.0,
                    anchor: .bottom
                )
                .animation(.interpolatingSpring(stiffness: 300, damping: 8), value: model.isPetted)
                .onTapGesture {
                    model.petTap()
                }
        }
        .fixedSize(horizontal: true, vertical: true)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: model.petReactionLine)
        .background(
            GeometryReader { proxy in
                Color.clear.preference(key: PetContentSizeKey.self, value: proxy.size)
            }
        )
        .onPreferenceChange(PetContentSizeKey.self) { [key = model.key] size in
            PetWindowController.shared.resizeToContent(size, forKey: key)
        }
        .animation(.easeInOut(duration: 0.22), value: model.chatLine)
        .animation(.spring(response: 0.35, dampingFraction: 0.7), value: model.activities.count)
        .animation(.easeInOut, value: pet.showChat)
        // Re-resolve bubble text when the app language changes at runtime.
        .environment(\.locale, appLang.locale)
        .environment(\.animationsEnabled, pet.animationsEnabled)
    }
}

// MARK: - Simple Chat Bubble (celebrate / done / waiting fallback)

/// A plain speech bubble with a downward tail, used for celebrate/done lines.
/// Theme-aware (light/dark/system); reused by the Settings live preview.
/// When `projectName` is non-nil (split-pet mode), a small dimmed caption is
/// rendered above the main text so the window is identifiable.
struct ChatBubble: View {
    let text: String
    var projectName: String? = nil
    @ObservedObject private var settings = BubbleSettings.shared

    private var fill: Color {
        switch settings.theme {
        case .light:  return Color.white.opacity(settings.opacity)
        case .dark:   return Color(nsColor: .windowBackgroundColor).opacity(settings.opacity)
        case .system: return Color(nsColor: .textBackgroundColor).opacity(settings.opacity)
        }
    }

    private var textColor: Color {
        switch settings.theme {
        case .light:  return .black.opacity(0.85)
        case .dark:   return .white.opacity(0.85)
        case .system: return Color.primary.opacity(0.85)
        }
    }

    private var dimmedTextColor: Color {
        switch settings.theme {
        case .light:  return .black.opacity(0.45)
        case .dark:   return .white.opacity(0.45)
        case .system: return Color.primary.opacity(0.45)
        }
    }

    private var borderColor: Color {
        switch settings.theme {
        case .light:  return .black.opacity(0.06)
        case .dark:   return .white.opacity(0.12)
        case .system: return Color.primary.opacity(0.08)
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 2) {
                if let name = projectName {
                    Text(name)
                        .font(.system(size: settings.fontSize.secondaryPt, weight: .regular))
                        .foregroundStyle(dimmedTextColor)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Text(text)
                    .font(.system(size: settings.fontSize.primaryPt, weight: .medium))
                    .foregroundStyle(textColor)
                    .contentTransition(.opacity)   // cross-fade text changes instead of a hard swap (no flicker)
                    .lineLimit(1)
                    .truncationMode(.tail)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(Capsule().fill(fill))
            .overlay(Capsule().strokeBorder(borderColor, lineWidth: 1))
            // Flatten to one layer so the shadow traces the capsule's
            // rounded silhouette instead of its rectangular bounding box
            // (SwiftUI draws boxy shadows on composed views otherwise).
            .compositingGroup()
            .shadow(color: .black.opacity(0.18), radius: 5, y: 2)
            Triangle()
                .fill(fill)
                .frame(width: 12, height: 7)
        }
        .fixedSize(horizontal: true, vertical: true)
        .frame(maxWidth: 420)
    }
}

private struct Triangle: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        p.addLine(to: CGPoint(x: rect.midX, y: rect.maxY))
        p.closeSubpath()
        return p
    }
}
