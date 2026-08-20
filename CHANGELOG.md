# 更新日志

本项目的所有重要变更都会记录在此文件中。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-08-20

首个正式版本。

### 新增

- **今日热榜**：Open Library 全球热门图书 Top 50，每日更新。
- **日榜 / 周榜 / 月榜**：切换 daily / weekly / monthly 热门周期。
- **分类浏览**：9 大类 65 个主题的书库。
- **搜索**：按书名、作者搜索。
- **随机选书**：一键随机推荐一本书。
- **收藏**：本机保存，支持增删。
- **中文翻译**：书名、简介、主题标签在线翻译为中文，并保留英文原文可切换查看。
- **本地缓存**：榜单 / 书库 / 翻译结果缓存到本机，断网可浏览。
- **Windows 离线翻译引擎**：可选安装免费开源翻译模型，离线翻译书名与简介。
- **平台支持**：Windows（.NET 9 / WinForms / WebView2）与 Android（Java + WebView，Android 8.0+）。

### 修复

- 修复自检中「本地翻译引擎状态」误报失败的问题：状态字符串 `notinstalled` 与校验值 `not_installed` 不一致，已对齐。

### 安全

- 重写 Android 构建脚本：移除硬编码 keystore 路径 / alias / 密码，默认仅生成未签名测试包，正式签名须显式启用并从环境变量读取凭据，禁止自动生成 keystore、禁止默认密码、密码不出现在命令行与日志。
- Android 构建输入暂存到纯 ASCII 临时目录，支持中文项目路径。

[1.0.0]: https://github.com/hanjunyu312-ship-it/BookPicks/releases/tag/v1.0.0
