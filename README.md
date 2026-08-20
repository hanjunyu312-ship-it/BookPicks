# BookPicks（韩俊宇的书库 · 全球书榜）

一个帮助你在「不知道看什么书」时做决定的书榜应用，支持 **Windows** 与 **Android**。

数据来自开放、免费的 [Open Library](https://openlibrary.org) 全球趋势接口，无账号、无广告、不收集用户信息。

## 功能

- **今日热榜** —— 全球热门图书 Top 50（Open Library 全球趋势，每日更新）。
- **日榜 / 周榜 / 月榜** —— 按 daily / weekly / monthly 切换热门周期。
- **分类浏览** —— 文学 / 人文 / 基础科学 / 生命健康 / 地球环境 / 科技前沿 / 商学 / 艺术 / 生活，共 9 大类 65 个主题。
- **搜索** —— 按书名、作者关键词搜索。
- **随机选书** —— 纠结时让「帮我选书」替你翻牌。
- **收藏** —— 看中的书一键收藏，保存在本机。
- **中文书名翻译** —— 卡片与详情自动把英文书名翻译成中文，并保留英文原名。
- **中文简介翻译** —— 详情页简介自动翻译为中文，可切换查看原文。
- **中文主题标签翻译** —— 主题标签翻译为中文，悬停可看英文原文。
- **本地缓存** —— 浏览过的榜单与书库自动缓存，断网仍可浏览。

## Windows

- **系统要求**：Windows 10 / 11（自带 WebView2 运行时）。
- **安装**：双击 `BookPicks.exe` 即可，无需安装。
- 首次运行若出现 SmartScreen 提示，点「更多信息」→「仍要运行」。
- 若旧版本正在运行无法覆盖，先关闭旧程序再替换 exe。

### Windows 离线翻译引擎（可选）

书名、简介、标签默认通过在线接口翻译；也可下载免费开源翻译模型
（[Helsinki-NLP/opus-mt-en-zh](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh)，约 300MB）
在本地离线翻译，断网也能用、不消耗流量、速度更稳。

- 在应用底部点「在线翻译 · 点击下载免费离线模型」自动安装。
- 或手动运行：`powershell -ExecutionPolicy Bypass -File tools\install_translator.ps1`
- 安装完成后重启应用，底部显示「本地离线翻译引擎已就绪」。
- 引擎不可用时自动回退在线翻译，无需任何操作。

## Android

- **系统要求**：Android 8.0 及以上。
- **安装**：把 `BookPicks.apk` 传到手机，点击安装；提示「未知来源」时允许本次安装即可。
- 界面与桌面版一致；收藏保存在手机本地。
- 网络请求直连 Open Library，需联网使用。
- 书名、简介与主题标签在线翻译成中文，翻译结果会缓存。

## 数据与网络说明

- 数据源：**Open Library**（openlibrary.org，免费开放数据）。
- 榜单为「全球热门趋势」，而非亚马逊 / 纽约时报销售榜。
- 翻译接口依赖网络，可能受网络状况影响。
- **国内网络环境访问 Open Library 可能需要代理或 VPN**：
  - Windows 版会自动识别系统代理（Clash / v2rayN 等），无需手动配置；
  - Android 版需要手机能访问国际网络（手机代理 / VPN），或让手机连接电脑共享的代理。
- **隐私**：收藏与缓存全部保存在本机，不收集用户账号，不上传用户收藏到任何作者服务器。

## 构建

技术栈：.NET 9 / WinForms / WebView2（Windows）、Java + WebView + 本地静态资源服务器（Android，无 Gradle）。

### Windows

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# 产物输出到 publish\BookPicks.exe（自包含单文件）
# 自检：publish\BookPicks.exe --selftest
```

要求：.NET 9 SDK。

### Android（未签名测试包）

```powershell
powershell -ExecutionPolicy Bypass -File android\build-android.ps1
# 产物输出到 android\BookPicks-unsigned.apk
```

要求：JDK 17、Android SDK（build-tools 36.1.0、platform android-36）。

### Android（正式签名包）

```powershell
$env:BOOKPICKS_KEYSTORE = "安全存放的 keystore 路径"
$env:BOOKPICKS_KEY_ALIAS = "bookpicks"
$env:BOOKPICKS_STORE_PASSWORD = "从安全密码管理器读取"
$env:BOOKPICKS_KEY_PASSWORD = "从安全密码管理器读取"
powershell -ExecutionPolicy Bypass -File android\build-android.ps1 -Signed
# 产物输出到 android\BookPicks.apk
```

> ⚠️ **Android 正式升级必须使用同一个签名证书**。请务必妥善保管 keystore 与密码——
> 丢失签名密钥将导致已安装用户无法覆盖升级。切勿把 keystore、密码提交到 Git 或上传到任何公共位置。

构建命令已在「Windows 11 + .NET 9.0.304 + JDK 17 + build-tools 36.1.0 / android-36」环境验证通过。

## 文档

- [更新日志](CHANGELOG.md)
- [安全说明](SECURITY.md)
- [贡献指南](CONTRIBUTING.md)
- [发布流程](RELEASE.md)
- [使用说明](使用说明.txt)

## 许可证

待确认。
