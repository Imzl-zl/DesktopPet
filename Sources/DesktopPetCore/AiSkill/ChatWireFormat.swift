import Foundation

/// Errors from chat wire encoding/decoding.
enum ChatWireError: Error, Equatable, Sendable {
    case invalidResponse
}

/// Wire format for OpenAI-compatible Chat Completions (`/v1/chat/completions`),
/// including function/tool calling. Format verified against the official docs:
/// - tools: `{"type":"function","function":{name,description,parameters}}`
/// - assistant tool calls: `message.tool_calls[]` with `{id,type:"function",function:{name,arguments}}`
/// - tool result: `{"role":"tool","tool_call_id":id,"content":...}`
/// - `finish_reason`: `stop | length | tool_calls | content_filter | function_call`
///
/// Pure JSON (Foundation `JSONSerialization`), platform-independent so it can be
/// unit-tested on Windows; the URLSession layer lives in the App target.
enum ChatWireFormat {
    static func encodeRequestBody(model: String,
                                  messages: [ChatMessage],
                                  tools: [AgentToolDescriptor],
                                  toolChoice: String = "auto") throws -> Data {
        var body: [String: Any] = ["model": model]
        body["messages"] = messages.map(messageJSON)
        if !tools.isEmpty {
            body["tools"] = tools.map(toolJSON)
            body["tool_choice"] = toolChoice
        }
        return try JSONSerialization.data(withJSONObject: body)
    }

    static func decodeResponse(_ data: Data) throws -> ChatResponse {
        let object = try JSONSerialization.jsonObject(with: data)
        guard let root = object as? [String: Any],
              let choices = root["choices"] as? [[String: Any]],
              let choice = choices.first else {
            throw ChatWireError.invalidResponse
        }

        let finish = (choice["finish_reason"] as? String) ?? ""
        let stopReason: StopReason
        switch finish {
        case "tool_calls": stopReason = .toolCalls
        case "stop": stopReason = .finished
        case "length": stopReason = .maxTokens
        default: stopReason = .error(finish)
        }

        let message = choice["message"] as? [String: Any] ?? [:]
        var toolCalls: [ChatToolCall]?
        if let calls = message["tool_calls"] as? [[String: Any]] {
            toolCalls = calls.compactMap { call in
                guard let id = call["id"] as? String,
                      let function = call["function"] as? [String: Any],
                      let name = function["name"] as? String,
                      let arguments = function["arguments"] as? String else { return nil }
                return ChatToolCall(id: id, name: name, arguments: arguments)
            }
        }

        let content = message["content"] as? String ?? ""
        let assistant = ChatMessage(role: .assistant, content: content,
                                    toolCalls: toolCalls, toolCallID: nil)
        return ChatResponse(messages: [assistant], stopReason: stopReason)
    }

    // MARK: - Encoding helpers

    private static func messageJSON(_ message: ChatMessage) -> [String: Any] {
        switch message.role {
        case .system, .user:
            return ["role": message.role.rawValue, "content": message.content]
        case .assistant:
            var json: [String: Any] = ["role": "assistant"]
            if let calls = message.toolCalls, !calls.isEmpty {
                // content is omitted when tool_calls are present (per API spec).
                json["tool_calls"] = calls.map { call in
                    ["id": call.id,
                     "type": "function",
                     "function": ["name": call.name, "arguments": call.arguments]]
                }
            } else {
                json["content"] = message.content
            }
            return json
        case .tool:
            return ["role": "tool",
                    "tool_call_id": message.toolCallID ?? "",
                    "content": message.content]
        }
    }

    private static func toolJSON(_ descriptor: AgentToolDescriptor) -> [String: Any] {
        var function: [String: Any] = ["name": descriptor.name,
                                       "description": descriptor.description]
        if let params = try? JSONSerialization.jsonObject(with: Data(descriptor.parametersJSONSchema.utf8))
            as? [String: Any] {
            function["parameters"] = params
        }
        return ["type": "function", "function": function]
    }
}
