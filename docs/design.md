# 界面设计

Clip 将 Peace 的工作区设计语言用于 WPF，保留系统窗口标题栏和原生控件行为。参考来自 Peace 的 `apps/web/src/styles/globals.css`、`page.css`、共享按钮与 `docs/modules/web/design-system.md`；强调色从 Clip 标志的樱粉、玫瑰色派生。

## 视觉规则

| 用途 | 浅色 | 深色 |
| --- | --- | --- |
| 工作区底色 | `#FFFFFF` | `#0A0A0A` |
| 内容表面 | `#FFFFFF` | `#161616` |
| 次级表面 | `#F9FAFB` | `#111111` |
| 选中背景 | `#EDEDED` | `#303030` |
| 主操作 | `#AC3863` | `#F2A0BD` |
| 强调浅底 | `#FCEEF3` | `#302029` |

- 玫瑰色用于导入、导出、播放、播放头和键盘焦点。选中轨道与下拉选项使用中性灰，选中片段增加中性描边以区分相邻素材。
- 页面边距 24，顶部操作栏高 56；控件圆角 8，面板圆角 12。正文 13，辅助文字 12，标题 22，空态标题 30；时间码使用等宽字体。
- 区域通过对齐、留白、细分隔线区分。导出与更新表单渐进展开，说明通过帮助入口或工具提示查看，异常和进度直接展示。
- 浅色主操作文字对比度约 6:1，深色约 8.8:1。高对比度模式改用系统颜色。

## 实现入口

- [UiTheme.cs](../src/Clip.Desktop/Design/UiTheme.cs)：唯一颜色定义与原生 Fluent 资源映射；启动及系统主题变化时更新，退出时取消系统事件订阅。
- [FluentTokens.xaml](../src/Clip.Desktop/Design/FluentTokens.xaml)：字号、间距、圆角。
- [FluentStyles.xaml](../src/Clip.Desktop/Design/FluentStyles.xaml)：按钮、表单、选项、菜单、工具提示与弹窗公共样式。
- [TimelineControl.cs](../src/Clip.Desktop/TimelineControl.cs)：自绘轨道、片段和播放头；沿用同一组颜色资源，主题变化后重绘。
- [NoticeWindow.xaml](../src/Clip.Desktop/NoticeWindow.xaml)：错误、诊断、导出结果和关闭确认；详情可复制、可滚动，关闭确认默认聚焦“继续剪辑”。

所有颜色引用使用动态资源，已打开窗口随系统明暗主题更新。系统文件选择器与 WPF 初始化失败时的原生错误提示由 Windows 提供。

## 验证

Windows 启动验证在既有编辑、拖拽、播放、导出验证之外，检查运行时主题切换、下拉选中态、主按钮对比度、帮助弹层 Escape、编辑工具栏忙碌态、缩放按钮和关闭确认取消行为。更新窗口使用内存 HTTP 响应验证“尚无正式版本”，不访问真实更新服务。

每种主题分别生成 `smoke-theme-{light|dark}-{editor|export|name|notice|update}.png`，上传到 Windows CI 的 UI smoke 产物。这些图片必须由实际 WPF 窗口生成；Linux 交叉编译不能代替窗口验收。

Windows 手动检查还应覆盖：

- 系统明暗主题切换时，保持导出和更新窗口打开，检查背景、表单、菜单及时间轴同时更新。
- 在 100% / 150% 缩放和最小窗口尺寸下，检查预览、工具栏、长片段名、长诊断文本和弹窗底部按钮。
- 用 Tab、Enter、Escape、方向键操作表单和菜单；确认帮助关闭后仍留在导出窗口。
- 用系统高对比度主题检查选中片段、轨道、键盘焦点和禁用状态。
