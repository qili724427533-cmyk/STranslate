## 更新

- 添加：自定义服务图标，可为每个服务单独设置图标，留空时回退到插件默认图标
- 添加：图片翻译语种检测独立配置，精简窗口与独立页提供语种检测展开器，不再受主界面语种检测设置影响
- 添加：增量翻译新增「清空输入」开关，控制增量翻译时是否清空输入框
- 添加：OCR 覆盖层支持双击选中文本所在整行
- 添加：新增 Google OCR、腾讯 OCR（默认高精度）、有道 OCR、OCR.Space 四款 OCR 插件
- 优化：前台窗口激活改用两阶段策略并恢复强制前台激活，提升窗口置顶可靠性
- 优化：服务复制策略优化，复制服务时保留原有配置
- 优化：服务切换器默认位置调整
- 优化：OCR 服务图标菜单项分组为子菜单并增加分隔符
- 优化：图片翻译译文覆盖改用矢量覆盖层即时绘制，替代超采样位图光栅化，显著降低内存峰值并优化行高
- 优化：通知栏视觉调整为 Win11 InfoBar 风格，以可释放的 NoticeBar 替换 InfoBar
- 优化：微信 OCR 更新至 1.0.5
- 修复：打开设置时保留当前选中的服务，不再丢失选择
- 修复：图片翻译覆盖层文本渲染可靠性
- 修复：图片翻译精简窗口加载显示
- 修复：图片翻译服务右键菜单项按 `SupportBoxPoints` 正确启用
- 修复：OCR 窗口、欢迎向导、设置窗口、Prompt 编辑窗口及图片翻译（精简窗口、独立窗口）多处内存泄漏与生命周期释放问题

## 插件开发

- `STranslate.Plugin` 更新至 `1.0.13`
- `Service` 新增 `IconPath` 属性，支持服务自定义图标路径（为空时回退 `MetaData.IconPath`）

## 其他

- [插件市场](https://stranslate.zggsong.com/plugins.html)
- [使用说明](https://stranslate.zggsong.com/docs/)
- [集成调用](https://stranslate.zggsong.com/docs/invoke.html)
- [安装卸载](https://stranslate.zggsong.com/docs/(un)install.html)
- [FAQ](https://stranslate.zggsong.com/docs/faq.html)

**完整更新日志:** [v2.0.8...v2.0.9](https://github.com/STranslate/STranslate/compare/v2.0.8...v2.0.9)
