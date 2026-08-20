# 贡献指南

欢迎为 BookPicks 贡献代码、文档或改进建议。请先阅读本指南与 [README](README.md)。

## 开发环境

### Windows

- .NET 9 SDK（`dotnet --version` 应为 9.x）。
- 本机已安装 WebView2 运行时（Windows 10 / 11 自带）。
- 构建：`powershell -ExecutionPolicy Bypass -File build.ps1`，产物在 `publish\`。

### Android

- JDK 17。
- Android SDK：`ANDROID_HOME` 指向 SDK 目录，需包含：
  - `build-tools\36.1.0`
  - `platforms\android-36`
- 未签名构建：`powershell -ExecutionPolicy Bypass -File android\build-android.ps1`。

### 测试

- JavaScript 语法检查（需 Node.js）：

  ```powershell
  node --check www/app.js
  node --check android/assets/www/app.js
  ```

- Windows 构建：`dotnet restore` + `dotnet build -c Release`。
- Windows 自检：`publish\BookPicks.exe --selftest`（部分检查依赖网络，需能访问 Open Library）。
- Android 未签名构建：见上文。
- 涉及翻译 / 榜单数据的验证依赖网络，若国内网络无法直连 Open Library，请配置代理后测试。

## 提交注意事项

- **不要提交构建产物**：APK、EXE、ZIP、`.idsig`、keystore、`bin/`、`obj/`、`build/`、`publish/` 等（见 `.gitignore`）。
- **不要提交签名材料与凭据**：任何 `.keystore` / `.jks` / `.p12`、密码、Token、API Key、本地配置文件。
- **不要覆盖两套前端**：`www/`（Windows）与 `android/assets/www/`（Android）是两份内容不同的前端，
  分别依赖本地 `/api/*` 代理与直连 Open Library，**禁止互相覆盖**。
- **修改 Android 前端请在 PR 说明中写清原因**，并说明修改的是哪一套。

## Pull Request 要求

- 每个 PR 只做一件事，便于评审与回滚。
- 说明变更内容、动机与测试结果。
- 运行相关测试后再提交 PR。
- 涉及 Android 构建 / 签名 / 前端逻辑的变更，请附构建或验证结果。

## 提交信息格式

使用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/v1.0.0/) 风格：

```text
feat: 添加日榜/周榜/月榜切换
fix: 修复自检误报
docs: 补充 README 构建说明
ci: 添加 GitHub Actions 工作流
build: 重构 Android 构建脚本
```

如涉及破坏性变更，请在提交信息中说明。

## 版本号

- Windows 版本见 `BookPicks.csproj` 的 `<Version>`。
- Android 版本见 `android/AndroidManifest.xml` 的 `versionCode` / `versionName`。
- 正式发版前请同步 README / CHANGELOG / Release 说明中的版本号，不要擅自升级版本号，除非确有必要并记录原因。
