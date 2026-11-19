use serde_json::{json, Value};

/// 返回所有向外部 Agent 暴露的 MCP 工具定义。
///
/// 红线约定：
/// - 原始使用记录（时长流水、会话片段、网页浏览流水）仅暴露只读工具，物理隔离任何增删改操作；
/// - 备份导入/回滚、WebDAV 云同步配置及密码、Mcp.Token 均不注册任何工具。
pub fn list_tools() -> Vec<Value> {
    vec![
        tool(
            "get_usage_overview",
            "[READ-ONLY] 获取指定日期或区间的屏幕使用概览（总时长、应用与网站分布、会话统计、Top 排行、可选环比上一周期）。原始使用记录绝对只读。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省为今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "timezone": { "type": "string", "description": "IANA 时区，如 Asia/Shanghai（缺省使用宿主机时区）" },
                    "browser_merged": { "type": "boolean", "description": "是否将网页时长并入浏览器行，默认 false（独立显示）" },
                    "compare_previous": { "type": "boolean", "description": "是否附带上一等长周期的环比数据，默认 false" },
                    "top_limit": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Top 应用/网站条数上限，默认 15" }
                }
            }),
        ),
        tool(
            "get_usage_timeline",
            "[READ-ONLY] 获取按小时（单日 24 桶）、按天（区间每日）或按月（全年 12 月）聚合的实体与分类使用时长分布。原始使用记录绝对只读。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "buckets": { "type": "string", "enum": ["hour", "day", "month"], "description": "时间桶粒度：hour / day / month，默认按区间自动选择（单日 hour，多日 day）" },
                    "browser_merged": { "type": "boolean", "description": "是否并入浏览器，默认 false" },
                    "top_entities": { "type": "integer", "minimum": 1, "maximum": 50, "description": "返回总时长排名前 N 个实体的分桶序列，默认 15" },
                    "timezone": { "type": "string", "description": "IANA 时区（缺省宿主机时区）" }
                }
            }),
        ),
        tool(
            "get_category_breakdown",
            "[READ-ONLY] 获取指定区间内各分类的汇总时长、占比、可选环比，或查看某一分类下的应用/网站成员排行。原始使用记录绝对只读。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "scope": { "type": "string", "enum": ["unified", "app", "web"], "description": "分类视角：unified（统一分类，默认）/ app / web" },
                    "category_id": { "type": "integer", "description": "可选：指定分类 ID 时额外返回该分类下的成员时长明细" },
                    "compare_previous": { "type": "boolean", "description": "指定 category_id 时是否计算上一周期环比，默认 false" },
                    "timezone": { "type": "string", "description": "IANA 时区（缺省宿主机时区）" }
                }
            }),
        ),
        tool(
            "get_app_sessions",
            "[READ-ONLY] 读取精确到起止时间的应用使用会话片段明细（StartTime、EndTime、Duration、AppName），用于深度复盘。原始记录绝对只读，不可修改。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "app_id": { "type": "integer", "description": "可选：仅过滤指定应用 ID" },
                    "app_name": { "type": "string", "description": "可选：按应用名/别名/描述模糊过滤（不区分大小写）" },
                    "min_duration_seconds": { "type": "integer", "minimum": 0, "description": "可选：仅返回单次时长 >= N 秒的会话" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "分页条数上限，默认 100，最大 500" },
                    "offset": { "type": "integer", "minimum": 0, "description": "分页偏移量，默认 0" },
                    "timezone": { "type": "string", "description": "IANA 时区（缺省宿主机时区）" }
                }
            }),
        ),
        tool(
            "get_hourly_logs",
            "[READ-ONLY] 读取按小时记录的应用原始流水条目（DataTime、App、Time 秒数）。原始记录绝对只读，不可修改。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "app_id": { "type": "integer", "description": "可选：仅过滤指定应用 ID" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "分页条数上限，默认 100，最大 500" },
                    "offset": { "type": "integer", "minimum": 0, "description": "分页偏移量，默认 0" },
                    "timezone": { "type": "string", "description": "IANA 时区（缺省宿主机时区）" }
                }
            }),
        ),
        tool(
            "get_web_browse_logs",
            "[READ-ONLY] 读取精细网页浏览流水记录（时间、域名、完整 URL、页面标题、停留秒数、浏览器）。原始记录绝对只读，不可修改。",
            json!({
                "type": "object",
                "properties": {
                    "start_date": { "type": "string", "description": "起始日期 YYYY-MM-DD（缺省今天）" },
                    "end_date": { "type": "string", "description": "结束日期 YYYY-MM-DD（缺省等于 start_date）" },
                    "site_id": { "type": "integer", "description": "可选：仅过滤指定站点 ID" },
                    "domain": { "type": "string", "description": "可选：按域名子串过滤（不区分大小写）" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "分页条数上限，默认 100，最大 500" },
                    "offset": { "type": "integer", "minimum": 0, "description": "分页偏移量，默认 0" },
                    "timezone": { "type": "string", "description": "IANA 时区（缺省宿主机时区）" }
                }
            }),
        ),
        tool(
            "list_apps",
            "[READ-ONLY] 列出已记录的桌面应用元数据（ID、进程名、别名、描述、路径、当前分类及只读累计时长）。",
            json!({
                "type": "object",
                "properties": {
                    "category_id": { "type": "integer", "description": "可选：按分类 ID 过滤" },
                    "only_uncategorized": { "type": "boolean", "description": "仅返回未归类（系统默认分类）的应用，默认 false" },
                    "search": { "type": "string", "description": "可选：按名称、别名、描述、路径模糊搜索" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "返回条数上限，默认 100" },
                    "offset": { "type": "integer", "minimum": 0, "description": "偏移量，默认 0" }
                }
            }),
        ),
        tool(
            "list_websites",
            "[READ-ONLY] 列出已记录的网站元数据（ID、域名、标题、别名、当前分类及只读累计时长）。",
            json!({
                "type": "object",
                "properties": {
                    "category_id": { "type": "integer", "description": "可选：按分类 ID 过滤" },
                    "only_uncategorized": { "type": "boolean", "description": "仅返回未归类（默认分类）的网站，默认 false" },
                    "search": { "type": "string", "description": "可选：按域名、标题、别名模糊搜索" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "返回条数上限，默认 100" },
                    "offset": { "type": "integer", "minimum": 0, "description": "偏移量，默认 0" }
                }
            }),
        ),
        tool(
            "list_categories",
            "读取时见中所有的应用分类与网站分类。",
            json!({
                "type": "object",
                "properties": {}
            }),
        ),
        tool(
            "manage_category",
            "创建、更新、删除应用分类或网站分类，或恢复系统默认分类。",
            json!({
                "type": "object",
                "required": ["action"],
                "properties": {
                    "scope": { "type": "string", "enum": ["app", "web"], "description": "分类作用域：app（默认，统一分类）或 web（仅网站分类）" },
                    "action": { "type": "string", "enum": ["create", "update", "delete", "restore"], "description": "操作类型" },
                    "id": { "type": "integer", "description": "update / delete / restore 时必填分类 ID" },
                    "name": { "type": "string", "description": "分类名称（create 必填，update 可选/必填）" },
                    "color": { "type": "string", "description": "颜色十六进制字符串，如 #5E81AC" },
                    "icon_file": { "type": "string", "description": "图标路径或内置 SVG 标识" },
                    "is_directory_match": { "type": "boolean", "description": "是否启用安装目录正则匹配（app 分类）" },
                    "directories": { "type": "string", "description": "目录匹配规则列表（JSON 数组字符串或换行分隔）" },
                    "is_url_match": { "type": "boolean", "description": "是否启用 URL 正则匹配" },
                    "url_patterns": { "type": "string", "description": "URL 匹配规则列表（JSON 数组字符串或换行分隔）" }
                }
            }),
        ),
        tool(
            "assign_apps_category",
            "批量修改应用的分类 ID、别名或描述。不会也绝不允许修改应用的原始累计时长。",
            json!({
                "type": "object",
                "required": ["items"],
                "properties": {
                    "items": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 200,
                        "items": {
                            "type": "object",
                            "required": ["app_id"],
                            "properties": {
                                "app_id": { "type": "integer", "description": "应用 ID" },
                                "category_id": { "type": "integer", "description": "目标分类 ID（可选）" },
                                "alias": { "type": ["string", "null"], "description": "应用别名（可选，传入 null 清空）" },
                                "description": { "type": ["string", "null"], "description": "应用描述（可选）" }
                            }
                        }
                    }
                }
            }),
        ),
        tool(
            "assign_sites_category",
            "批量修改网站的分类归属、别名或标题。不会也绝不允许修改网站的原始累计时长。",
            json!({
                "type": "object",
                "required": ["items"],
                "properties": {
                    "items": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 200,
                        "items": {
                            "type": "object",
                            "required": ["site_id"],
                            "properties": {
                                "site_id": { "type": "integer", "description": "网站 ID" },
                                "category_id": { "type": "integer", "description": "目标分类 ID（可选）" },
                                "alias": { "type": ["string", "null"], "description": "网站别名（可选）" },
                                "title": { "type": ["string", "null"], "description": "网站标题（可选）" }
                            }
                        }
                    }
                }
            }),
        ),
        tool(
            "run_auto_classify",
            "执行内置自动分类或规则匹配（app_auto：内置常用软件与站点智能归类；directory_match：按分类目录规则匹配；url_match：按分类 URL 规则匹配）。",
            json!({
                "type": "object",
                "required": ["mode"],
                "properties": {
                    "mode": { "type": "string", "enum": ["app_auto", "directory_match", "url_match"], "description": "归类模式" },
                    "patterns": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "可选：仅执行指定正则列表（适用于 directory_match / url_match）"
                    }
                }
            }),
        ),
        tool(
            "list_merge_groups",
            "列出所有子母程序 / 应用与站点合并组以及已被解散抑制的自动组标识。",
            json!({
                "type": "object",
                "properties": {}
            }),
        ),
        tool(
            "manage_merge_group",
            "创建、更新、重命名、解散子母程序合并组，或恢复已抑制的自动合并组。",
            json!({
                "type": "object",
                "required": ["action"],
                "properties": {
                    "action": { "type": "string", "enum": ["create", "update", "rename", "dissolve", "restore"], "description": "操作类型" },
                    "group_id": { "type": "integer", "description": "update / rename / dissolve 时必填组 ID" },
                    "name": { "type": "string", "description": "组显示名称" },
                    "primary_ref": { "type": "string", "description": "主成员引用，格式 app:<进程名> 或 site:<域名>" },
                    "members": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "成员引用列表，格式如 [\"app:Code\", \"app:CodeSetup\"] 或 [\"site:github.com\"]"
                    }
                }
            }),
        ),
        tool(
            "get_settings",
            "读取时见系统配置（General 常规与外观、Behavior 统计与忽略/白名单规则、Shortcut 快捷键）。出于安全考虑，WebDAV 云同步配置及密码与 MCP Token 不予返回。",
            json!({
                "type": "object",
                "properties": {}
            }),
        ),
        tool(
            "update_settings",
            "增量修改时见系统配置（支持 General、Behavior、Shortcut 字段，以及便捷增删忽略进程/忽略 URL/进程白名单）。WebDAV 与 MCP Token 受保护不可通过此工具修改。",
            json!({
                "type": "object",
                "properties": {
                    "general": { "type": "object", "description": "要合并更新的 General 字段（camelCase 或 PascalCase）" },
                    "behavior": { "type": "object", "description": "要合并更新的 Behavior 字段（如 ignoreProcessList, ignoreUrlList, isWhiteList, processWhiteList, isSleepWatch, inactiveThreshold）" },
                    "shortcut": { "type": "object", "description": "要合并更新的 Shortcut 字段（refresh, search, navigateBack）" },
                    "add_ignore_processes": { "type": "array", "items": { "type": "string" }, "description": "便捷追加到忽略进程列表的正则/名称" },
                    "remove_ignore_processes": { "type": "array", "items": { "type": "string" }, "description": "便捷从忽略进程列表移除的条目" },
                    "add_ignore_urls": { "type": "array", "items": { "type": "string" }, "description": "便捷追加到忽略 URL 列表的规则" },
                    "remove_ignore_urls": { "type": "array", "items": { "type": "string" }, "description": "便捷从忽略 URL 列表移除的条目" }
                }
            }),
        ),
        tool(
            "list_plugins",
            "列出所有已安装插件的清单、启用状态、配置 Schema、当前配置、运行状态和状态记录。",
            json!({
                "type": "object",
                "properties": {
                    "plugin_id": { "type": "string", "description": "可选：指定插件 ID 只返回该插件完整详情" }
                }
            }),
        ),
        tool(
            "configure_plugin",
            "管理插件启停、更新插件配置（含使用时长提醒插件的可视化规则与 Lua 脚本）、写入状态记录或下发运行指令。",
            json!({
                "type": "object",
                "required": ["plugin_id", "action"],
                "properties": {
                    "plugin_id": { "type": "string", "description": "插件 ID，例如 reminder" },
                    "action": { "type": "string", "enum": ["set_enabled", "save_config", "upsert_record", "push_command"], "description": "操作类型" },
                    "enabled": { "type": "boolean", "description": "action=set_enabled 时必填" },
                    "values": { "type": "object", "description": "action=save_config 时必填配置键值对象（可增量合并现有配置，如 {\"mode\":\"Lua 脚本\",\"script\":\"...\"}）" },
                    "merge_config": { "type": "boolean", "description": "action=save_config 时是否与当前配置合并，默认 true" },
                    "record_key": { "type": "string", "description": "action=upsert_record 时必填键名" },
                    "record_value": { "description": "action=upsert_record 时必填 JSON 值" },
                    "cmd": { "type": "string", "description": "action=push_command 时必填指令名" },
                    "cmd_data": { "description": "action=push_command 时可选负载数据" }
                }
            }),
        ),
    ]
}

pub fn list_resources() -> Vec<Value> {
    vec![
        json!({
            "uri": "shijian://overview/today",
            "name": "今日屏幕使用概览",
            "description": "[READ-ONLY] 今天的屏幕使用汇总、分类占比与 Top 应用/网站",
            "mimeType": "application/json"
        }),
        json!({
            "uri": "shijian://categories",
            "name": "分类列表",
            "description": "当前所有应用分类与网站分类",
            "mimeType": "application/json"
        }),
        json!({
            "uri": "shijian://settings/public",
            "name": "系统配置（脱敏）",
            "description": "当前常规、统计规则与快捷键配置（不含 WebDAV 与 MCP Token）",
            "mimeType": "application/json"
        }),
    ]
}

fn tool(name: &str, description: &str, input_schema: Value) -> Value {
    json!({
        "name": name,
        "description": description,
        "inputSchema": input_schema
    })
}
