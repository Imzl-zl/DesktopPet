## Review
- Correct: 四份 catalog 独立校验均为 730 个键、24 个含占位符的值；键集完全一致，所有占位符集合一致。
- Correct: `WpfLocalizer` 已支持静态、格式化、动态排除及本地化参数，见 [WpfLocalizer.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Localization/WpfLocalizer.cs:34)。
- Correct: 自定义宠物名、人格描述、Provider URL、日记日期/正文等路径使用动态排除或原始格式化参数，未发现会因值恰好等于 catalog key 而被翻译。
- Correct: Welcome 人格 combo、SpritePreview header、Danmaku 窗口及 Settings 直接拥有的 modal windows 已接入刷新路径，见 [App.xaml.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/App.xaml.cs:568) 和 [SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:249)。
- Correct: 内置人格卡片与 Welcome combo 使用 `i18n.T(...)`，见 [SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2283) 和 [WelcomeWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Windows/WelcomeWindow.cs:168)。
- Correct: [LanguageCoordinator.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Localization/LanguageCoordinator.cs:47) 仍先保存，成功后才设置语言并发布。
- Blocker: 无。
- Important: [SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:1017) 和 [SettingsWindow.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Settings/SettingsWindow.cs:2628) 直接向 `ComboBox.Items` 添加字符串；[WpfLocalizer.cs](C:/sudy/github/DesktopPet/windows-native/src/DesktopPet.App/Localization/WpfLocalizer.cs:126) 只遍历逻辑树中的 `DependencyObject`，不会翻译这些 item 字符串。结果是宠物卡片的人格选择器中的“跟随全局”和所有内置人格名，以及模型连接编辑器中的“关闭思考/跟随模型默认”，在英文等语言下仍显示中文；语言切换后也不会刷新。对应翻译键已存在于 catalog，因此这是当前仍未处理的用户可见本地化缺口。
- Note: 未运行原生 Windows GUI smoke；自动化测试不能覆盖真实 ComboBox item、Win2D/XAML Island、托盘及跨窗口刷新行为。

已运行并通过：
- 四 catalog 键集/占位符独立 Node 校验
- `dotnet test ...DesktopPet.Core.Tests.csproj ... --filter FullyQualifiedName~I18nTests`，13/13
- `dotnet test ...DesktopPet.App.Tests.csproj ... --filter "FullyQualifiedName~WpfLocalizerTests|FullyQualifiedName~LanguageCoordinatorTests"`，7/7
- `git diff --check`，通过