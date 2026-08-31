import Foundation

/// Errors produced by the agent loop.
enum AgentRuntimeError: Error, Equatable, Sendable {
    case maxIterationsExceeded
    case unknownTool(name: String)
    case toolFailed(name: String, message: String)
}

/// Outcome of a completed agent run.
struct AgentRunResult: Equatable, Sendable {
    var finalMessage: String
    var toolCallCount: Int
}

/// Drives the agent loop: prepend the skill system prompt + user request, ask
/// the model, execute any requested tools, feed results back, and repeat until
/// the model finishes (or the iteration budget is exhausted).
struct AgentRuntime: Sendable {
    var chat: any ChatSending
    var tools: [String: any AgentTool]
    var systemPrompt: String
    var maxIterations: Int

    init(chat: any ChatSending,
         tools: [String: any AgentTool],
         systemPrompt: String = "",
         maxIterations: Int = 10) {
        self.chat = chat
        self.tools = tools
        self.systemPrompt = systemPrompt
        self.maxIterations = maxIterations
    }

    /// Runs the loop for one user request. Returns the model's final message.
    func run(userRequest: String) async throws -> AgentRunResult {
        var messages: [ChatMessage] = []
        if !systemPrompt.isEmpty {
            messages.append(ChatMessage(role: .system, content: systemPrompt, toolCalls: nil, toolCallID: nil))
        }
        messages.append(ChatMessage(role: .user, content: userRequest, toolCalls: nil, toolCallID: nil))

        let descriptors = tools.values.map(\.descriptor)
        var toolCallCount = 0

        for _ in 0..<maxIterations {
            let response = try await chat.send(messages: messages, tools: descriptors)
            guard let assistant = response.messages.first(where: { $0.role == .assistant }) else {
                throw AgentRuntimeError.toolFailed(name: "", message: "model returned no assistant message")
            }
            messages.append(assistant)

            guard let calls = assistant.toolCalls, !calls.isEmpty else {
                return AgentRunResult(finalMessage: assistant.content, toolCallCount: toolCallCount)
            }

            toolCallCount += calls.count
            for call in calls {
                guard let tool = tools[call.name] else {
                    throw AgentRuntimeError.unknownTool(name: call.name)
                }
                do {
                    let result = try await tool.execute(argumentsJSON: call.arguments)
                    messages.append(ChatMessage(role: .tool, content: result, toolCalls: nil, toolCallID: call.id))
                } catch {
                    throw AgentRuntimeError.toolFailed(name: call.name, message: String(describing: error))
                }
            }
        }
        throw AgentRuntimeError.maxIterationsExceeded
    }
}
