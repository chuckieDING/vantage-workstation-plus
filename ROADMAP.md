# Roadmap

未来 4 项优化方向，按优先级排序。每项一个 feature 分支 / 一个 PR，逐步合入 main。

---

## 1. 错误恢复友好化（优先级：高）

**目标**：批量操作中网络抖动、超时、单条业务失败不再让用户从头重跑。

**功能点**
- HTTP / SOAP 请求级**指数退避重试**（瞬时错误如 `HttpRequestException`、`TaskCanceledException`、HTTP 502/503/504）
- **断点续传**：批量任务把"已成功"的 ID 写到 `./logs/batch-{guid}.checkpoint.json`，崩溃 / 取消后下次启动检测到未完成 checkpoint 弹"是否继续上次未完成的批次？"
- **错误分类**：日志和 UI 区分以下类别（彩色标记）
  - 🌐 网络问题 — 可重试
  - 🔒 鉴权问题 — 提示重新登录
  - 📋 业务问题 — 跳过当前条目继续
  - 💥 严重错误 — 中止整批
- **失败明细导出**：批次结束时弹出"导出失败项到 Excel"按钮，含失败 ID + 错误原因，方便修正后重跑

**涉及文件**
- `Services/RetryPolicy.cs`（新增）
- `Services/BatchCheckpoint.cs`（新增）
- `Panels/{Signoff,Dehydration,Archive,Transfer}Panel.xaml.cs` 各自批量循环改造

**完成定义**
- 网络中断 5 秒后能自动恢复继续跑
- Kill 进程后重启，能问"继续上次未完成"
- 失败明细可直接导出为可读 Excel

---

## 2. Excel 模板生成器（优先级：高）

**目标**：用户不再凭记忆手填 ID 格式，工具按当前业务上下文生成预填模板。

**功能点**
- 4 个 Panel 各加「下载模板」按钮，根据当前选择生成对应 xlsx：
  - **批量出片**：当前位置 + 当前医生下，今天扫码完成的玻片清单（一列 ID + 一列状态供勾选）
  - **批量脱水**：当前篮 / 缸下未脱水的包埋盒清单
  - **批量归档**：当前 case 下未归档的物品清单（含蜡块 / 玻片，多 sheet）
  - **批量流转**：当前位置下流转中物品清单
- 模板带**表头说明**和**示例行**（首行批注 "在这一列填要操作的 ID，第一列为 ID，其他列为参考信息"）
- 用现有 `DocumentFormat.OpenXml` 依赖，0 新依赖

**涉及文件**
- `Services/ExcelTemplateWriter.cs`（新增，与 `SlideListReader.cs` 配对）
- 4 个 Panel 加按钮 + Click handler

**完成定义**
- 4 个 Tab 都能下载到带数据的可用模板
- 模板下载后用 Excel 打开内容正确（无格式错误 / 空表）
- 模板填好可被现有 `SlideListReader.Read()` 解析

---

## 3. 数据库数据源（优先级：中，工程量大）

**目标**：不再依赖 Excel 文件，可定时从医院 LIS / HIS 数据库直接抽取要操作的对象列表。

**功能点**

### 3.1 多数据库连接
- 支持 **SQL Server / MySQL / PostgreSQL / Oracle** 4 种主流类型
- 连接串配置 UI（设置对话框新增「数据源」Tab）：
  - 类型下拉
  - 主机 / 端口 / 数据库 / 用户名 / 密码
  - 测试连接按钮
  - 密码用 DPAPI 加密后存到 appsettings
- 连接池复用，避免每次抽取都新建连接

### 3.2 字段映射配置
- 每个 Panel 配置查询 SQL（用 `{date}`、`{user}` 等占位符）+ 字段映射：
  - 哪一列是物品 ID（必填）
  - 哪一列是辅助信息（在 UI 显示用，可选）
- 配置存在 `appsettings.json` 的 `DataSources` 段

### 3.3 定时抽取策略
- 每个数据源可配置 cron 表达式（如"工作日早 8 点"）
- 后台 `DispatcherTimer`/`Quartz.NET` 触发，抽取结果存到 `./inbox/{panel}-{timestamp}.xlsx`
- UI 通知 "有 N 条新任务，是否查看？" → 自动加载到对应 Panel

**涉及文件 / 依赖**
- 新增依赖：`Microsoft.Data.SqlClient` / `MySqlConnector` / `Npgsql` / `Oracle.ManagedDataAccess.Core`
- `Services/DbSourceConnector.cs`（新增，抽象接口 + 4 个实现）
- `Services/DbScheduler.cs`（新增）
- `Dialogs/DataSourceConfigDialog.xaml`（新增）

**完成定义**
- 用户在 UI 配置完 SQL Server 连接，能成功测试通过
- 定时抽取触发后任务自动出现在对应 Panel
- 抽取失败有 toast 通知，不阻塞 UI

**风险**
- 4 种数据库驱动加起来 EXE 体积会从 8 MB 涨到 ~25 MB
- DPAPI 加密只能本机解密，多人共享配置时密码需重输

---

## 4. UI 优化（优先级：中）

**目标**：在现有 Fluent 风格基础上进一步提升易用性和美观度。

**功能点**

### 4.1 设置对话框
- 替代手动编辑 `appsettings.json`
- Tabs：「常规」（BaseUrl / WorkCellId）、「数据源」（DB 配置）、「外观」（主题）、「关于」
- 配置改动即时保存到 appsettings.json，无需重启

### 4.2 主题切换
- 浅色 / 深色 / 跟随系统
- ModernTheme.xaml 已经语义化命名（B.Bg/B.Surface 等），只需加一份深色色板

### 4.3 状态指示器升级
- 当前批量进度只有 ProgressBar，加每条目 status：
  - 待处理（灰）
  - 处理中（蓝旋转）
  - 成功（绿 ✓）
  - 失败（红 ✗，hover 显示原因）
- 用 `ListView` 替代 `WrapPanel` 的 chip 显示

### 4.4 键盘快捷键
- `Ctrl+O` 选文件
- `Ctrl+Enter` 开始批量
- `Ctrl+1..4` 切换 Tab
- `F5` 刷新
- `Esc` 中止当前批量

### 4.5 工具提示与帮助
- 所有按钮加 ToolTip 说明（操作含义 + 快捷键）
- 主窗口加 ❓ 按钮，弹"帮助"对话框含常见问题
- 首次启动 Tab 时显示一句简短引导文字

**涉及文件**
- `Dialogs/SettingsDialog.xaml(.cs)`（新增）
- `Styles/ModernTheme.Dark.xaml`（新增）
- `Panels/*.xaml` 加快捷键 binding + 工具提示

**完成定义**
- 设置对话框能改 BaseUrl 不需要手动编辑 JSON
- 深色主题切换实时生效
- 至少 5 个快捷键可用

---

## 节奏

| Phase | PR | 预计工作量 |
|---|---|---|
| 1 | 错误恢复 | 2-3 天 |
| 2 | Excel 模板 | 1-2 天 |
| 3 | 数据库支持 | 5-7 天 |
| 4 | UI 优化 | 3-4 天 |

每个 PR 独立、按顺序合入 main。后续每次代码改动都走 PR，不直接推 main。
