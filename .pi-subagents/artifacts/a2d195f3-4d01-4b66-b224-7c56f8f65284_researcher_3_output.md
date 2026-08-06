# Research: .NET 8 WPF 动作 clip 网格与 hover 动画预览 API 证据清单

## Summary

**状态：Blocked。** 当前子会话未提供网页搜索、HTTP 抓取或浏览器工具，无法在线核验 Microsoft/.NET 官方文档。根据主管指示，不基于模型记忆生成未经核验的 API 事实、URL 或实现结论；未修改任何项目文件，也未执行命令。

## 尚待官方资料核验的主题

父会话需仅在 `learn.microsoft.com`、`dotnet.microsoft.com` 或 Microsoft 官方 API Reference 中核验：

1. **UI 线程定时更新**
   - `System.Windows.Threading.DispatcherTimer`
   - `System.Windows.Media.CompositionTarget.Rendering`
   - 待核验：线程归属、调度/渲染时机、计时精度保证、生命周期与事件退订要求。
   - 特别不能未经文档推断：哪一种一定更省资源、固定帧率准确度、隐藏控件是否自动停止回调。

2. **BitmapSource、Freezable 与跨线程访问**
   - `System.Windows.Media.Imaging.BitmapSource`
   - `System.Windows.Freezable`
   - `Freezable.Freeze`、`CanFreeze`、`IsFrozen`
   - `DispatcherObject.CheckAccess` / `VerifyAccess`
   - 待核验：冻结后可否跨线程共享、冻结的前置条件、冻结对数据绑定和动画的限制。
   - 特别不能未经文档推断：所有 `BitmapSource` 均可冻结，或冻结后任意相关对象图都天然线程安全。

3. **ItemsControl/ListBox 的选择语义与多选**
   - `System.Windows.Controls.ItemsControl`
   - `System.Windows.Controls.ListBox`
   - `SelectionMode`
   - `SelectedItem`、`SelectedItems`、`SelectedValue`
   - 待核验：单选、多选、扩展选择行为，以及 `ItemsControl` 本身是否提供选择模型。
   - 特别不能未经文档推断：`SelectedItems` 可像普通属性一样直接双向绑定，或虚拟化容器状态等同于业务选择状态。

4. **数据绑定通知**
   - `System.ComponentModel.INotifyPropertyChanged`
   - `System.Collections.ObjectModel.ObservableCollection<T>`
   - WPF 数据绑定概述与集合绑定文档
   - 待核验：属性值变化与集合结构变化分别需要何种通知。
   - 特别不能未经文档推断：`ObservableCollection<T>` 会自动传播集合元素内部属性变化。

5. **WrapPanel 与虚拟化**
   - `System.Windows.Controls.WrapPanel`
   - `System.Windows.Controls.VirtualizingPanel`
   - `System.Windows.Controls.VirtualizingStackPanel`
   - ItemsControl/ListBox 的 ItemsPanel 配置及性能文档
   - 待核验：标准 `WrapPanel` 是否提供 UI 虚拟化，以及自定义 `ItemsPanel` 后 ListBox 默认虚拟化是否仍成立。
   - 特别不能未经文档推断：设置 `VirtualizingPanel.IsVirtualizing="True"` 就能使任意 Panel 获得虚拟化能力。

## Findings

1. **blocker — 研究环境缺少网页检索能力**：无法按任务要求读取并验证 Microsoft 官方文档，因此没有可提交的“已核验官方 URL—事实”证据链。
2. **未产生代码级发现**：本任务限定为资料研究且明确禁止修改项目；没有适用的项目文件路径、行号或代码严重性结论。
3. **保守处理正确性**：未用博客、第三方资料或模型记忆补齐证据，避免将未经核验的 WPF 行为作为实现依据。

## Sources

- Kept：无；当前环境无法访问和核验官方网页。
- Dropped：无；未执行搜索，也未使用第三方资料。

## Gaps

以下交付物仍需父会话通过 Exa/浏览器完成：

- 每个主题的 Microsoft 官方 URL。
- 每个 URL 可直接支持的原文事实。
- 官方文档未承诺、因而不能推断的行为边界。
- 面向当前项目的保守实现建议。
- 如需结合项目路径给出风险，还需另行检查相关 View、ViewModel、资源加载和 ItemsPanel 配置；本子任务没有进行代码审查。