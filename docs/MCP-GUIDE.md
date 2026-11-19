# 时见（ShiJian）MCP 接入指南

时见内置了 **Model Context Protocol (MCP)** 服务，让本机安装的任意 AI Agent（Claude Desktop、Cursor、Antigravity、Claude Code、Codex、OpenCode 等）能够安全读取你的屏幕使用统计与细粒度使用明细，并协助管理分类、子母程序合并组、过滤规则与插件配置。

---

## 一、接入方式与优先选择原则

时见同时提供 **Streamable HTTP** 与 **极薄 STDIO 桥接** 两种接入方式，均默认关闭，需在客户端 **设置 → AI 与 MCP** 中开启，并通过自动生成的本地 **Bearer Token** 鉴权。

### 优先选择顺序（请严格按此顺序配置）

1. **首选：Streamable HTTP（推荐）**
   - 端点：`http://127.0.0.1:37091/mcp`（若设置了 `SHIJIAN_SERVER` 环境变量，则随对应端口变化）
   - 鉴权头：`Authorization: Bearer <token>`
   - **优先原因**：直接复用常驻的 `shijian-server` 后台进程，**零额外子进程启动与内存开销**，响应最快。
2. **回退：STDIO 命令行桥接（`shijian-server --mcp`）**
   - 命令：`shijian-server.exe --mcp`（macOS 为 `shijian-server --mcp`）
   - **适用条件**：仅当用户所使用的 Agent/客户端**不支持 HTTP MCP**（仅支持 `command` + `args` 的本地进程模式），或本地 HTTP 直连测试失败时回退使用。
   - **工作机制**：`shijian-server --mcp` 会在启动最早期跳过数据库与单实例锁，仅作为极薄 JSON-RPC 转发器将 `stdin`/`stdout` 代理至本机运行中的 `shijian-server` `/mcp` 端点（默认读取 `AppConfig.json` 中的最新 Bearer Token，也支持通过 `--url` / `--token` 或环境变量 `SHIJIAN_SERVER` / `SHIJIAN_MCP_TOKEN` 覆盖）。

> **零门槛安装**：在客户端 **设置 → AI 与 MCP** 中点击 **「复制安装提示词」**，将提示词直接发给电脑上的任意 Agent，Agent 即可按「HTTP 优先、STDIO 回退」的规则自动完成配置与连通性自检。

---

## 二、安全红线与能力边界

| 能力类别 | 权限 | 说明 |
| --- | --- | --- |
| 统计汇总（今日/周/月/自定义区间概览、环比、分类占比、时间轴） | **只读** | 聚合结果直接复用 `shijian-server` 服务计算 |
| 细粒度原始流水（应用会话 `sessions`、小时流水 `hours`、网页浏览流水） | **绝对只读** | **物理不注册任何写入/修改/删除原始记录的工具** |
| 应用与网站分类、批量归类、自动匹配 | **可读写** | 修改分类与别名时严格保留原始累计时长不变 |
| 子母程序合并组（创建/编辑/重命名/解散/恢复） | **可读写** | 仅操作合并关系元数据，原始流水一行不改 |
| 系统与过滤规则设置（常规、外观、行为、忽略进程/URL、白名单、快捷键） | **可读写** | **WebDAV 节点与 `Mcp.Token` 完全隔离，不可读不可改** |
| 插件与脚本（启停、插件配置、提醒规则与 Lua 脚本） | **可读写** | 支持读取 Schema 并读写配置、状态记录与指令 |
| 备份覆盖导入 / 回滚 / WebDAV 云同步及密码 | **禁止开放** | 对 MCP 完全屏蔽 |

---

## 三、已暴露 MCP 工具清单

### 只读查询工具（`[READ-ONLY]`）
- `get_usage_overview`：指定日期/区间总览、Top 实体、分类分布与可选环比
- `get_usage_timeline`：按小时（`hour`）/ 按天（`day`）/ 按月（`month`）分桶趋势
- `get_category_breakdown`：分类占比与单分类成员排行（支持 `unified` / `app` / `web`）
- `get_app_sessions`：精确到起止时间的应用使用会话片段明细（支持过滤与分页）
- `get_hourly_logs`：按小时聚合的应用原始流水明细（支持过滤与分页）
- `get_web_browse_logs`：网页浏览流水明细（时间、域名、URL、标题、停留秒数、浏览器）
- `list_apps`：应用元数据列表（支持未分类过滤、搜索、分页）
- `list_websites`：网站元数据列表（支持未分类过滤、搜索、分页）

### 配置与管理工具
- `list_categories` / `manage_category`：读取与增删改应用/网站分类、恢复系统默认分类
- `assign_apps_category` / `assign_sites_category`：批量修改应用/网站分类归属、别名或描述（不改原始时长）
- `run_auto_classify`：执行内置智能归类 / 目录规则匹配 / URL 规则匹配
- `list_merge_groups` / `manage_merge_group`：管理子母程序合并组
- `get_settings` / `update_settings`：读取与增量更新系统配置（已剥离并保护 WebDAV 与 MCP Token）
- `list_plugins` / `configure_plugin`：管理插件启停、配置（含提醒插件规则与 Lua 脚本）、记录与指令
