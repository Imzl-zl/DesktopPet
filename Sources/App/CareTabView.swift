import SwiftUI
import DesktopPetCore

/// The tamagotchi panel: level + evolution stage, hunger, today's feeding,
/// lifetime totals, and where the food data comes from.
struct CareTabView: View {
    @ObservedObject private var care = PetCareController.shared
    @ObservedObject private var pet = PetController.shared
    @ObservedObject private var imagePets = ImagePetStore.shared
    @Environment(\.openURL) private var openURL

    /// Ticks so hunger and "today" counters stay fresh while the panel is open.
    @State private var now = Date()
    private let tick = Timer.publish(every: 60, on: .main, in: .common).autoconnect()

    private var currentPack: ImagePetPack? {
        pet.selectedPetID.flatMap { imagePets.pack(id: $0) }
    }

    private var currentName: String {
        imagePets.displayName(for: pet.selectedPetID)
    }

    var body: some View {
        Form {
            Section("Companion") {
                companionCard
            }

            Section("Hunger") {
                hungerCard
            }

            Section("Today") {
                LabeledContent("Snacks eaten", value: "\(care.current.mealsToday)")
                LabeledContent("Streak") {
                    Text(care.current.streakDays == 1
                         ? NSLocalizedString("1 day", comment: "streak singular")
                         : String(format: NSLocalizedString("%d days", comment: "streak"), care.current.streakDays))
                    .foregroundStyle(Theme.textPrimary)
                }
            }

            Section("Lifetime") {
                LabeledContent("Total XP", value: Self.plain(care.current.xp))
                LabeledContent("Total snacks", value: "\(care.current.totalMeals)")
            }

            Section {
                achievementsGrid
                    .padding(.vertical, Theme.space1)
            } header: {
                Text("Achievements")
            } footer: {
                Text(verbatim: "\(care.achievements.count) of \(Achievement.allCases.count) unlocked")
                    .foregroundStyle(Theme.textMuted)
            }

            if care.raisedPetIDs.count > 1 {
                Section("All companions") {
                    ForEach(care.raisedPetIDs, id: \.self) { id in
                        companionRow(id: id)
                    }
                    Text("Each companion keeps its own experience. Switch pets in the Pet tab to raise another one.")
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                }
            }

        }
        .formStyle(.grouped)
        .onAppear {
            care.refreshDay()
        }
        .onReceive(tick) { date in
            now = date
            care.refreshDay()
        }
    }

    // MARK: - Companion hero

    private var companionCard: some View {
        HStack(spacing: Theme.space3) {
            Group {
                if let frame = currentPack?.clip(0).first {
                    Image(nsImage: frame).resizable().interpolation(.none).scaledToFit()
                        .padding(5)
                } else {
                    Image(systemName: stageIcon)
                        .font(.ui(size: 22, weight: .semibold))
                        .foregroundStyle(stageColor)
                }
            }
            .frame(width: 52, height: 52)
            .background(
                RoundedRectangle(cornerRadius: Theme.radiusMd, style: .continuous)
                    .fill(stageColor.opacity(0.14))
            )
            .overlay(
                RoundedRectangle(cornerRadius: Theme.radiusMd, style: .continuous)
                    .strokeBorder(stageColor.opacity(0.35), lineWidth: 1)
            )

            VStack(alignment: .leading, spacing: Theme.space1) {
                HStack(spacing: Theme.space2) {
                    Text(verbatim: currentName)
                        .font(.title3).bold()
                        .foregroundStyle(Theme.textPrimary)
                    Text(verbatim: "Lv \(care.level)")
                        .font(.title3)
                        .foregroundStyle(stageColor)
                    StageBadge(stageIndex: care.stageIndex, size: 22)
                    Text(NSLocalizedString(care.stageKey, comment: "evolution stage"))
                        .font(.caption).bold()
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(stageColor.opacity(0.18)))
                        .foregroundStyle(stageColor)
                }
                ProgressView(value: care.levelProgress)
                    .tint(stageColor)
                Text(xpCaption)
                    .font(.caption)
                    .foregroundStyle(Theme.textMuted)
                Text(String(format: NSLocalizedString("≈ %@ XP to Lv %d", comment: ""),
                            Self.plain(max(0, PetCare.xpToReach(level: care.level + 2) - care.current.xp)),
                            care.level + 1))
                    .font(.caption)
                    .foregroundStyle(stageColor)
            }
        }
        .padding(.vertical, Theme.space1)
    }

    // MARK: - Hunger

    private var hungerCard: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            HStack {
                Text(hungerLabel)
                    .foregroundStyle(Theme.textPrimary)
                Spacer()
                if let last = care.current.lastFedAt {
                    Text(String(format: NSLocalizedString("Last fed %@", comment: ""),
                                last.formatted(.relative(presentation: .named))))
                        .font(.caption)
                        .foregroundStyle(Theme.textMuted)
                }
            }
            ProgressView(value: fullness)
                .tint(fullness > 0.5 ? Theme.success : (fullness > 0.25 ? Theme.warning : Theme.danger))
            Text("The pet eats real work: chat with it, finish tasks, and let it summarize your day.")
                .font(.caption)
                .foregroundStyle(Theme.textMuted)
        }
        .padding(.vertical, Theme.space1)
    }

    // MARK: - Achievements

    private var achievementsGrid: some View {
        let unlocked = care.achievements
        return LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Theme.space2), count: 5), spacing: 14) {
            ForEach(Achievement.allCases, id: \.self) { a in
                let on = unlocked.contains(a)
                VStack(spacing: Theme.space1) {
                    Image(systemName: PetCare.achievementSymbol(a))
                        .font(.ui(size: 18))
                        .foregroundStyle(on ? stageColor : Theme.textDisabled)
                        .frame(height: 22)
                    Text(PetCare.achievementDisplayName(a))
                        .font(.ui(size: 9))
                        .multilineTextAlignment(.center)
                        .foregroundStyle(on ? Theme.textPrimary : Theme.textMuted)
                        .lineLimit(2)
                }
                .frame(maxWidth: .infinity)
                .opacity(on ? 1 : 0.5)
                .help("\(PetCare.achievementDisplayName(a)) — \(PetCare.achievementDescription(a))")
            }
        }
    }

    // MARK: - Companions

    @ViewBuilder
    private func companionRow(id: String) -> some View {
        let s = care.state(for: id)
        let lv = PetCare.displayLevel(forXP: s.xp)
        let idx = PetCare.stageIndex(forLevel: PetCare.level(forXP: s.xp))
        let color = Theme.stageColors[min(idx, Theme.stageColors.count - 1)].top
        HStack(spacing: Theme.space3) {
            Group {
                if let frame = imagePets.pack(id: id)?.clip(0).first {
                    Image(nsImage: frame).resizable().interpolation(.none).scaledToFit()
                } else {
                    Image(systemName: Theme.stageColors[min(idx, Theme.stageColors.count - 1)].glyph)
                        .font(.ui(size: 13))
                        .foregroundStyle(color)
                }
            }
            .frame(width: 24, height: 24)
            .overlay(alignment: .bottomTrailing) {
                StageBadge(stageIndex: idx, size: 13).offset(x: 3, y: 3)
            }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: Theme.space2) {
                    Text(verbatim: imagePets.displayName(for: id))
                        .font(.ui(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.textPrimary)
                    if id == care.currentPetID {
                        Text("Raising")
                            .font(.caption2).bold()
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Capsule().fill(Theme.accentSoft))
                            .foregroundStyle(Theme.accent)
                    }
                }
                ProgressView(value: PetCare.progress(forXP: s.xp))
                    .tint(color)
                    .controlSize(.small)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 2) {
                Text(verbatim: "Lv \(lv)")
                    .font(.ui(size: 12, weight: .bold))
                    .foregroundStyle(Theme.textPrimary)
                Text(verbatim: "\(Self.plain(s.xp)) XP")
                    .font(.caption2)
                    .foregroundStyle(Theme.textMuted)
            }
        }
        .padding(.vertical, Theme.space1)
    }

    // MARK: - Derived display

    private var stageIcon: String { Theme.stageColors[min(care.stageIndex, Theme.stageColors.count - 1)].glyph }
    private var stageColor: Color { Theme.stageColors[min(care.stageIndex, Theme.stageColors.count - 1)].top }

    private var xpCaption: String {
        let (inLevel, span) = PetCare.xpWithinLevel(forXP: care.current.xp)
        return String(format: NSLocalizedString("%@ / %@ XP to next level", comment: ""),
                      Self.plain(inLevel), Self.plain(span))
    }

    /// Continuous fullness 0…1 from the time since the last feeding (48h → empty).
    private var fullness: Double {
        guard let last = care.current.lastFedAt else { return 0.5 }
        let hours = now.timeIntervalSince(last) / 3600
        return max(0, min(1, 1 - hours / 48))
    }

    private var hungerLabel: String {
        switch care.hunger {
        case .full: return NSLocalizedString("Full", comment: "hunger")
        case .satisfied: return NSLocalizedString("Satisfied", comment: "hunger")
        case .peckish: return NSLocalizedString("Peckish", comment: "hunger")
        case .hungry: return NSLocalizedString("Hungry", comment: "hunger")
        case .starving: return NSLocalizedString("Starving", comment: "hunger")
        }
    }

    private static func plain(_ n: Int) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        return f.string(from: NSNumber(value: n)) ?? "\(n)"
    }
}
