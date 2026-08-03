import Foundation

// MARK: - 服务配置（base URL / API key 由用户在界面中自行填写，不写死）

struct ImageGenConfig: Equatable {
    var baseURL: String
    var apiKey: String
}

enum ImageGenConfigStore {
    private static let baseURLKey = "imageGen.baseURL"
    private static let apiKeyKey = "imageGen.apiKey"

    static func load() -> ImageGenConfig {
        ImageGenConfig(
            baseURL: UserDefaults.standard.string(forKey: baseURLKey) ?? "",
            apiKey: UserDefaults.standard.string(forKey: apiKeyKey) ?? ""
        )
    }

    static func save(_ config: ImageGenConfig) {
        UserDefaults.standard.set(config.baseURL, forKey: baseURLKey)
        UserDefaults.standard.set(config.apiKey, forKey: apiKeyKey)
    }
}

// MARK: - 请求 / 响应

/// 尺寸选项：OpenAI 精确尺寸（2.0/2.1 都支持）+ 2.1 档位式（size=2K&ratio=…）。
/// 与具体模型解耦，模型名完全由 /v1/models 动态提供。
struct ImageGenSizeOption: Identifiable, Equatable {
    let label: String
    let size: String
    let ratio: String?
    var id: String { label }

    static let all: [ImageGenSizeOption] = [
        ImageGenSizeOption(label: "1024 × 1024", size: "1024x1024", ratio: nil),
        ImageGenSizeOption(label: "1024 × 768", size: "1024x768", ratio: nil),
        ImageGenSizeOption(label: "768 × 1024", size: "768x1024", ratio: nil),
        ImageGenSizeOption(label: "2K · 1:1  → 2048 × 2048", size: "2K", ratio: "1:1"),
        ImageGenSizeOption(label: "2K · 16:9 → 2624 × 1472", size: "2K", ratio: "16:9"),
        ImageGenSizeOption(label: "2K · 3:4  → 1728 × 2304", size: "2K", ratio: "3:4"),
        ImageGenSizeOption(label: "2K · 9:16 → 1472 × 2624", size: "2K", ratio: "9:16"),
        ImageGenSizeOption(label: "2K · 21:9 → 3136 × 1344", size: "2K", ratio: "21:9"),
    ]
}

struct ImageGenRequest {
    var model: String
    var prompt: String
    var size: String
    var ratio: String?
    /// 图生图/多图合成参考图：公网 URL 或 data URI（data:image/png;base64,...）
    var imageRefs: [String] = []
}

struct ImageGenError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// 调用 OpenAI 兼容的 /v1/images/generations 并返回图片二进制数据。
/// 接口兼容 OpenAI Images API，但有两个非标准约定：`response_format` 必须放
/// 在 `extra_body` 内；图生图时 `image` 数组也放在 `extra_body.image`。
struct ImageGenClient {
    let baseURL: String
    let apiKey: String

    /// GET {base}/v1/models —— 动态拉取模型列表，不硬编码任何模型名。
    func listModels() async throws -> [String] {
        let url = try endpointURL(path: "models")
        var urlRequest = URLRequest(url: url)
        urlRequest.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        urlRequest.timeoutInterval = 60

        let (data, response) = try await URLSession.shared.data(for: urlRequest)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? -1
            throw ImageGenError(message: Self.errorMessage(from: data, statusCode: code))
        }
        struct ModelsResponse: Decodable {
            struct Model: Decodable { let id: String }
            let data: [Model]
        }
        let decoded = try JSONDecoder().decode(ModelsResponse.self, from: data)
        guard !decoded.data.isEmpty else {
            throw ImageGenError(message: "GET /v1/models 未返回数据，该网关可能不支持模型列表，请改用自定义模型。")
        }
        return decoded.data.map(\.id)
    }

    func generate(_ request: ImageGenRequest) async throws -> Data {
        let url = try endpointURL(path: "images/generations")
        guard !apiKey.isEmpty else {
            throw ImageGenError(message: "请先填写 API Key")
        }

        var body: [String: Any] = [
            "model": request.model,
            "prompt": request.prompt,
            "size": request.size,
        ]
        if let ratio = request.ratio {
            body["ratio"] = ratio
        }
        var extraBody: [String: Any] = ["response_format": "url"]
        if !request.imageRefs.isEmpty {
            extraBody["image"] = request.imageRefs
        }
        body["extra_body"] = extraBody

        var urlRequest = URLRequest(url: url)
        urlRequest.httpMethod = "POST"
        urlRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        urlRequest.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        urlRequest.httpBody = try JSONSerialization.data(withJSONObject: body)
        // 文档建议 60s–360s 超时。
        urlRequest.timeoutInterval = 300

        let session = URLSession.shared
        let (data, response) = try await session.data(for: urlRequest)
        guard let http = response as? HTTPURLResponse else {
            throw ImageGenError(message: "无 HTTP 响应")
        }
        guard (200..<300).contains(http.statusCode) else {
            throw ImageGenError(message: Self.errorMessage(from: data, statusCode: http.statusCode))
        }

        let decoded = try JSONDecoder().decode(ImagesResponse.self, from: data)
        guard let item = decoded.data.first else {
            throw ImageGenError(message: "响应中 data 为空")
        }
        if let urlString = item.url, let url = URL(string: urlString) {
            let (imageData, _) = try await session.data(from: url)
            return imageData
        }
        if let b64 = item.b64_json {
            guard let imageData = Data(base64Encoded: b64) else {
                throw ImageGenError(message: "b64_json 解码失败")
            }
            return imageData
        }
        throw ImageGenError(message: "响应中既没有 url 也没有 b64_json")
    }

    private struct ImagesResponse: Decodable {
        struct Item: Decodable {
            let url: String?
            let b64_json: String?
        }
        let data: [Item]
    }

    /// 兼容用户填 `https://host` 或 `https://host/v1` 两种写法。
    private func endpointURL(path: String) throws -> URL {
        var base = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty else {
            throw ImageGenError(message: "请先填写 Base URL")
        }
        while base.hasSuffix("/") { base.removeLast() }
        let root = base.hasSuffix("/v1") ? base : base + "/v1"
        guard let url = URL(string: "\(root)/\(path)") else {
            throw ImageGenError(message: "Base URL 无效：\(baseURL)")
        }
        return url
    }

    private static func errorMessage(from data: Data, statusCode: Int) -> String {
        struct ErrorBody: Decodable {
            struct E: Decodable { let message: String? }
            let error: E?
        }
        if let body = try? JSONDecoder().decode(ErrorBody.self, from: data),
           let message = body.error?.message, !message.isEmpty {
            return "HTTP \(statusCode)：\(message)"
        }
        let snippet = String(data: data, encoding: .utf8) ?? ""
        return "HTTP \(statusCode)\(snippet.isEmpty ? "" : "：\(snippet.prefix(300))")"
    }
}
