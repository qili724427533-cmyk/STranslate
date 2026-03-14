## 更新

- 添加：剪贴板监听翻译（新增全局热键 `Alt + Shift + A`、主界面按钮、显示开关）
- 添加：划词翻译支持 `Ctrl + C + C` 触发模式（可与传统热键切换）
- 添加：主界面标题栏按钮布局可配置（拖拽显示/隐藏、拖拽排序、全部显示/全部隐藏）
- 添加：历史记录支持导出、批量删除与全选/取消，感谢 @Rockytkg #636
- 添加：插件市场（浏览/下载/升级/安装状态/项目主页）并整合到设置流程
- 添加：插件市场下载可取消，插件升级后支持“待重启/立即重启”提示
- 添加：插件市场高级配置（CDN 源切换、下载代理、自定义 URL）
- 添加：切换提示词后自动翻译（立即翻译）选项
- 添加：软件内一键便携模式/漫游模式切换，支持迁移用户数据目录并自动重启应用
- 添加：图片翻译支持独立 OCR 服务配置（设置页可拖拽/右键指定，图片翻译窗口可直接切换）
- 添加：替换翻译与插入翻译支持可选剪贴板
- 添加：主窗口跟随鼠标选项
- 添加：划词翻译取词失败时执行输入翻译
- 添加：自动更新检查
- 添加：日语、韩语本地化支持（主程序与插件）
- 优化：朗读按钮改为播放/停止切换，支持 TTS 一键启停，感谢 @Rockytkg #636
- 优化：回译结果支持快速取消朗读；插入文本支持按住 `Shift` 小写插入
- 优化：插件市场界面与交互（布局、图标按钮、点击区域、提示、流畅度）
- 优化：设置页打开速度、历史记录多选体验、主界面/设置页历史导航按钮显示
- 优化：图片翻译 OCR 引擎优先使用专用服务，并在未配置时回退到全局 OCR 启用项
- 优化：`DebounceExecutor` 防抖机制由 `CancellationToken + Task.Delay` 重构为 generation 判定，减少频繁取消导致的调试异常噪音
- 优化：外部调用匹配忽略大小写
- 修复：插件市场下载多进度显示异常
- 修复：多处事件重复订阅与状态滞留导致的内存泄漏问题
- 修复：启动模式修改失效问题
- 修复：静默执行后光标未恢复问题
- 插件开发：`IPluginContext` 新增 `ApplyTheme`，支持插件主题同步，感谢 @SwiftFloatFlow #644

## 其他

- [插件市场](https://stranslate.zggsong.com/plugins.html)
- [使用说明](https://stranslate.zggsong.com/docs/)
- [集成调用](https://stranslate.zggsong.com/docs/invoke.html)
- [安装卸载](https://stranslate.zggsong.com/docs/(un)install.html)
- [FAQ](https://stranslate.zggsong.com/docs/faq.html)

**完整更新日志:** [v2.0.5...v2.0.6](https://github.com/STranslate/STranslate/compare/v2.0.5...v2.0.6)
