import SwiftUI
import AppKit
import DesktopPetCore

/// Rich menu bar popover: a blurred dark card with an arrow pointing at the
/// status item, the companion's stats, quick controls, and a footer bar.
struct MenuContentView: View {
    @ObservedObject private var petWindow = PetWindowController.shared
    @ObservedObject private var statusBar = StatusBarController.shared
    @ObservedObject private var pet = PetController.shared
    var dismiss: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header
            divider
            careSection
            divider
            controls
            divider
            footer
        }
        .frame(width: 300)
        .background(Theme.background)
        .themedCard(padding: 0, radius: Theme.radiusXl, shadow: true)
        .environment(\.colorScheme, .dark)
        .noFocusRing()
    }

    private var divider: some View { Divider().overlay(Theme.cardStrokeStrong) }

    // MARK: Header

    private var header: some View {
        HStack(spacing: Theme.space3) {
            ZStack {
                RoundedRectangle(cornerRadius: Theme.radiusSm, style: .continuous)
                    .fill(Theme.accent)
                Image(systemName: "pawprint.fill")
                    .font(.ui(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: 28, height: 28)
            .shadow(color: Theme.accentGlow, radius: 8, y: 2)

            VStack(alignment: .leading, spacing: 1) {
                Text("DesktopPet")
                    .font(.ui(size: 14, weight: .bold))
                    .foregroundStyle(Theme.textPrimary)
                Text(subtitle)
                    .font(.ui(size: 11))
                    .foregroundStyle(Theme.textMuted)
            }
            Spacer()
        }
        .padding(Theme.space4)
    }

    private var subtitle: String {
        switch pet.mood {
        case .working: return NSLocalizedString("Busy with something", comment: "popover subtitle")
        case .waiting: return NSLocalizedString("Waiting on you", comment: "popover subtitle")
        case .done: return NSLocalizedString("Just finished", comment: "popover subtitle")
        default: return NSLocalizedString("Your little companion", comment: "popover subtitle")
        }
    }

    // MARK: Companion (care stats)

    @ObservedObject private var care = PetCareController.shared
    @ObservedObject private var imagePets = ImagePetStore.shared

    private var careSection: some View {
        let level = care.level
        let idx = min(care.stageIndex, Theme.stageColors.count - 1)
        let color = Theme.stageColors[idx].top
        let name = imagePets.displayName(for: pet.selectedPetID)

        return VStack(alignment: .leading, spacing: Theme.space2) {
            EyebrowLabel("Companion")
                .padding(.horizontal, Theme.space4)
                .padding(.top, Theme.space3)
                .padding(.bottom, Theme.space1)

            HStack(spacing: Theme.space2) {
                StageBadge(stageIndex: idx, size: 20)
                Text(name)
                    .font(.ui(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.textPrimary)
                    .lineLimit(1).truncationMode(.tail)
                Text(verbatim: "Lv \(level)")
                    .font(.ui(size: 12, weight: .bold))
                    .foregroundStyle(color)
                    .layoutPriority(1)
                Spacer(minLength: Theme.space2)
                Text(hungerText)
                    .font(.ui(size: 11))
                    .foregroundStyle(Theme.textMuted)
                    .lineLimit(1).layoutPriority(1)
            }
            .padding(.horizontal, Theme.space4)

            ProgressView(value: care.levelProgress)
                .tint(color)
                .controlSize(.small)
                .padding(.horizontal, Theme.space4)

            HStack {
                Text(verbatim: xpLine)
                Spacer()
                Text(verbatim: todayLine)
            }
            .font(.ui(size: 10))
            .foregroundStyle(Theme.textMuted)
            .padding(.horizontal, Theme.space4)
            .padding(.bottom, Theme.space3)
        }
    }

    private var xpLine: String {
        let (inLevel, span) = PetCare.xpWithinLevel(forXP: care.current.xp)
        return "\(inLevel) / \(span) XP"
    }

    private var todayLine: String {
        let meals = care.current.mealsToday
        if meals == 1 {
            return NSLocalizedString("Today 1 snack", comment: "popover care today line, singular")
        }
        return String(
            format: NSLocalizedString("Today %d snacks", comment: "popover care today line"),
            meals
        )
    }

    private var hungerText: String {
        switch care.hunger {
        case .full: return NSLocalizedString("Full", comment: "hunger")
        case .satisfied: return NSLocalizedString("Satisfied", comment: "hunger")
        case .peckish: return NSLocalizedString("Peckish", comment: "hunger")
        case .hungry: return NSLocalizedString("Hungry", comment: "hunger")
        case .starving: return NSLocalizedString("Starving", comment: "hunger")
        }
    }

    // MARK: Controls

    private var controls: some View {
        VStack(spacing: 0) {
            controlRow(icon: "pawprint", label: "Show pet", isOn: $petWindow.isVisible)
            controlRow(icon: "bubble.left", label: "Show chat on menu bar", isOn: $statusBar.showChatOnMenuBar)
            controlRow(icon: "square.split.2x1", label: "Split pet", isOn: $pet.splitPet)
            animationRow
            sizeRow
        }
    }

    private var animationRow: some View {
        HStack(spacing: Theme.space2) {
            Image(systemName: "play.fill")
                .foregroundStyle(Theme.textSecondary)
                .frame(width: 16)
            Text("Animate pets")
                .font(.ui(size: 13))
                .foregroundStyle(Theme.textPrimary)
            Spacer()
            if pet.animationsEnabled {
                HStack(spacing: Theme.space1) {
                    Slider(value: $pet.animationFPS, in: 1...12, step: 1)
                        .controlSize(.mini)
                        .tint(Theme.accent)
                        .frame(width: 80)
                    Text("\(Int(pet.animationFPS))")
                        .font(.ui(size: 10, weight: .medium, design: .monospaced))
                        .foregroundStyle(Theme.textMuted)
                        .fixedSize()
                }
            }
            ColorSwitch(isOn: $pet.animationsEnabled)
        }
        .padding(.horizontal, Theme.space4)
        .padding(.vertical, Theme.space2)
        .animation(Theme.easeMedium, value: pet.animationsEnabled)
    }

    private var sizeRow: some View {
        HStack(spacing: Theme.space2) {
            Image(systemName: "arrow.up.left.and.arrow.down.right")
                .foregroundStyle(Theme.textSecondary)
                .frame(width: 16)
            Text("Pet size")
                .font(.ui(size: 13))
                .foregroundStyle(Theme.textPrimary)
            Slider(value: $pet.petPoint, in: PetController.minPoint...PetController.maxPoint)
                .controlSize(.mini)
                .tint(Theme.accent)
        }
        .padding(.horizontal, Theme.space4)
        .padding(.vertical, Theme.space2)
    }

    private func controlRow(icon: String, label: String, isOn: Binding<Bool>) -> some View {
        HStack(spacing: Theme.space2) {
            Image(systemName: icon)
                .foregroundStyle(Theme.textSecondary)
                .frame(width: 16)
            Text(label)
                .font(.ui(size: 13))
                .foregroundStyle(Theme.textPrimary)
            Spacer()
            ColorSwitch(isOn: isOn)
        }
        .padding(.horizontal, Theme.space4)
        .padding(.vertical, Theme.space2)
    }

    // MARK: Footer

    @ObservedObject private var updater = UpdaterController.shared

    private var footer: some View {
        HStack(spacing: Theme.space3) {
            FooterButton(icon: "sparkles", label: "Generate") {
                StatusBarController.shared.closeAndThen {
                    ImageGenWindowController.shared.show()
                }
            }
            FooterButton(icon: "gearshape", label: "Settings") {
                StatusBarController.shared.closeAndThen {
                    SettingsWindowController.shared.show()
                }
            }
            FooterButton(
                icon: "arrow.triangle.2.circlepath",
                label: "Updates",
                badge: updater.updatePending
            ) {
                StatusBarController.shared.closeAndThen {
                    UpdaterController.shared.checkForUpdates()
                }
            }
            Spacer()
            FooterButton(icon: "power", label: "Quit") {
                NSApplication.shared.terminate(nil)
            }
        }
        .padding(.horizontal, Theme.space4)
        .padding(.vertical, Theme.space3)
    }
}

private struct FooterButton: View {
    let icon: String
    let label: String
    var badge: Bool = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                ZStack(alignment: .topTrailing) {
                    Image(systemName: icon)
                        .font(.ui(size: 13, weight: .medium))
                    if badge {
                        Circle()
                            .fill(Theme.warning)
                            .frame(width: 6, height: 6)
                            .offset(x: 4, y: -4)
                    }
                }
                Text(label)
            }
            .font(.ui(size: 12, weight: .medium))
            .foregroundStyle(Theme.textSecondary)
        }
        .buttonStyle(PillButtonStyle())
    }
}
