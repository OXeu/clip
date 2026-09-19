# Clip · Fluent 2 界面规范

使用微软 .NET 自带的 `PresentationFramework.Fluent` 和浅色主题。应用操作图标使用 [Remix Icon](https://remixicon.com/)，直接以原始 SVG 路径转成 WPF Geometry，无网页容器或图标字体依赖。原生控件的弹出层、焦点、禁用态和滚动条保留微软 Fluent 模板。

## 四原则

| 原则 | 实现 |
| --- | --- |
| 对比 | 中性浅色工作区衬托视频画面；主操作为品牌蓝；选中片段以蓝底、描边和“已选中”文案共同区分；标题、正文、辅助信息分级 |
| 对齐 | 页面统一 24 DIP 外边距，卡片统一 20 DIP 内边距；标签、输入和解释左对齐；播放控制居中，时间读数两端对齐 |
| 重复 | 所有面板、按钮、文字和时间轴共用语义令牌；按钮高度 36 DIP，圆角 4 / 8 DIP，应用图标为 20 DIP Remix 矢量 |
| 亲和性 | 素材信息紧邻素材；片段详情只在选择后出现；分割、删除、撤销集中在时间轴；导出参数与其解释放在同一组 |

## 渐进式披露

| 状态 / 操作 | 显示内容 |
| --- | --- |
| 未导入 | 单一主入口“导入视频”及拖放提示，隐藏素材栏、播放控制、导出和时间轴 |
| 已导入 | 显示素材、预览、播放控制、时间轴和导出 |
| 选中片段 | 显示片段时长和删除操作；精确入点、出点仍可按需展开 |
| 导出 | 默认显示质量和尺寸；编码器、硬件解码收在“高级设置” |
| 自定义尺寸 | 仅选择自定义后显示宽高输入；验证失败在字段下方显示错误 |
| 设置 | 日常入口保持一个按钮；FFmpeg 目录及 NVIDIA 诊断放在高级子菜单 |

必需操作不藏进高级区域：分割、删除、导出始终在相应任务上下文直接可达。原画重编码的说明留在基本导出设置内。

## 令牌与代码

- `Design/FluentTokens.xaml`：中性、品牌、错误及预览语义色，字体、字号、4 DIP 间距刻度和圆角。
- `Design/FluentStyles.xaml`：继承原生 Fluent 按钮模板的主、次、轻量操作，以及统一面板和文案样式。
- `Design/RemixGeometry.xaml`、`RemixIcon.cs`：图标几何与原生矢量渲染；颜色继承按钮状态。
- `TimelineControl.cs`：画布也消费相同语义色和字体，避免出现独立的一套样式。

主要颜色：背景 #F5F5F5 / #FFFFFF，正文 #242424，辅助文案 #616161，品牌 #0F6CBD，品牌浅底 #EBF3FC，错误 #B10E1C。正文 14、辅助文案 12、区域标题 16、页面标题 24–28 DIP。普通文本组合满足至少 4.5:1 的对比度；选中状态另有文字提示，非仅靠颜色。

## 验证

Windows CI 用真实 FFmpeg 测试视频验证导入、分割、选择、删除和撤销，并保存空态、导入后、选中后、最小窗口、基本导出及高级导出的截图。断言高级选项默认收起、自定义宽高按需显示、错误以内联方式出现。截图捕获客户端区域并使用当前 DPI，避免窗口装饰或黑边影响审查。

设计依据：[Fluent 2 布局](https://fluent2.microsoft.design/layout)、[字体](https://fluent2.microsoft.design/typography)、[颜色](https://fluent2.microsoft.design/color)、[按需展开](https://fluent2.microsoft.design/components/web/react/core/accordion/usage)、[WPF Fluent 主题](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)。
