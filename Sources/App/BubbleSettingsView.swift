import SwiftUI
import DesktopPetCore

// MARK: - BubbleSettingsView

struct BubbleSettingsView: View {
    @ObservedObject private var settings = BubbleSettings.shared
    @ObservedObject private var pet = PetController.shared
    @ObservedObject private var chat = ChatSettings.shared

    var body: some View {
        Form {
            appearanceSection
            messageSection
        }
        .formStyle(.grouped)
    }

    /// Global look of the chat bubble: theme, opacity, font size.
    private var appearanceSection: some View {
        Section {
            HStack {
                Text("Theme")
                Spacer()
                Picker("Theme", selection: $settings.theme) {
                    ForEach(BubbleSettings.Theme.allCases, id: \.self) {
                        Text($0.displayName).tag($0)
                    }
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .labelsHidden()
            }

            HStack {
                Text("Font size")
                Spacer()
                Picker("Font size", selection: $settings.fontSize) {
                    Text("S").tag(BubbleSettings.FontSize.small)
                    Text("M").tag(BubbleSettings.FontSize.medium)
                    Text("L").tag(BubbleSettings.FontSize.large)
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .labelsHidden()
            }

            HStack {
                Text("Opacity")
                Slider(value: $settings.opacity, in: 0.6...1.0)
                Text("\(Int(settings.opacity * 100))%")
                    .monospacedDigit()
                    .lineLimit(1)
                    .fixedSize()
                    .foregroundStyle(.secondary)
                    .frame(width: 48, alignment: .trailing)
            }

            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Show idle message")
                    Text("The pet's chatter while it is resting.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                ColorSwitch(isOn: $pet.showIdleMessage)
            }
        } header: {
            Text("Appearance")
        }
    }

    /// Chat lines per mood: the built-in system set, or custom lines the user
    /// types. Drives the pet's speech bubble.
    private var messageSection: some View {
        Section {
            HStack {
                Text("Show chat bubble")
                Spacer()
                ColorSwitch(isOn: $pet.showChat)
            }
            Picker("Messages", selection: $chat.source) {
                Text("System").tag(ChatSettings.Source.system)
                Text("Custom").tag(ChatSettings.Source.custom)
            }
            .pickerStyle(.segmented)
            if chat.source == .custom {
                ForEach(ChatSettings.editableMoods, id: \.self) { mood in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(moodLabel(mood)).font(.caption).foregroundStyle(.secondary)
                        GrowingTextEditor(text: Binding(
                            get: { chat.text(for: mood) },
                            set: { chat.setText($0, for: mood) }
                        ))
                        .padding(4)
                        .background(RoundedRectangle(cornerRadius: 6).fill(Color(white: 0.16)))
                        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.white.opacity(0.12)))
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                HStack {
                    Text("One message per line; a random one is shown.")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("Reset to defaults") { chat.resetToDefaults() }
                        .controlSize(.small)
                }
            }
        } header: {
            Text("Chat bubble")
        }
    }

    private func moodLabel(_ mood: PetMood) -> String {
        switch mood {
        case .working:   return NSLocalizedString("Working", comment: "pet mood")
        case .waiting:   return NSLocalizedString("Waiting", comment: "pet mood")
        case .done:      return NSLocalizedString("Done", comment: "pet mood")
        case .celebrate: return NSLocalizedString("Celebrate", comment: "pet mood")
        case .idle:      return NSLocalizedString("Idle", comment: "pet mood")
        case .sleepy:    return NSLocalizedString("Sleepy", comment: "pet mood")
        case .levelup:   return NSLocalizedString("Level up", comment: "pet mood")
        }
    }
}
