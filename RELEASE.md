# 发布流程（Release Guide）

> 目标读者：项目维护者。本流程用于发布新的 Windows / Android 正式版本。

## 发布前置检查

1. **更新版本号**：
   - Windows：`BookPicks.csproj` 的 `<Version>`（如 `1.1.0`）。
   - Android：`android/AndroidManifest.xml` 的 `versionCode`（递增整数）与 `versionName`（与 Windows 主次版本一致）。
   - 升级覆盖安装依赖 `versionCode` 递增；`versionName` 保持可读语义。
2. **更新 CHANGELOG**：在 `CHANGELOG.md` 顶部记录新版本的变更。
3. **执行敏感信息扫描**：确认暂存区/Release 内容不含 keystore、密码、Token、本机路径、`上传资料/` 内容：

   ```powershell
   git grep -n -i -E "storepass|keypass|ks-pass|pass:"
   git grep -n -i -E "password|secret|token|apikey"
   git status --ignored
   ```

## 构建

4. **构建 Windows**：

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

   产物：`publish\BookPicks.exe`（自包含单文件，含 `www/` 与 `tools/`）。

5. **构建 Android 测试版本**（未签名）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File android\build-android.ps1
   ```

   产物：`android\BookPicks-unsigned.apk`。

6. **使用原正式 keystore 构建正式 Android APK**：

   ```powershell
   $env:BOOKPICKS_KEYSTORE = "安全存放的 keystore 路径"
   $env:BOOKPICKS_KEY_ALIAS = "bookpicks"
   $env:BOOKPICKS_STORE_PASSWORD = "从安全密码管理器读取"
   $env:BOOKPICKS_KEY_PASSWORD = "从安全密码管理器读取"
   powershell -ExecutionPolicy Bypass -File android\build-android.ps1 -Signed
   ```

   产物：`android\BookPicks.apk`。

   > ⚠️ **必须使用与上一版本相同的签名证书**，否则已安装用户无法覆盖升级。

## 校验

7. **计算 SHA-256**：

   ```powershell
   Get-FileHash publish\BookPicks.exe -Algorithm SHA256
   Get-FileHash android\BookPicks.apk -Algorithm SHA256
   ```

   将结果写入 `SHA256SUMS.txt`。

8. **安装测试**：
   - Windows：在干净的 Windows 10 / 11 上运行 `BookPicks.exe`。
   - Android：在 Android 8.0+ 真机上安装 `BookPicks.apk`。

9. **测试覆盖升级**：
   - 从上一个正式版本升级到新版本（Windows 覆盖 exe、Android 直接覆盖安装 APK），确认数据（收藏 / 缓存）保留且功能正常。
   - Android 覆盖升级失败通常意味着签名证书不一致。

## 发布

10. **创建 Git tag**：

    ```powershell
    git tag v<版本号>            # 如 v1.1.0
    git push origin v<版本号>
    ```

11. **创建 GitHub Release**（建议先创建 **Draft**，确认后再正式发布）。
12. **上传发布资产**：
    - Windows：`publish\BookPicks.exe`（或打包为 ZIP）。
    - Android：`android\BookPicks.apk`。
    - `SHA256SUMS.txt`。
    - 可选：`使用说明.txt`。

## 禁止上传

- keystore / `.jks` / `.p12` 等签名文件。
- `.idsig` 文件。
- 临时文件、缓存、本地翻译模型。
- 任何含密码 / Token / 本机路径的文件。

## 自动化

仓库内的 GitHub Actions 提供辅助：

- `ci.yml`：每次推送 / PR 时执行 Windows 构建、JS 语法检查与 Android 未签名构建（不读取正式 keystore）。
- `release.yml`：推送 `v*` tag 或手动触发时，从 GitHub Secrets 读取 keystore 并构建正式签名 APK，生成 `SHA256SUMS.txt`，创建 **Draft Release**。

在 GitHub Actions 中配置以下 Secrets 即可使用 `release.yml`：

| Secret | 说明 |
| --- | --- |
| `BOOKPICKS_KEYSTORE_BASE64` | 正式 keystore 的 Base64 编码内容 |
| `BOOKPICKS_KEY_ALIAS` | keystore alias |
| `BOOKPICKS_STORE_PASSWORD` | keystore 密码 |
| `BOOKPICKS_KEY_PASSWORD` | key 密码 |

> keystore 内容仅临时解码于 CI 运行器，构建结束后删除；日志中不会出现密码。
> 建议在发布前用 CI 构建产物做一次真机安装与覆盖升级测试。
