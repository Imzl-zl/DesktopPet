import Foundation

/// Role of a chat message.
enum ChatRole: String, Codable, Equatable, Sendable {
    case system, user, assistant, tool
}

/// One message in the conversation sent to the model. Flat representation:
/// `role == .assistant` may carry `toolCalls`; `role == .tool` carries the
/// execution result via `toolCallID` + `content`. JSON wire encoding lives in
/// the App-layer client, this model stays platform-independent.
struct ChatMessage: Equatable, Sendable {
    var role: ChatRole
    var content: String
    var toolCalls: [ChatToolCall]?
    var toolCallID: String?
}

/// A tool invocation requested by the assistant.
struct ChatToolCall: Equatable, Sendable {
    var id: String
    var name: String
    /// JSON object string of the tool arguments.
    var arguments: String
}

/// Why the model stopped the current turn.
enum StopReason: Equatable, Sendable {
    case toolCalls
    case finished
    case maxTokens
    case error(String)
}

/// Parsed response from the chat model for one request.
struct ChatResponse: Equatable, Sendable {
    var messages: [ChatMessage]
    var stopReason: StopReason
}

/// Network abstraction for chat completion. The URLSession implementation
/// lives in the App layer; the agent runtime depends only on this protocol so
/// it stays unit-testable with a scripted double.
protocol ChatSending: Sendable {
    func send(messages: [ChatMessage], tools: [AgentToolDescriptor]) async throws -> ChatResponse
}

/// Describes a tool to the model (name, purpose, JSON Schema for arguments).
struct AgentToolDescriptor: Equatable, Sendable {
    var name: String
    var description: String
    var parametersJSONSchema: String
}

/// A tool the agent can invoke during a run.
protocol AgentTool: Sendable {
    var descriptor: AgentToolDescriptor { get }
    /// Executes with the JSON-encoded arguments and returns a JSON/text result
    /// that is fed back to the model as a `.tool` message.
    func execute(argumentsJSON: String) async throws -> String
}
