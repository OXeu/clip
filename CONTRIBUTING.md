# 参与贡献

欢迎通过反馈问题、改进文档、调整界面或提交代码参与 Clip。交流可使用中文或英文，请遵守[行为准则](CODE_OF_CONDUCT.md)。

## 反馈问题与提出建议

先阅读[使用指南](docs/usage.md)并搜索[已有 Issue](https://github.com/OXeu/clip/issues)。发现新问题时，使用[问题反馈模板](https://github.com/OXeu/clip/issues/new?template=bug_report.yml)，提供复现步骤、预期与实际结果、应用版本或构建链接，以及 Windows 和显卡信息。

功能建议请使用[功能建议模板](https://github.com/OXeu/clip/issues/new?template=feature_request.yml)，说明具体剪辑场景和希望解决的问题。较大的功能或架构调整建议先讨论范围；小修复和文档改进可以直接提交 PR。

日志、截图和测试素材请移除令牌、个人路径及敏感内容。尽量用可公开分享的最小样例复现问题。安全问题或行为准则投诉请私下发送至 [thankrain@qq.com](mailto:thankrain@qq.com)，不要在公开 Issue 中提交敏感详情。

## 开发环境

Clip 使用 C#、.NET 10 和 WPF。安装 [global.json](global.json) 指定的 SDK；Windows 脚本使用 PowerShell 7。Linux 可运行核心测试和交叉编译，WPF 界面运行与交互检查需要 Windows。

Fork 仓库后，从 `master` 创建工作分支。环境配置、FFmpeg 下载和启动步骤见[开发指南](docs/development.md)，界面约定见[设计文档](docs/design.md)。

在仓库根目录运行与改动相关的检查：

```bash
dotnet build src/Clip.Desktop -c Release
dotnet run --project tests/Clip.Tests -c Release
dotnet run --project tests/Clip.UpdateTests -c Release
```

修改视频处理、剪辑或导出行为时，还需准备包含 `libx264` 的 FFmpeg 和 FFprobe，运行集成测试：

```bash
dotnet run --project tests/Clip.Tests -c Release -- --integration
```

界面改动请在 Windows 中检查明暗主题、100% / 150% 缩放及相关键盘操作，并附实际窗口截图。Linux 编译成功不代表已经验证 WPF 界面。仅修改文档或模板时，检查格式、链接和模板字段即可。

## 提交 Pull Request

- 一个 PR 聚焦一个问题，避免混入无关格式化或依赖升级。
- 沿用现有代码和界面约定；颜色、字号与间距优先复用 `src/Clip.Desktop/Design` 中的资源。
- 修改编辑、导出或更新逻辑时，增加能复现问题或验证行为的测试，保留源文件保护、取消与回滚行为。
- 在 PR 中说明问题、修改后的行为、关联 Issue，以及实际运行的检查和结果；未运行的检查请注明原因。
- 行为、配置或使用方式变化时同步更新文档。默认分支的 README 截图由 Windows CI 更新，不需要手工替换。

PR 的目标分支为 `master`。Windows CI 会执行集成测试、界面检查、更新验证与打包检查；请根据失败日志修正相关问题。

## 许可与第三方内容

提交代码贡献时，请确认你有权提供这些内容，并同意贡献按项目的 [MIT License](LICENSE) 分发。引入第三方代码、图标或其他素材时，保留原始许可和署名，并更新 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。第三方组件和行为准则继续适用各自声明的许可证。
