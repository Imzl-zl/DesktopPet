import XCTest
@testable import DesktopPetCore

final class ChatWireFormatTests: XCTestCase {

    private func jsonObject(_ data: Data) throws -> [String: Any] {
        let obj = try JSONSerialization.jsonObject(with: data)
        return try XCTUnwrap(obj as? [String: Any])
    }

    // MARK: encode

    func test_encode_request_includes_model_and_plain_messages() throws {
        let messages = [
            ChatMessage(role: .system, content: "你是助手", toolCalls: nil, toolCallID: nil),
            ChatMessage(role: .user, content: "你好", toolCalls: nil, toolCallID: nil),
        ]
        let data = try ChatWireFormat.encodeRequestBody(model: "m1", messages: messages, tools: [])
        let body = try jsonObject(data)

        XCTAssertEqual(body["model"] as? String, "m1")
        let msgs = try XCTUnwrap(body["messages"] as? [[String: Any]])
        XCTAssertEqual(msgs.count, 2)
        XCTAssertEqual(msgs[0]["role"] as? String, "system")
        XCTAssertEqual(msgs[0]["content"] as? String, "你是助手")
        XCTAssertEqual(msgs[1]["role"] as? String, "user")
    }

    func test_encode_assistant_tool_calls_uses_nested_function_shape() throws {
        let messages = [
            ChatMessage(role: .assistant, content: "",
                        toolCalls: [ChatToolCall(id: "call_1", name: "get_weather", arguments: "{\"city\":\"Paris\"}")],
                        toolCallID: nil)
        ]
        let data = try ChatWireFormat.encodeRequestBody(model: "m1", messages: messages, tools: [])
        let body = try jsonObject(data)
        let msgs = try XCTUnwrap(body["messages"] as? [[String: Any]])
        let assistant = msgs[0]
        XCTAssertEqual(assistant["role"] as? String, "assistant")
        // content must be omitted (or null) when tool_calls are present.
        XCTAssertNil(assistant["content"])
        let calls = try XCTUnwrap(assistant["tool_calls"] as? [[String: Any]])
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0]["id"] as? String, "call_1")
        XCTAssertEqual(calls[0]["type"] as? String, "function")
        let fn = try XCTUnwrap(calls[0]["function"] as? [String: Any])
        XCTAssertEqual(fn["name"] as? String, "get_weather")
        XCTAssertEqual(fn["arguments"] as? String, "{\"city\":\"Paris\"}")
    }

    func test_encode_tool_result_message_carries_tool_call_id() throws {
        let messages = [
            ChatMessage(role: .tool, content: "sunny", toolCalls: nil, toolCallID: "call_9")
        ]
        let data = try ChatWireFormat.encodeRequestBody(model: "m1", messages: messages, tools: [])
        let body = try jsonObject(data)
        let msgs = try XCTUnwrap(body["messages"] as? [[String: Any]])
        XCTAssertEqual(msgs[0]["role"] as? String, "tool")
        XCTAssertEqual(msgs[0]["tool_call_id"] as? String, "call_9")
        XCTAssertEqual(msgs[0]["content"] as? String, "sunny")
    }

    func test_encode_tools_nests_function_under_type() throws {
        let tools = [
            AgentToolDescriptor(name: "get_weather", description: "天气",
                                parametersJSONSchema: "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}")
        ]
        let data = try ChatWireFormat.encodeRequestBody(model: "m1", messages: [], tools: tools)
        let body = try jsonObject(data)
        let arr = try XCTUnwrap(body["tools"] as? [[String: Any]])
        XCTAssertEqual(arr.count, 1)
        XCTAssertEqual(arr[0]["type"] as? String, "function")
        let fn = try XCTUnwrap(arr[0]["function"] as? [String: Any])
        XCTAssertEqual(fn["name"] as? String, "get_weather")
        let params = try XCTUnwrap(fn["parameters"] as? [String: Any])
        XCTAssertEqual(params["type"] as? String, "object")
    }

    // MARK: decode

    func test_decode_response_with_tool_calls() throws {
        let json = """
        {
          "choices": [{
            "finish_reason": "tool_calls",
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_1",
                "type": "function",
                "function": {"name": "get_weather", "arguments": "{\\"city\\":\\"Paris\\"}"}
              }]
            }
          }]
        }
        """
        let response = try ChatWireFormat.decodeResponse(Data(json.utf8))
        XCTAssertEqual(response.stopReason, .toolCalls)
        let assistant = try XCTUnwrap(response.messages.first)
        XCTAssertEqual(assistant.role, .assistant)
        let calls = try XCTUnwrap(assistant.toolCalls)
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0].id, "call_1")
        XCTAssertEqual(calls[0].name, "get_weather")
        XCTAssertEqual(calls[0].arguments, "{\"city\":\"Paris\"}")
    }

    func test_decode_response_plain_finish() throws {
        let json = """
        {"choices": [{"finish_reason": "stop", "message": {"role": "assistant", "content": "完成"}}]}
        """
        let response = try ChatWireFormat.decodeResponse(Data(json.utf8))
        XCTAssertEqual(response.stopReason, .finished)
        XCTAssertEqual(response.messages.first?.content, "完成")
        XCTAssertNil(response.messages.first?.toolCalls)
    }

    func test_decode_response_maps_length_to_max_tokens() throws {
        let json = """
        {"choices": [{"finish_reason": "length", "message": {"role": "assistant", "content": "..."}}]}
        """
        let response = try ChatWireFormat.decodeResponse(Data(json.utf8))
        XCTAssertEqual(response.stopReason, .maxTokens)
    }

    func test_decode_response_throws_on_empty_choices() {
        let json = #"{"choices": []}"#
        XCTAssertThrowsError(try ChatWireFormat.decodeResponse(Data(json.utf8)))
    }
}
