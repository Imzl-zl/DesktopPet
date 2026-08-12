import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// AI 生图面板：调用 OpenAI 兼容的 /v1/images/generations 接口。
/// 模型列表从 GET /v1/models 动态拉取（不硬编码模型名），拉取失败时可手动输入；
/// base URL 与 API Key 由用户自行填写。
struct ImageGenView: View {
    @State private var baseURL = ""
    @State private var apiKey = ""
    @State private var prompt = ""
    @State private var models: [String] = []
    @State private var selectedModel = customModelTag
    @State private var customModel = ""
    @State private var isModelsLoading = false
    @State private var modelLoadError: String?
    @State private var sizeOption = ImageGenSizeOption.all[0]
    @State private var customSize = ""
    @State private var imageRefs: [String] = []
    @State private var isGenerating = false
    @State private var errorText: String?
    @State private var generatedImage: NSImage?
    @State private var imageURLInput = ""
    @State private var hoveredRef: String?

    static let customModelTag = "__custom__"

    private var canGenerate: Bool {
        !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !apiKey.isEmpty
            && !currentModel.isEmpty
            && !isGenerating
    }

    private var currentModel: String {
        selectedModel == Self.customModelTag
            ? customModel.trimmingCharacters(in: .whitespacesAndNewlines)
            : selectedModel
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: Theme.space4) {
                    configSection
                    promptSection
                    modelSection
                    sizeSection
                    refsSection
                    errorSection
                }
                .padding(Theme.space4)
            }
            Divider()
            footer
        }
        .frame(width: 560)
        .frame(minHeight: 680, maxHeight: 900)
        .background(Theme.bgTop)
        .preferredColorScheme(.dark)
        .noFocusRing()
        .onAppear(perform: loadConfig)
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: Theme.space3) {
            ZStack {
                RoundedRectangle(cornerRadius: Theme.radiusSm, style: .continuous)
                    .fill(Theme.accent)
                Image(systemName: "sparkles")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: 28, height: 28)
            VStack(alignment: .leading, spacing: 1) {
                Text("Generate Image")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(Theme.textPrimary)
                Text("OpenAI 兼容生图 · 模型列表从 /v1/models 动态获取")
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.textMuted)
            }
            Spacer()
        }
        .padding(Theme.space4)
    }

    // MARK: 服务配置

    private var configSection: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            EyebrowLabel("服务配置")
            TextField("Base URL，例如 https://apihub.agnes-ai.com", text: $baseURL)
                .textFieldStyle(.plain)
                .padding(.horizontal, Theme.space3)
                .padding(.vertical, 8)
                .background(Theme.card)
                .cornerRadius(Theme.radiusMd)
                .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                .onChange(of: baseURL) { _ in saveConfig() }
            SecureField("API Key", text: $apiKey)
                .textFieldStyle(.plain)
                .padding(.horizontal, Theme.space3)
                .padding(.vertical, 8)
                .background(Theme.card)
                .cornerRadius(Theme.radiusMd)
                .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                .onChange(of: apiKey) { _ in saveConfig() }
        }
    }

    // MARK: Prompt

    private var promptSection: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            EyebrowLabel("提示词")
            GrowingTextEditor(text: $prompt)
                .frame(minHeight: 72, maxHeight: 140)
                .padding(.horizontal, 2)
                .padding(.vertical, 4)
                .background(Theme.card)
                .cornerRadius(Theme.radiusMd)
                .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
            HStack(spacing: Theme.space2) {
                Text("试试：").font(.system(size: 11)).foregroundStyle(Theme.textMuted)
                sampleChip("pixel cat", "a cute pixel-art cat, transparent background, game sprite style")
                sampleChip("floating city", "a luminous floating city above a misty canyon at sunrise, cinematic realism, wide shot")
                sampleChip("cyberpunk street", "cyberpunk street at night, neon signs, wet pavement reflections, high detail")
            }
        }
    }

    private func sampleChip(_ label: String, _ text: String) -> some View {
        Button(label) { prompt = text }
            .buttonStyle(PillButtonStyle())
    }

    // MARK: 模型（动态列表）

    private var modelSection: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            HStack {
                EyebrowLabel("模型")
                Spacer()
                if isModelsLoading {
                    ProgressView().controlSize(.mini)
                }
                Button {
                    Task { await loadModels() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                        .font(.system(size: 11, weight: .medium))
                }
                .buttonStyle(PillButtonStyle())
                .disabled(isModelsLoading)
            }
            Picker("", selection: $selectedModel) {
                Text("自定义模型…").tag(Self.customModelTag)
                ForEach(models, id: \.self) { m in
                    Text(m).tag(m)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(maxWidth: 300)

            if selectedModel == Self.customModelTag {
                TextField("模型名，例如 agnes-image-2.1-flash", text: $customModel)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, Theme.space3)
                    .padding(.vertical, 8)
                    .background(Theme.card)
                    .cornerRadius(Theme.radiusMd)
                    .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                    .onChange(of: customModel) { _ in saveConfig() }
            }
            if let modelLoadError {
                Text(modelLoadError)
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.warning)
            }
        }
    }

    // MARK: 尺寸（与模型解耦）

    private var sizeSection: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            EyebrowLabel("输出尺寸")
            Picker("", selection: $sizeOption) {
                ForEach(ImageGenSizeOption.all) { opt in
                    Text(opt.label).tag(opt)
                }
                Text("自定义…").tag(ImageGenSizeOption(label: "__custom__", size: "__custom__", ratio: nil))
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(maxWidth: 340)
            .onChange(of: sizeOption) { _ in saveConfig() }
            if sizeOption.size == "__custom__" {
                TextField("精确尺寸，例如 1024x768", text: $customSize)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, Theme.space3)
                    .padding(.vertical, 8)
                    .background(Theme.card)
                    .cornerRadius(Theme.radiusMd)
                    .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                    .onChange(of: customSize) { _ in saveConfig() }
            }
        }
    }

    // MARK: 参考图片（图生图 / 多图合成）

    private var refsSection: some View {
        VStack(alignment: .leading, spacing: Theme.space2) {
            HStack {
                EyebrowLabel("参考图片（可选）")
                Spacer()
                if !imageRefs.isEmpty {
                    Button("清空") { imageRefs.removeAll() }
                        .buttonStyle(.plain)
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.textMuted)
                }
            }
            HStack(spacing: Theme.space2) {
                TextField("图片 URL（公网可访问）", text: $imageURLInput)
                    .textFieldStyle(.plain)
                    .padding(.horizontal, Theme.space3)
                    .padding(.vertical, 7)
                    .background(Theme.card)
                    .cornerRadius(Theme.radiusMd)
                    .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                Button("添加") { addURLRef() }
                    .buttonStyle(PillButtonStyle())
                    .disabled(imageURLInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                Button("选择文件…") { addFileRef() }
                    .buttonStyle(PillButtonStyle())
            }
            if !imageRefs.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: Theme.space2) {
                        ForEach(imageRefs, id: \.self) { ref in
                            refChip(ref)
                        }
                    }
                }
            }
        }
    }

    private func refChip(_ ref: String) -> some View {
        let label = ref.hasPrefix("data:") ? "本地图片" : ref
        return HStack(spacing: 6) {
            Image(systemName: "photo")
                .font(.system(size: 10))
            Text(label)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: 140)
            Button {
                imageRefs.removeAll { $0 == ref }
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 10))
            }
            .buttonStyle(.plain)
        }
        .font(.system(size: 11))
        .foregroundStyle(Theme.textSecondary)
        .padding(.horizontal, Theme.space2)
        .padding(.vertical, 6)
        .background(Theme.card.opacity(hoveredRef == ref ? 1.0 : 0.6))
        .cornerRadius(Theme.radiusMd)
        .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
        .onHover { hoveredRef = $0 ? ref : nil }
    }

    private func addURLRef() {
        var url = imageURLInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !url.isEmpty, !imageRefs.contains(url) else { return }
        if !url.lowercased().hasPrefix("http") { url = "https://" + url }
        imageRefs.append(url)
        imageURLInput = ""
    }

    private func addFileRef() {
        let panel = NSOpenPanel()
        panel.title = "选择参考图片"
        panel.prompt = "添加"
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = true
        let webpType = UTType(filenameExtension: "webp") ?? .data
        panel.allowedContentTypes = [.png, .jpeg, webpType, .gif, .tiff, .bmp]
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            guard let data = try? Data(contentsOf: url) else { continue }
            let mime = Self.mimeType(for: url)
            let uri = "data:\(mime);base64,\(data.base64EncodedString())"
            if !imageRefs.contains(uri) {
                imageRefs.append(uri)
            }
        }
    }

    private static func mimeType(for url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "webp": return "image/webp"
        case "gif": return "image/gif"
        case "tiff", "tif": return "image/tiff"
        case "bmp": return "image/bmp"
        default: return "image/png"
        }
    }

    // MARK: 错误与结果

    @ViewBuilder
    private var errorSection: some View {
        if let errorText {
            HStack(alignment: .top, spacing: Theme.space2) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Theme.warning)
                Text(errorText)
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.textSecondary)
                    .textSelection(.enabled)
                Spacer(minLength: 0)
                Button {
                    self.errorText = nil
                } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 10))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Theme.textMuted)
            }
            .padding(Theme.space3)
            .background(Theme.warningSoft)
            .cornerRadius(Theme.radiusMd)
        }

        if isGenerating {
            HStack(spacing: Theme.space3) {
                ProgressView()
                    .controlSize(.small)
                Text("正在生成，通常需要数秒到数十秒…")
                    .font(.system(size: 12))
                    .foregroundStyle(Theme.textMuted)
            }
            .padding(Theme.space3)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.card)
            .cornerRadius(Theme.radiusMd)
        }

        if let generatedImage {
            VStack(spacing: Theme.space2) {
                Image(nsImage: generatedImage)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .frame(maxHeight: 380)
                    .background(Theme.bgElevated)
                    .cornerRadius(Theme.radiusMd)
                    .overlay(RoundedRectangle(cornerRadius: Theme.radiusMd).strokeBorder(Theme.cardStroke, lineWidth: 1))
                HStack(spacing: Theme.space3) {
                    Text("\(Int(generatedImage.size.width)) × \(Int(generatedImage.size.height))")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(Theme.textMuted)
                    Spacer()
                    Button("保存…") { saveImage() }
                        .buttonStyle(PillButtonStyle())
                }
            }
        }
    }

    // MARK: Footer

    private var footer: some View {
        HStack {
            Text("OpenAI 兼容 · POST /v1/images/generations · 模型来自 /v1/models")
                .font(.system(size: 10))
                .foregroundStyle(Theme.textMuted)
            Spacer()
            Button("生成") { generate() }
                .buttonStyle(AccentButtonStyle())
                .disabled(!canGenerate)
                .keyboardShortcut(.defaultAction)
        }
        .padding(Theme.space4)
    }

    // MARK: 逻辑

    private func loadConfig() {
        let config = ImageGenConfigStore.load()
        baseURL = config.baseURL
        apiKey = config.apiKey
        customModel = UserDefaults.standard.string(forKey: "imageGen.customModel") ?? ""
        selectedModel = UserDefaults.standard.string(forKey: "imageGen.model") ?? Self.customModelTag
        let savedSize = UserDefaults.standard.string(forKey: "imageGen.size")
        if let savedSize, let match = ImageGenSizeOption.all.first(where: { $0.label == savedSize }) {
            sizeOption = match
        }
        if let savedCustomSize = UserDefaults.standard.string(forKey: "imageGen.customSize") {
            customSize = savedCustomSize
        }
        // 已有配置时自动拉取模型列表。
        if !baseURL.isEmpty && !apiKey.isEmpty {
            Task { await loadModels() }
        }
    }

    private func saveConfig() {
        ImageGenConfigStore.save(ImageGenConfig(baseURL: baseURL, apiKey: apiKey))
        UserDefaults.standard.set(selectedModel, forKey: "imageGen.model")
        UserDefaults.standard.set(customModel, forKey: "imageGen.customModel")
        UserDefaults.standard.set(sizeOption.label, forKey: "imageGen.size")
        UserDefaults.standard.set(customSize, forKey: "imageGen.customSize")
    }

    /// 从 GET /v1/models 拉取模型列表；失败时保留自定义输入兜底。
    private func loadModels() async {
        let baseUrl = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        let apiKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !baseUrl.isEmpty, !apiKey.isEmpty else {
            modelLoadError = "先填写 Base URL 和 API Key，再刷新模型列表。"
            return
        }
        isModelsLoading = true
        modelLoadError = nil
        defer { isModelsLoading = false }
        do {
            let all = try await ImageGenClient(baseURL: baseUrl, apiKey: apiKey).listModels()
            // 不过滤：中转站模型命名各异（agnes-image-*、diffusiongemma、dall-e…），
            // 关键词过滤必然漏；全部列出让用户自选，选错由接口报错兜底。
            models = all
            if !models.contains(selectedModel) {
                selectedModel = models.first ?? Self.customModelTag
            }
        } catch {
            modelLoadError = "模型列表加载失败：\(error.localizedDescription)"
        }
    }

    private func generate() {
        errorText = nil
        generatedImage = nil
        isGenerating = true
        saveConfig()

        let size: String
        let ratio: String?
        if sizeOption.size == "__custom__" {
            size = customSize.trimmingCharacters(in: .whitespacesAndNewlines)
            ratio = nil
        } else {
            size = sizeOption.size
            ratio = sizeOption.ratio
        }

        let request = ImageGenRequest(
            model: currentModel,
            prompt: prompt.trimmingCharacters(in: .whitespacesAndNewlines),
            size: size,
            ratio: ratio,
            imageRefs: imageRefs
        )
        let client = ImageGenClient(baseURL: baseURL, apiKey: apiKey)

        Task {
            do {
                let data = try await client.generate(request)
                guard let image = NSImage(data: data) else {
                    errorText = "图片数据无法解析"
                    isGenerating = false
                    return
                }
                generatedImage = image
            } catch {
                errorText = error.localizedDescription
            }
            isGenerating = false
        }
    }

    private func saveImage() {
        guard let generatedImage,
              let tiff = generatedImage.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else { return }
        let panel = NSSavePanel()
        panel.title = "保存图片"
        panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = "desktoppet-\(Int(Date().timeIntervalSince1970)).png"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? png.write(to: url)
    }
}
