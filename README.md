<img src="tub.png" alt="BookmarkVault" width="128" />

# BookmarkVault

[English](README.en.md) | **简体中文**

一个**本地优先**的 Edge 书签整理工具，Windows 桌面应用（WPF / .NET 8）。

自动导入 Edge 书签，然后在这之上做整理、检测和安全评估。**所有数据只存在你自己的机器上**，不登录、不联网同步、不上传任何服务器。

---

## 主要能力

### 导入与管理

- 自动探测 Edge 配置目录并导入书签，按 GUID / URL 去重合并；Edge 正在运行时也能读取
- 在应用里手动添加网址，支持一次粘贴多行，一行一个
- 标记每条书签的来源（Edge 同步 / 手动添加），可按来源筛选
- 编辑标题与备注、删除收藏（只删本地条目，不动 Edge 的书签文件）

### 整理

- **两套分类体系**：项目分类（应用内自建，可新建 / 重命名 / 删除）与浏览器分类（跟随 Edge 文件夹结构，只读）
- 一个网址可以同时归入多个分类，右键勾选或从侧栏拖到分类文件夹上完成归类
- **归类看板**视图：把左侧列表里的书签拖到分类文件夹上
- **隐私模式**：把书签移入隐私侧后，常规模式下完全隐身，两侧的分类互不可见
- 智能归类：按 Edge 文件夹结构导入、或按网站类型自动归类
- 筛选面板：视图 / 封面 / 分类 / 来源 / 可访问状态 / 安全等级 / 站点类型 / 域名后缀，组内多选为「或」、组间为「且」，条件会持久化到下次启动
- 6 档排序：默认 / 最近添加 / 访问最多 / 名称 / 状态 / 安全等级

### 检测与安全

- 并发 HTTP 探测：记录状态码、最终 URL、重定向链路、超时与证书有效性，结果缓存 24 小时，可随时取消
- 站点类型识别：内置 15 类站点的本地规则库
- 安全评估：HTTPS、证书、IP 直连、非标准端口、风险 TLD、Punycode 仿冒、钓鱼敏感词、短链、跨根域跳转
- **恶意网址库**：接入 [URLhaus](https://urlhaus.abuse.ch/) 与 [OpenPhish](https://openphish.com/) 两个公开纯文本订阅源（无需 API Key），命中后在详情页标出来源；也可以自己添加条目（支持 `example.com` 子域匹配、`https://example.com/bad` 路径前缀、`*.example.com` 通配），删除订阅条目会进忽略名单，下次更新不会拉回来

### 界面

- 四种主区视图：Steam 风格封面墙 / 简洁卡片墙 / 概览页 / 归类看板
- 详情页：Hero 封面、三栏信息、可访问状态、安全评估明细、该站账号、相关收藏推荐
- 封面图：手动导入，或自动抓取网页的 `og:image` / `twitter:image`；卡片封面可单独裁成 2:3
- 图标：自动抓取 favicon 并缓存，抓不到时用首字母色块兜底
- 可指定用哪个浏览器打开网址，右键还能临时换一个
- 可自定义快捷键
- Steam 暗色主题，界面语言为简体中文

### 账号库

- 集中管理各站点的账号
- 密码使用 Windows DPAPI 加密（绑定当前 Windows 账号），打开网站时可选弹出提示
- 一键导出为表格：行首勾选要导出的账号（支持全选 / 全不选 / 反选），导出 **CSV 或 Excel（`.xlsx`）**；一条都没勾就导出全部
- 「包含明文密码」是可选开关，默认关闭；开启后会在导出前提示 DPAPI 加密将失效

---

## 环境要求

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft Edge（仅用于导入书签，不是运行的必要条件）

## 构建与运行

```bash
git clone https://github.com/Nero-Nyari/BookmarkVault.git
cd BookmarkVault

# 开发运行
dotnet run

# 或者只构建
dotnet build -c Release
```

生成的程序在 `bin/Release/net8.0-windows/BookmarkVault.exe`。

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+F` | 聚焦搜索框 |
| `Ctrl+R` | 同步 Edge 书签 |
| `F5` | 检测当前筛选结果 |
| `Ctrl+E` | 循环切换主视图 |
| `Esc` | 退一层：先关最上层的浮层，没有浮层就清空搜索，再按取消选中 |

以上都可以在设置页的「快捷键」卡片里改键或清空；隐私模式、筛选面板、添加网址、打开各个页面等操作默认没有绑定按键，可以自己加。改完立即生效并保存。

## 数据存放

所有数据都在 `%APPDATA%\BookmarkVault\`：

| 文件 / 目录 | 内容 |
| --- | --- |
| `library.json` | 书签、分类、账号、设置 |
| `probe-cache.json` | 检测结果缓存（可随时删除重建） |
| `security/blacklist.json` | 恶意网址库规则与忽略名单 |
| `covers/` | 封面图 |
| `icons/` | favicon 缓存 |
| `app.log` | 运行日志 |

全部是纯 JSON，直接删掉对应文件即可重置。仓库里**不含任何用户数据**，`.gitignore` 也挡了一道，防止误提交。

## 隐私说明

- 书签、分类、标签、备注、封面、账号都只保存在本机，不会上传
- 账号密码用 Windows DPAPI 加密，绑定当前 Windows 账号；换电脑或重装系统后需要重新录入
- 唯一会主动联网的行为是：点击「检测」时向被检测的站点发一次 HTTP 请求；更新恶意网址库订阅时下载两个公开的纯文本列表。**被检测的域名不会发送给任何第三方**
- 应用不修改你的 Edge 书签文件，只读取

## 项目结构

```
BookmarkVault/
├── App.xaml(.cs)           应用入口、依赖装配、全局异常处理
├── MainWindow.xaml(.cs)    主窗口、右键菜单、拖拽归类、快捷键派发
├── AssemblyInfo.cs         程序集信息
├── Converters/             值转换器
├── Models/                 数据模型（书签、分类、账号、黑名单、设置）
├── Services/               Edge 读取、HTTP 探测、安全评估、黑名单、封面、图标、账号导出、本地存储
├── Themes/                 Steam 暗色主题资源
├── ViewModels/             MVVM 视图模型
├── Views/                  独立页面（账号库、网址库、设置）
└── 后续功能规划.md          设计说明、已实现清单与待办
```

## 技术栈

- WPF / .NET 8（`net8.0-windows`）
- MVVM，使用 [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) 8.4.2 —— 这是整个项目**唯一**的 NuGet 依赖
- 存储为纯 JSON 文件，原子写入 + 损坏自动备份重建

更多设计细节、实现取舍与后续计划见 [后续功能规划.md](后续功能规划.md)。

## 许可证

[MIT](LICENSE)
