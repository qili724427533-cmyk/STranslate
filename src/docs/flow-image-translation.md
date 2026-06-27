# 图片翻译链路

## 模块职责
- 管理图片翻译独立窗口/精简窗口的导入、截图、重试、标注图和结果图显示。
- 维护图片翻译专用 OCR 服务与翻译服务绑定，避免普通 OCR 服务误入需要坐标的流程。
- 对 OCR 结果执行结构化投影、分段逻辑、翻译分发、文字覆盖回写和图片级文本选中。
- 约束 OCR 插件坐标框支持声明、结构化分段返回方式和本地 `Smart` 分段回退策略。

## 关键入口
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
  - `ExecuteAsync(Bitmap)`：图片翻译窗口主执行命令。
  - `ApplyLayoutAnalysis(OcrResult)`：按分段模式生成 `OcrLayoutBlock`。
  - `GenerateTranslatedImage(IReadOnlyList<OcrLayoutBlock>, BitmapSource?)`：擦除原文并覆盖译文。
  - `RefreshSelectableOcrWords()`：在原文标注图和译文结果图之间切换图片文本选中数据源。
- `STranslate/Views/ImageTranslateWindow.xaml` / `ImageTranslateCompactWindow.xaml`
  - `Standalone`：原独立窗口，保留服务、语言、文本框和完整工具栏。
  - `Compact`：无标题精简窗口，图片区贴回截图选区，底部预留悬浮核心按钮区。
- `STranslate/Core/Screenshot.cs`
  - `GetScreenshotCaptureAsync()`：调用 `ScreenGrabber.CaptureWithRegionAsync`，截图时直接回传选区物理坐标，无需事后反推。
  - 精简窗口模式下传 `padImage: false`，关闭 ScreenGrab 对 <64px 小截图的背景画布 padding 扩展，保证 `bitmap.Size == 选区物理尺寸`，避免贴回时 Viewbox 把 padding 图缩放导致原始内容缩小；其他窗口模式保留默认 padding 行为。
- `STranslate/Core/OcrLayoutAnalyzer.cs`
  - `AnalyzeBlocks(OcrResult, LayoutAnalysisMode)`：分段逻辑入口。
  - `Auto` / `Provider` / `Smart` / `NoMerge`：图片翻译分段策略。
- `STranslate/Core/OcrLayoutBlock.cs`
  - 图片翻译内部分段块，记录段落框、行框、分段来源和置信度。
- `STranslate/Core/ImageTranslateTextOverlayLayout.cs`
  - 计算覆盖层矩形、擦除矩形、字体大小、多行回退、裁剪策略和主题色。
- `STranslate/Services/OcrService.cs`
  - `GetImageTranslateOcrServices()` / `GetImageTranslateOcrServiceOrDefault()`：图片翻译 OCR 服务筛选和兜底。
- `STranslate/Services/TranslateService.cs`
  - `ImageTranslateService`：图片翻译专用翻译服务。
- `STranslate.Plugin/IOcrPlugin.cs`
  - `IOcrPlugin.SupportBoxPoints()`、`OcrRequest`、`OcrResult`、`BoxPoint`。

## 从入口到结果
1. 入口来自主窗口图片翻译命令、图片翻译窗口导入文件、剪贴板图片、重新执行或窗口内截图。
2. 主窗口图片翻译入口会先关闭已打开的图片翻译独立窗口或精简窗口，再截图，避免把旧结果截入新图片。
3. `IScreenshot.GetScreenshotCaptureAsync()` 调用 `ScreenGrabber.CaptureWithRegionAsync`，截图时即回传选区物理坐标；精简窗口优先按该坐标贴回选区，仅异常情况下回退到光标附近定位。
4. `Settings.ImageTranslateWindowMode` 决定使用 `ImageTranslateWindow` 还是 `ImageTranslateCompactWindow` 承载同一个 `ImageTranslateWindowViewModel`。
5. `ImageTranslateWindowViewModel.ExecuteAsync(bitmap)` 清空旧状态，缓存 `_sourceImage` 并显示原图。
6. 获取图片翻译专用 OCR：`OcrService.GetImageTranslateOcrSvcOrDefault()`。候选服务必须实现 `IOcrPlugin`，并让 `SupportBoxPoints()` 返回 `true`。
7. 宿主用真实图片尺寸构造 `OcrRequest(data, Settings.OcrLanguage, bitmap.Width, bitmap.Height)`，插件必须返回图片像素坐标 `BoxPoints`。
8. OCR 返回后调用 `Utilities.PrepareOcrResult()`；如果插件只填充结构化 `Regions`，宿主会投影出兼容的 `OcrContents`。
9. 原始 OCR 结果用于图片文本选中：`OcrWordBuilder.CreateFromOcrContents(_lastOcrResult.OcrContents)` 生成原文选中块。
10. `ApplyLayoutAnalysis()` 生成 `OcrLayoutBlock`，并把分析后的块投影回 `OcrResult.OcrContents`，供标注图、复制和结果文本复用。
11. 获取 `TranslateService.ImageTranslateService`，该服务必须是 `ITranslatePlugin`，词典类服务不会进入图片翻译翻译列表。
12. 对每个 `OcrLayoutBlock.Text` 并发执行语言检测和翻译；翻译成功后用 `ImageTranslateTextOverlayLayout.NormalizeOverlayText()` 收敛空白，再回写到对应 block。
13. 使用翻译后的分段块生成结果图：优先按每个 block 的 `LineBoxPoints` 擦除原文，再按覆盖策略绘制译文。
14. `Settings.IsImTranShowingAnnotated` 控制显示标注图还是结果图；图片文本选中同步切换为原文块或译文块。

## 窗口模式
- `Standalone` 是默认模式，保留当前可缩放、可调整大小的独立窗口。
- `Compact` 使用无标题、不可缩放、非任务栏、**完全透明**窗口，窗口本身无背景色；屏幕上只看到截图内容 + 悬浮按钮条（按钮条自带半透明胶囊背景）。
- 精简窗口的图片始终钉在截图选区的物理屏幕位置（贴图位置不变铁律）；按钮条作为悬浮额外内容，根据空间自动选择位置：
  - 横向：按钮条窄于选区时居中；宽于选区时贴左缘向右延展，右边放不下则镜像向左延展。
  - 纵向：默认在图片下方；下方放不下翻上方；上下都放不下则叠加在图片底部之上（按钮条 ZIndex 高于图片）。
- 布局算法在 `ImageTranslateCompactWindowPlacement.CreateLayout`，返回窗口矩形、图片偏移、按钮条位置与 `ToolbarSide`（`Below`/`Above`/`Overlay`），由 `ImageTranslateCompactWindow.ApplyLayoutToVisualTree` 换算成 DIP 应用到 `ImageZoom` 与按钮条 `Border`。
- 精简窗口不支持图片拖拽、滚轮缩放或双击复位，底部只保留关闭、复制/全选、标注切换、重新截图、重新执行和设置等核心按钮。
- 精简窗口按 `Esc`、点击窗口外部或再次触发图片翻译关闭；右键菜单和窗口内部文字选择不会触发外部关闭。
- 精简窗口不显示右侧文本框，`Settings.IsImTranShowingTextControl` 只影响独立窗口。

## 分段模式
- `Auto`：默认模式。OCR 返回结构化 `Regions` 时使用 Provider 段落；没有结构化分段时回退 `Smart`。
- `Provider`：只使用服务商结构化 `Regions -> Paragraphs -> Lines`；缺失结构化分段时退化为 `NoMerge`，不自行猜段落。
- `Smart`：本地智能分段。适用于只返回扁平 `OcrContents` 但有坐标框的 OCR。
- `NoMerge`：保留 OCR 原始块，适合用户希望逐块翻译或服务商块已经足够稳定的场景。
- 无有效坐标：跳过智能分段，保留 OCR 返回文本；图片翻译无法可靠生成覆盖框和图片文本选中框。

## Smart 分段策略
`Smart` 只在宿主内部生效，不改变插件接口和外部枚举。

1. `BuildLineSegments()` 先按 Y 位置恢复视觉行，并按横向间距拆成行内 segment。
2. 表格/网格上下文会提前参与视觉行拆分：多行重复列起点、表格式行距和足够列跨度同时满足时，列边界优先于普通行内碎片合并，避免 `File Explorer Add-ons File Locksmith` 这类同一行跨单元格误合并。
3. 小图标、符号、图标色块等前置装饰不会单独拆开，仍和后续文本组成同一个功能项。
4. `BuildLayoutRegions()` 按列/区域相似度聚合，避免不同栏之间链式吞并。
5. `AnalyzeRegion()` 在 region 内合并 paragraph；普通段落、PDF 连续行、多列正文和英文断词续行继续使用原段落合并规则。
6. 表格/网格 region 会被识别为 `TableLike`：至少多行、多列、多个视觉行有横向 peers，并且列左边缘或中心点在多行重复对齐。
7. `TableLike` region 内禁止跨视觉行继续追加为同一 paragraph，所以功能列表、表格单元格默认按每个视觉行/单元项独立翻译。

## 结构化 OCR 与插件契约
图片翻译对 OCR 插件的要求比普通 OCR 更高：

- 必须 override `IOcrPlugin.SupportBoxPoints()` 并返回 `true`。
- 必须为文本块返回图片像素坐标 `BoxPoints`。
- 可以只返回扁平 `OcrResult.OcrContents`，宿主会用 `Smart` 分段。
- 如果服务商能返回区域/段落/行，插件应填充 `OcrResult.Regions`。
- 内置百度 OCR 使用 `paragraph=true` 获取 `paragraphs_result.words_result_idx`，再把对应 `words_result` 行组装成 `OcrRegion -> OcrParagraph -> OcrContent`。
- `OcrResult.OcrContents` 仍是兼容旧插件和旧调用链的扁平结果；结构化插件可以同时填充它，也可以只填充 `Regions`。
- 如服务商返回归一化坐标，插件需要使用 `OcrRequest.PixelWidth` / `PixelHeight` 换算成图片像素坐标后再写入 `BoxPoints`。
- 插件不要按屏幕缩放或窗口缩放改写坐标；图片翻译使用图片自身的像素坐标。

`Auto` 模式下，结构化 OCR 的 Provider 段落优先级高于本地 `Smart`，所以插件返回的 `Regions` 会直接影响分段粒度。插件侧应尽量让 `Paragraphs` 表示真实语义段落或表格单元项，而不是把整列/整表合成一个 paragraph。

## 译文覆盖策略
- 覆盖层不直接使用截图背景采样颜色，而是跟随软件主题：
  - 浅色主题：浅色覆盖层 + 黑字。
  - 深色主题：深色覆盖层 + 白字。
- 擦除区域优先来自 `OcrLayoutBlock.LineBoxPoints`，尽量只擦除原文行；缺少行框时退回 block 外接框。
- 段落框与行框范围不一致时，译文选区取两者并集，避免只按段落框的局部高度缩小字号。
- 字体大小按完整译文选区动态拟合；原文单行块先按单行渲染，最小字号仍放不下时才扩展为多行区域，扩展高度最多约为原行高的 `3.2` 倍。
- 所有多行译文统一使用 `1.24 × 字号` 作为基础行高，字号测量与最终绘制使用相同规则，不区分译文语言。
- 多行译文达到至少 3 行且排版高度不足原选区 90% 时，会按实际行数自适应增加行高，最大不超过 `2.0 × 字号`；仍有余量时整体垂直居中。
- 如果最小字号仍放不下，保留裁剪/截断保护，避免文本绘制溢出到相邻区域。
- 覆盖文本会先归一化空白，减少翻译服务返回的多余换行和空格破坏布局。

## 图片文本选中
- `ImageZoom` 使用 `OcrWords` 模拟图片上的文本选中。
- 标注图显示时，`OcrWords` 来自原始 OCR 坐标和原文文本。
- 结果图显示时，`OcrWords` 来自翻译后的 `OcrLayoutBlock`，复制时拿到的是译文。
- 缺少坐标框时显示无位置信息提示，图片上无法可靠模拟选区。

## 服务绑定与配置
- OCR 专用绑定：`ServiceSettings.ImageTranslateOcrSvcID`，由图片翻译窗口的 OCR 选择写入。
- 翻译专用绑定：`ServiceSettings.ImageTranslateSvcID`，由图片翻译窗口的翻译服务选择写入。
- 服务缺失或插件被删除时，启动加载会重置失效的图片翻译服务 ID。
- `Settings.ImageTranslateWindowMode` 控制图片翻译结果使用独立窗口或精简窗口，默认 `Standalone`。
- `Settings.LayoutAnalysisMode` 是分段模式配置，默认 `Auto`，序列化支持 `auto`、`provider`、`smart`、`noMerge`；旧未知值归一为 `Auto`。
- `Settings.IsImTranShowingAnnotated` 控制标注图/结果图显示。
- `Settings.IsImTranShowingTextControl` 控制图片翻译窗口文本区域显示。
- `Settings.ImageTranslateSourceLang` / `ImageTranslateTargetLang` 控制图片翻译语言。
- `Settings.ShowImageTranslateItemInNotifyIconMenu` 控制托盘菜单是否显示图片翻译入口。

## 错误处理
- 图片翻译 OCR 服务未配置：`Helper.PromptConfigureService()` 弹出配置提示并定位到 `OcrPage`。
- 图片翻译翻译服务未配置：窗口内 `_snackbar.ShowWarning("NoTranslateService")`。
- OCR 失败、翻译异常或运行时异常：窗口内 Snackbar 提示，日志写入 `ImageTranslateWindowViewModel` logger。
- 语言检测失败：当前 block 跳过翻译并提示 `LanguageDetectionFailed`。
- 用户取消执行：捕获 `TaskCanceledException`，当前实现不额外弹提示。

## 关键文件
- `STranslate/ViewModels/ImageTranslateWindowViewModel.cs`
- `STranslate/Views/ImageTranslateCompactWindow.xaml`
- `STranslate/Core/Screenshot.cs`
- `STranslate/Core/OcrLayoutAnalyzer.cs`
- `STranslate/Core/OcrLayoutBlock.cs`
- `STranslate/Core/ImageTranslateTextOverlayLayout.cs`
- `STranslate/Core/LayoutAnalysisModeJsonConverter.cs`
- `STranslate/Services/OcrService.cs`
- `STranslate/Services/TranslateService.cs`
- `STranslate.Plugin/IOcrPlugin.cs`
- `Tests/STranslate.Tests/OcrLayoutAnalyzerTests.cs`
- `Tests/STranslate.Tests/ImageTranslateTextOverlayLayoutTests.cs`

## 常见改动任务
- 调整图片翻译分段逻辑或表格/网格误合并：优先改 `OcrLayoutAnalyzer`，并补 `OcrLayoutAnalyzerTests`。
- 调整译文覆盖大小、裁剪、擦除范围或主题颜色：改 `ImageTranslateTextOverlayLayout`，并补 `ImageTranslateTextOverlayLayoutTests`。
- 接入服务商结构化 OCR：插件填充 `OcrResult.Regions`，并确保每个 `OcrContent` 有图片像素坐标 `BoxPoints`。
- 调整图片翻译 OCR 候选服务：改 `OcrService.IsImageTranslateOcrService()` / `GetImageTranslateOcrServices()`。
- 调整图片翻译翻译服务候选：改 `ImageTranslateWindowViewModel.OnTransFilter()` 或 `TranslateService.ImageTranslateService` 相关逻辑。
- 调整图片上选中文本行为：改 `RefreshSelectableOcrWords()`、`OcrWordBuilder` 或 `ImageZoom` 的选区逻辑。
- 调整精简窗口定位或关闭行为：改 `ImageTranslateCompactWindow` 的 `PlaceForCapture` / `PlaceOnPhysicalWindowBounds`，选区物理坐标由 `Screenshot.GetScreenshotCaptureAsync` 经 ScreenGrab `CaptureWithRegionAsync` 直接回传。
- 调整精简窗口布局/定位/按钮条翻向逻辑：改 `ImageTranslateCompactWindowPlacement.CreateLayout`，并补 `ImageTranslateCompactWindowPlacementTests` 对应场景；按钮条尺寸/间距常量在 `ImageTranslateCompactWindow`（`ToolbarWidth`/`GapH`/`GapV`/`WindowMargin`）。
