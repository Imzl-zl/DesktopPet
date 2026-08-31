import XCTest
@testable import DesktopPetCore

// MARK: - Scripted chat double

private struct MockError: Error {}

/// Chat double that replays a scripted response sequence and records every
/// message batch it receives (so tests can assert what the runtime sent back).
private actor ScriptedChat: ChatSending {
    private var responses: [ChatResponse]
    private(set) var receivedBatches: [[ChatMessage]] = []

    init(_ responses: [ChatResponse]) {
        self.responses = responses
    }

    func send(messages: [ChatMessage], tools: [AgentToolDescriptor]) async throws -> ChatResponse {
        receivedBatches.append(messages)
        guard !responses.isEmpty else { throw MockError() }
        return responses.removeFirst()
    }
}

/// Chat double that always returns tool calls (never finishes).
private struct NeverFinishChat: ChatSending {
    func send(messages: [ChatMessage], tools: [AgentToolDescriptor]) async throws -> ChatResponse {
        ChatResponse(
            messages: [ChatMessage(role: .assistant, content: "",
                                   toolCalls: [ChatToolCall(id: "c1", name: "echo", arguments: "{}")],
                                   toolCallID: nil)],
            stopReason: .toolCalls
        )
    }
}

/// Chat double that requests an unregistered tool.
private struct UnknownToolChat: ChatSending {
    func send(messages: [ChatMessage], tools: [AgentToolDescriptor]) async throws -> ChatResponse {
        ChatResponse(
            messages: [ChatMessage(role: .assistant, content: "",
                                   toolCalls: [ChatToolCall(id: "c1", name: "ghost", arguments: "{}")],
                                   toolCallID: nil)],
            stopReason: .toolCalls
        )
    }
}

/// Chat double whose requested tool throws during execution.
private struct FailingToolChat: ChatSending {
    func send(messages: [ChatMessage], tools: [AgentToolDescriptor]) async throws -> ChatResponse {
        ChatResponse(
            messages: [ChatMessage(role: .assistant, content: "",
                                   toolCalls: [ChatToolCall(id: "c1", name: "boom", arguments: "{}")],
                                   toolCallID: nil)],
            stopReason: .toolCalls
        )
    }
}

// MARK: - Tools

private struct EchoTool: AgentTool {
    var descriptor: AgentToolDescriptor {
        AgentToolDescriptor(name: "echo", description: "echoes arguments", parametersJSONSchema: "{}")
    }
    func execute(argumentsJSON: String) async throws -> String {
        "echo:\(argumentsJSON)"
    }
}

private struct BoomTool: AgentTool {
    var descriptor: AgentToolDescriptor {
        AgentToolDescriptor(name: "boom", description: "always fails", parametersJSONSchema: "{}")
    }
    func execute(argumentsJSON: String) async throws -> String {
        throw AgentRuntimeError.toolFailed(name: "boom", message: "kaboom")
    }
}

// MARK: - Tests


// MARK: - Async bridge (Windows XCTest runner is sync-only)

private func awaitResult<T>(timeout: TimeInterval = 10,
                            _ body: @escaping () async throws -> T) throws -> T {
    let sem = DispatchSemaphore(value: 0)
    nonisolated(unsafe) var result: Result<T, Error>?
    Task {
        do { result = .success(try await body()) }
        catch { result = .failure(error) }
        sem.signal()
    }
    _ = sem.wait(timeout: .now() + timeout)
    guard let value = result else {
        throw MockError()
    }
    return try value.get()
}

final class AgentRuntimeTests: XCTestCase {

    private func makeRuntime(chat: any ChatSending, maxIterations: Int = 10, systemPrompt: String = "") -> AgentRuntime {
        let tools: [String: any AgentTool] = ["echo": EchoTool(), "boom": BoomTool()]
        return AgentRuntime(chat: chat, tools: tools, systemPrompt: systemPrompt, maxIterations: maxIterations)
    }

    func test_run_returns_final_message_when_no_tool_calls() throws {
        let chat = ScriptedChat([
            ChatResponse(messages: [ChatMessage(role: .assistant, content: "你好！", toolCalls: nil, toolCallID: nil)],
                         stopReason: .finished)
        ])
        let runtime = makeRuntime(chat: chat)
        let result = try awaitResult { try await runtime.run(userRequest: "做个宠物") }
        XCTAssertEqual(result.finalMessage, "你好！")
        XCTAssertEqual(result.toolCallCount, 0)
    }

    func test_run_executes_tool_and_returns_final_message() throws {
        let chat = ScriptedChat([
            ChatResponse(messages: [ChatMessage(role: .assistant, content: "",
                                               toolCalls: [ChatToolCall(id: "c1", name: "echo", arguments: "{\"v\":1}")],
                                               toolCallID: nil)],
                         stopReason: .toolCalls),
            ChatResponse(messages: [ChatMessage(role: .assistant, content: "完成", toolCalls: nil, toolCallID: nil)],
                         stopReason: .finished)
        ])
        let runtime = makeRuntime(chat: chat)
        let result = try awaitResult { try await runtime.run(userRequest: "加个动作") }
        XCTAssertEqual(result.finalMessage, "完成")
        XCTAssertEqual(result.toolCallCount, 1)

        // The tool result must have been sent back as a .tool message with the
        // matching tool call id, and the assistant tool-call message preserved.
        let batches = try awaitResult { await chat.receivedBatches }
        XCTAssertEqual(batches.count, 2)
        let toolBatch = batches[1]
        XCTAssertTrue(toolBatch.contains { $0.role == .tool && $0.toolCallID == "c1" },
                      "runtime must send back the tool result with matching id")
        XCTAssertTrue(toolBatch.contains { $0.role == .assistant && $0.toolCalls?.first?.id == "c1" },
                      "runtime must keep the assistant tool-call message in history")
    }

    func test_run_preserves_system_and_user_prefix() throws {
        let chat = ScriptedChat([
            ChatResponse(messages: [ChatMessage(role: .assistant, content: "ok", toolCalls: nil, toolCallID: nil)],
                         stopReason: .finished)
        ])
        let runtime = makeRuntime(chat: chat, systemPrompt: "你是宠物助手")
        _ = try awaitResult { try await runtime.run(userRequest: "跳一下") }
        let batches = try awaitResult { await chat.receivedBatches }
        let first = try XCTUnwrap(batches.first)
        XCTAssertEqual(first.first?.role, .system)
        XCTAssertEqual(first.first?.content, "你是宠物助手")
        XCTAssertEqual(first.last?.role, .user)
        XCTAssertEqual(first.last?.content, "跳一下")
    }

    func test_run_throws_when_max_iterations_exceeded() {
        let runtime = makeRuntime(chat: NeverFinishChat(), maxIterations: 3)
        do {
            _ = try awaitResult { try await runtime.run(userRequest: "x") }
            XCTFail("expected maxIterationsExceeded")
        } catch let error as AgentRuntimeError {
            guard case .maxIterationsExceeded = error else {
                return XCTFail("wrong error: \(error)")
            }
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }

    func test_run_throws_when_tool_not_registered() {
        let runtime = makeRuntime(chat: UnknownToolChat())
        do {
            _ = try awaitResult { try await runtime.run(userRequest: "x") }
            XCTFail("expected unknownTool")
        } catch let error as AgentRuntimeError {
            guard case .unknownTool(let name) = error else {
                return XCTFail("wrong error: \(error)")
            }
            XCTAssertEqual(name, "ghost")
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }

    func test_run_surfaces_tool_execution_failure() {
        let runtime = makeRuntime(chat: FailingToolChat())
        do {
            _ = try awaitResult { try await runtime.run(userRequest: "x") }
            XCTFail("expected toolFailed")
        } catch let error as AgentRuntimeError {
            guard case .toolFailed(let name, _) = error else {
                return XCTFail("wrong error: \(error)")
            }
            XCTAssertEqual(name, "boom")
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }
}
