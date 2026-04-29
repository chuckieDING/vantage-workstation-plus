# Histology Workstation Plus

针对 **Roche / Ventana navify Advantage Histology System** 的批量操作工具。原系统只支持单件操作；本工具通过逆向其 Web aspx 和 SOAP TouchScreenService API，把 4 个高频流程做成"上传文件 → 批量执行"模式，单次几百件物品几分钟搞定。

## 功能

| 模块 | 功能 | 后端协议 |
|---|---|---|
| 批量出片 | 玻片清单按位置 + 病理医生批量签出 | Web aspx (`Folders.aspx`) |
| 批量脱水 | 包埋盒入篮 → 装载脱水机 → 启动 → 结束（单缸单篮）| SOAP `TouchScreenService.asmx` |
| 批量归档 | 物品按 Case 分组批量归档到指定位置 | Web aspx (`SpecimenArchive.aspx`) |
| 批量流转 | 物品（玻片/蜡块/样本）批量流转到指定位置 | Web aspx (`SpecimenTracking.aspx`) |

## 技术架构

- **WPF / .NET 8** Windows 桌面应用，单文件可执行（约 8.4 MB，依赖 .NET 8 Desktop Runtime）
- **双协议鉴权**：
  - Web 三个模块 → `Login.aspx` POST 拿 JWT cookie `Ventana.Vantage.ApiAuth`
  - 脱水（SOAP）→ 工作站机器密钥 AES-256-GCM 加密的 AuthHeader（每次 call 一个新 nonce）
- **工作站注册**：要求事先用原版 `clientsetup.exe` 在服务端创建 TissueProcessing 工作站，然后在 `appsettings.json` 填入分配到的 `WorkCellId`
- **现代 Fluent 风格 UI**：自定义 ModernTheme.xaml（无第三方 UI 框架依赖），WrapPanel chip 列表、卡片化布局
- **状态轮询**：脱水模块 10s 自动刷新缸/篮/蜡块状态，检测到已超时但服务端未自动结束的缸，自动提示一键收尾
- **文件支持**：`.xls / .xlsx / .xlsm / .csv / .txt / .tsv`（通过 ExcelDataReader 同时兼容 BIFF 和 OpenXML）

## 运行要求

- Windows 10 1809+（amd64）
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（如未装，运行时会提示下载）
- 同网段能访问 Vantage Histology System 服务器
- 本机已用原版 `clientsetup.exe` 注册过 TissueProcessing 工作站（仅脱水模块需要）

## 部署

1. 拷 `publish-fx/VantageWorkstationPlus.exe` 和 `publish-fx/appsettings.json` 到任意目录
2. 编辑 `appsettings.json`：
   ```json
   {
     "WorkCellId": <服务端分配的 WorkCellID>,
     "EnabledTabs": ["signoff", "dehydration", "archive", "transfer"]
   }
   ```
   - `WorkCellId` 不填或填 0 时启动会列出服务器现有 TP 工作站让你选 ID 填回
   - `EnabledTabs` 控制启用哪些功能 Tab，缺省 / 空数组 = 全部启用
3. 双击 `VantageWorkstationPlus.exe` 启动；首次会被 Windows SmartScreen 拦（未代码签名），点"更多信息 → 仍要运行"
4. 输入服务器地址（如 `http://win-i9enn0hk2m8`）+ 用户名 + 密码登录

## 配置项

`appsettings.json` 字段：

| 字段 | 默认 | 说明 |
|---|---|---|
| `WorkCellId` | `0` | 服务端 TP 工作站实例 ID，必填 |
| `EmpUserId` | `1` | SOAP 调用的 empUserID 参数；登录后会被覆盖为实际用户 ID |
| `WorkCellType` | `"TissueProcessing"` | SOAP 登录用，固定 |
| `ClientVersion` | `"4.1.25136.1"` | 模仿 WPF 客户端版本号，发给服务端 |
| `EnabledTabs` | 全部 | 字符串数组，可选 `signoff` / `dehydration` / `archive` / `transfer` |
| `AcceptAnyServerCert` | `true` | 是否接受任意 TLS 证书（自签内网常用），生产环境建议 `false` |

## 工作站注册环境变量

SOAP 鉴权用本机环境变量持久化（与原版 WPF 客户端共用）：
- `{MAC}_NPLA_ENCRYPTION_KEY` — 32B AES key (Base64)
- `{MAC}_NPLA_PASSWORD` — 32B password (Base64)
- `{MAC}_NPLA_WORKCELL_ID` — 服务端分配的工作站 ID
- `{MAC}_NPLA_MACH_ID` — 注册时生成的 machid (GUID 前 25 字符)

`{MAC}` 是本机首张活跃网卡的物理地址，大写无分隔符（如 `00155D78EC38`）。

## 项目结构

```
vantage_workstation_wpf/
├── App.xaml.cs              入口 + 全局异常 hook + 配置加载
├── MainWindow.xaml          主窗口（侧栏导航 + 内容区）
├── Dialogs/LoginDialog.xaml 登录页（全屏 + 卡片）
├── Panels/                  4 个批量功能面板
│   ├── BatchPanelBase.cs    Log + 文件选择公共工具
│   ├── SignOffPanel.xaml    批量出片
│   ├── DehydrationPanel.xaml 批量脱水（最复杂，含 10s 轮询 + 状态树）
│   ├── ArchivePanel.xaml    批量归档
│   └── TransferPanel.xaml   批量流转
├── Services/
│   ├── AppSession.cs        Web 会话（JWT cookie）
│   ├── TouchScreenSession.cs SOAP 客户端（11 个方法）
│   ├── SoapAuth.cs          AES-GCM AuthHeader 生成 + 工作站注册
│   ├── FoldersApi.cs        /Workflow/Folders.aspx 封装（出片）
│   ├── ArchiveApi.cs        /Workflow/SpecimenArchive.aspx 封装
│   ├── TrackingApi.cs       /Workflow/SpecimenTracking.aspx 封装
│   ├── SlideListReader.cs   xls/xlsx/csv/txt 文件读取
│   └── AppLog.cs            ./logs/{date}.log 文件日志
├── Styles/ModernTheme.xaml  统一现代风格资源
├── Models/Models.cs         共享数据模型
├── appsettings.json         运行时配置
├── app.manifest             DPI / asInvoker / SmartScreen 减负
└── VantageWorkstationPlus.csproj
```

## 编译

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fx
```

WSL/Linux 上跑也行（项目设了 `EnableWindowsTargeting`）。产物：`publish-fx/VantageWorkstationPlus.exe`（~8.4 MB）。

## 已知限制

- **代码未签名** → 首次启动 SmartScreen 警告，须手动允许
- **脱水仅支持单缸单篮**：UI 设计上一次只处理一个 basket-to-retort 装载；多并发请求需要批量按缸分别操作
- **服务端不会自动清理超时缸**：脱水时长到点后服务器仍标 `IsInProcess=true`，需手动或本工具 10s 轮询自动收尾
- **Web JWT 与 SOAP AuthHeader 是两套鉴权**，密码登录时同时跑两份；不能用 Web 登录顶替 SOAP，反之亦然
- **依赖 .NET 8 Desktop Runtime**：目标机若未装会启动失败

## 安全

- 工作站 AES key 存在用户级 Windows 环境变量（与原版 WPF 客户端做法一致），同机其他 Windows 用户可读取
- Web 登录密码不持久化，仅在内存中
- 用户输入的服务器地址保存在 `Properties.Settings`（未加密）
- TLS 证书校验默认放行（`AcceptAnyServerCert: true`），针对自签内网；公网部署务必改为 `false` 并配置受信证书

## 许可

私有项目，仅限内部使用。
