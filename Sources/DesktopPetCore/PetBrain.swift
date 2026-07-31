import Foundation

/// A single chat turn exchanged with the pet.
public struct ChatTurn: Codable, Sendable, Equatable {
    public enum Role: String, Codable, Sendable {
        case user
        case assistant
        case system
    }

    public var role: Role
    public var text: String
    // Future: audio attachment for voice chat (Phase 2).

    public init(role: Role, text: String) {
        self.role = role
        self.text = text
    }
}

/// Multimodal capability adapter: one protocol for chat, daily summary and
/// image generation. Phase 1 ships the protocol plus `NoopBrain`; real
/// providers (OpenAI-compatible, Ollama, Anthropic) land in Phase 2/3.
public protocol PetBrain: Sendable {
    var providerName: String { get }
    /// Real-time conversation (text in Phase 2; audio later).
    func chat(_ turns: [ChatTurn]) async throws -> String
    /// Daily summary from the day's activities.
    func summarize(activities: [ActivityEvent]) async throws -> String
    /// Generates a recap image (PNG/JPEG data) from a prompt.
    func generateImage(prompt: String) async throws -> Data
}

/// Default brain before a model is configured: answers with placeholders.
public struct NoopBrain: PetBrain {
    public var providerName: String { "none" }

    public init() {}

    public func chat(_ turns: [ChatTurn]) async throws -> String {
        "…"
    }

    public func summarize(activities: [ActivityEvent]) async throws -> String {
        ""
    }

    public func generateImage(prompt: String) async throws -> Data {
        Data()
    }
}
