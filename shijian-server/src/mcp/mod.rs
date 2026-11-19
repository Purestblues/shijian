pub mod schema;
pub mod stdio;
pub mod tools;

use axum::{
    extract::{Extension, State},
    http::{header, HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::post,
    Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};
use sqlx::SqlitePool;
use std::sync::Arc;
use tokio::sync::watch;

use crate::plugins::PluginState;
use crate::services::config::ConfigService;
use tools::McpContext;

const SUPPORTED_PROTOCOLS: &[&str] = &["2025-03-26", "2024-11-05"];
const SERVER_INSTRUCTIONS: &str = "时见（ShiJian）本地屏幕使用时间 MCP 服务。\n\
红线约束：所有原始使用记录（应用时长流水、会话片段、网页浏览流水）绝对只读，不可修改或删除；\n\
WebDAV 云同步配置与密码、备份导入/回滚、MCP Token 均对 MCP 屏蔽。\n\
你可以通过工具读取统计概览、时间分布、精细会话与浏览流水，并管理分类、子母程序合并组、系统过滤/常规设置及插件脚本。";

pub fn router() -> Router<SqlitePool> {
    Router::new().route(
        "/mcp",
        post(handle_post).get(handle_get).delete(handle_delete),
    )
}

async fn authorize_mcp(
    headers: &HeaderMap,
    config_service: &ConfigService,
) -> Result<(), Response> {
    let config = match config_service.get_or_load().await {
        Ok(c) => c,
        Err(e) => {
            return Err((
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "code": 500, "message": format!("加载配置失败: {e}") })),
            )
                .into_response());
        }
    };

    let provided = extract_bearer_token(headers);
    let expected = config.mcp.token.trim();
    if expected.is_empty()
        || !provided
            .as_deref()
            .map(|p| constant_time_eq(p.as_bytes(), expected.as_bytes()))
            .unwrap_or(false)
    {
        return Err((
            StatusCode::UNAUTHORIZED,
            [(header::WWW_AUTHENTICATE, "Bearer realm=\"shijian-mcp\"")],
            Json(json!({
                "code": 401,
                "message": "Unauthorized: valid MCP Bearer token required"
            })),
        )
            .into_response());
    }

    if !config.mcp.enabled {
        return Err((
            StatusCode::FORBIDDEN,
            Json(json!({
                "code": 403,
                "message": "ShiJian MCP is disabled. Enable it in Settings -> AI & MCP."
            })),
        )
            .into_response());
    }

    Ok(())
}

fn extract_bearer_token(headers: &HeaderMap) -> Option<String> {
    let raw = headers.get(header::AUTHORIZATION)?.to_str().ok()?.trim();
    if raw.len() < 7 {
        return None;
    }
    let (scheme, rest) = raw.split_at(7);
    if !scheme.eq_ignore_ascii_case("bearer ") {
        return None;
    }
    let token = rest.trim();
    if token.is_empty() {
        None
    } else {
        Some(token.to_string())
    }
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

async fn handle_get(
    headers: HeaderMap,
    Extension(config_service): Extension<Arc<ConfigService>>,
) -> Response {
    if let Err(resp) = authorize_mcp(&headers, &config_service).await {
        return resp;
    }
    (
        StatusCode::METHOD_NOT_ALLOWED,
        [(header::ALLOW, "POST, DELETE")],
        "SSE stream on GET /mcp is not offered; use POST /mcp for JSON-RPC requests.",
    )
        .into_response()
}

async fn handle_delete(
    headers: HeaderMap,
    Extension(config_service): Extension<Arc<ConfigService>>,
) -> Response {
    if let Err(resp) = authorize_mcp(&headers, &config_service).await {
        return resp;
    }
    StatusCode::OK.into_response()
}

async fn handle_post(
    headers: HeaderMap,
    State(pool): State<SqlitePool>,
    Extension(config_service): Extension<Arc<ConfigService>>,
    Extension(web_enabled_tx): Extension<watch::Sender<bool>>,
    Extension(plugin_state): Extension<Arc<PluginState>>,
    body: String,
) -> Response {
    if let Err(resp) = authorize_mcp(&headers, &config_service).await {
        return resp;
    }

    let payload: Value = match serde_json::from_str(&body) {
        Ok(v) => v,
        Err(e) => {
            return (
                StatusCode::BAD_REQUEST,
                Json(rpc_error(Value::Null, -32700, &format!("Parse error: {e}"))),
            )
                .into_response();
        }
    };

    let ctx = McpContext {
        pool,
        config_service,
        web_enabled_tx,
        plugin_state,
    };

    if let Some(items) = payload.as_array() {
        if items.is_empty() {
            return (
                StatusCode::BAD_REQUEST,
                Json(rpc_error(Value::Null, -32600, "Invalid Request: empty batch")),
            )
                .into_response();
        }
        let mut responses = Vec::new();
        for item in items {
            if let Some(resp) = dispatch_single(&ctx, item.clone()).await {
                responses.push(resp);
            }
        }
        if responses.is_empty() {
            return StatusCode::ACCEPTED.into_response();
        }
        return (StatusCode::OK, Json(Value::Array(responses))).into_response();
    }

    match dispatch_single(&ctx, payload).await {
        Some(resp) => (StatusCode::OK, Json(resp)).into_response(),
        None => StatusCode::ACCEPTED.into_response(),
    }
}

#[derive(Deserialize)]
struct JsonRpcRequest {
    jsonrpc: Option<String>,
    id: Option<Value>,
    method: String,
    #[serde(default)]
    params: Value,
}

async fn dispatch_single(ctx: &McpContext, raw: Value) -> Option<Value> {
    let req: JsonRpcRequest = match serde_json::from_value(raw) {
        Ok(r) => r,
        Err(e) => return Some(rpc_error(Value::Null, -32600, &format!("Invalid Request: {e}"))),
    };

    if req.jsonrpc.as_deref() != Some("2.0") {
        return req
            .id
            .map(|id| rpc_error(id, -32600, "Invalid Request: jsonrpc must be \"2.0\""));
    }

    // 所有通知（无 id 或 notifications/*）不返回响应体
    if req.id.is_none() || req.method.starts_with("notifications/") {
        return None;
    }
    let id = req.id.unwrap();

    let result = match req.method.as_str() {
        "initialize" => {
            let client_ver = req
                .params
                .get("protocolVersion")
                .and_then(|v| v.as_str())
                .unwrap_or("2025-03-26");
            let negotiated = if SUPPORTED_PROTOCOLS.contains(&client_ver) {
                client_ver
            } else {
                SUPPORTED_PROTOCOLS[0]
            };
            Ok(json!({
                "protocolVersion": negotiated,
                "capabilities": {
                    "tools": { "listChanged": false },
                    "resources": { "subscribe": false, "listChanged": false }
                },
                "serverInfo": {
                    "name": "shijian-mcp",
                    "version": env!("CARGO_PKG_VERSION")
                },
                "instructions": SERVER_INSTRUCTIONS
            }))
        }
        "ping" => Ok(json!({})),
        "tools/list" => Ok(json!({ "tools": schema::list_tools() })),
        "tools/call" => {
            let name = req
                .params
                .get("name")
                .and_then(|v| v.as_str())
                .unwrap_or("");
            if name.is_empty() {
                return Some(rpc_error(id, -32602, "Invalid params: missing tool name"));
            }
            let args = req
                .params
                .get("arguments")
                .cloned()
                .unwrap_or_else(|| json!({}));

            match tools::call_tool(ctx, name, args).await {
                Ok(data) => {
                    let text = serde_json::to_string_pretty(&data)
                        .unwrap_or_else(|_| data.to_string());
                    Ok(json!({
                        "content": [{ "type": "text", "text": text }],
                        "isError": false
                    }))
                }
                Err(err_msg) => Ok(json!({
                    "content": [{ "type": "text", "text": err_msg }],
                    "isError": true
                })),
            }
        }
        "resources/list" => Ok(json!({ "resources": schema::list_resources() })),
        "resources/read" => {
            let uri = req
                .params
                .get("uri")
                .and_then(|v| v.as_str())
                .unwrap_or("");
            if uri.is_empty() {
                return Some(rpc_error(id, -32602, "Invalid params: missing resource uri"));
            }
            match tools::read_resource(ctx, uri).await {
                Ok(data) => {
                    let text = serde_json::to_string_pretty(&data)
                        .unwrap_or_else(|_| data.to_string());
                    Ok(json!({
                        "contents": [{
                            "uri": uri,
                            "mimeType": "application/json",
                            "text": text
                        }]
                    }))
                }
                Err(err_msg) => return Some(rpc_error(id, -32602, &err_msg)),
            }
        }
        other => return Some(rpc_error(id, -32601, &format!("Method not found: {other}"))),
    };

    Some(match result {
        Ok(res) => json!({
            "jsonrpc": "2.0",
            "id": id,
            "result": res
        }),
        Err(err) => err,
    })
}

fn rpc_error(id: Value, code: i64, message: &str) -> Value {
    json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": {
            "code": code,
            "message": message
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::config::ConfigModel;
    use crate::services::backup::BackupPaths;
    use crate::{create_app, db};
    use axum::body::Body;
    use axum::http::Request;
    use http_body_util::BodyExt;
    use tower::ServiceExt;

    async fn setup_test_env(
        enabled: bool,
        token: &str,
    ) -> (
        tempfile_guard::TempDir,
        axum::Router,
        SqlitePool,
        Arc<ConfigService>,
    ) {
        let dir = tempfile_guard::TempDir::new("shijian_mcp_test");
        let data_dir = dir.path.join("data");
        let plugins_dir = dir.path.join("plugins");
        let web_favicons = dir.path.join("favicons");
        std::fs::create_dir_all(&data_dir).unwrap();
        std::fs::create_dir_all(plugins_dir.join("reminder")).unwrap();
        std::fs::write(
            plugins_dir.join("reminder").join("plugin.json"),
            r#"{"id":"reminder","name":"提醒","version":"0.1.0","entry":"shijian-plugin-reminder","configSchema":[{"key":"mode","label":"模式","type":"select","default":"可视化规则"},{"key":"script","label":"脚本","type":"text","default":""}]}"#,
        )
        .unwrap();

        let db_path = data_dir.join("data.db");
        let pool = db::init_db(db_path.to_str().unwrap(), "Asia/Shanghai")
            .await
            .unwrap();
        let config_service = Arc::new(ConfigService::new(&data_dir));
        let mut cfg = ConfigModel::default();
        cfg.mcp.enabled = enabled;
        cfg.mcp.token = token.to_string();
        cfg.webdav.enabled = true;
        cfg.webdav.url = "https://dav.example.com".to_string();
        cfg.webdav.username = "alice".to_string();
        cfg.webdav.password = "super_secret_pw".to_string();
        config_service.save(&cfg).await.unwrap();

        let (web_tx, _web_rx) = watch::channel(false);
        let plugin_state = Arc::new(PluginState::init(&plugins_dir, &data_dir).await.unwrap());
        let backup_paths = Arc::new(BackupPaths::from_env(&data_dir, &web_favicons, &dir.path));

        let app = create_app(
            pool.clone(),
            config_service.clone(),
            web_tx,
            plugin_state,
            backup_paths,
            &dir.path,
        );

        (dir, app, pool, config_service)
    }

    mod tempfile_guard {
        use std::path::PathBuf;
        pub struct TempDir {
            pub path: PathBuf,
        }
        impl TempDir {
            pub fn new(prefix: &str) -> Self {
                let nanos = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos();
                let path = std::env::temp_dir().join(format!("{prefix}_{}_{nanos}", std::process::id()));
                std::fs::create_dir_all(&path).unwrap();
                Self { path }
            }
        }
        impl Drop for TempDir {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.path);
            }
        }
    }

    async fn post_mcp(app: &axum::Router, auth: Option<&str>, body: Value) -> (StatusCode, String) {
        let mut builder = Request::builder()
            .method("POST")
            .uri("/mcp")
            .header(header::CONTENT_TYPE, "application/json");
        if let Some(a) = auth {
            builder = builder.header(header::AUTHORIZATION, a);
        }
        let req = builder.body(Body::from(body.to_string())).unwrap();
        let resp = app.clone().oneshot(req).await.unwrap();
        let status = resp.status();
        let bytes = resp.into_body().collect().await.unwrap().to_bytes();
        (status, String::from_utf8_lossy(&bytes).to_string())
    }

    #[tokio::test]
    async fn mcp_requires_valid_bearer_token_and_enabled_flag() {
        let (_dir, app, _pool, config_service) = setup_test_env(false, "sj_mcp_test_secret").await;

        let init_req = json!({
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": { "protocolVersion": "2025-03-26" }
        });

        let (status, _) = post_mcp(&app, None, init_req.clone()).await;
        assert_eq!(status, StatusCode::UNAUTHORIZED);

        let (status, _) = post_mcp(&app, Some("Bearer wrong"), init_req.clone()).await;
        assert_eq!(status, StatusCode::UNAUTHORIZED);

        // Token 对但开关默认关闭 -> 403
        let (status, _) = post_mcp(&app, Some("Bearer sj_mcp_test_secret"), init_req.clone()).await;
        assert_eq!(status, StatusCode::FORBIDDEN);

        // 开启后 -> 200
        let mut cfg = config_service.get_or_load().await.unwrap().as_ref().clone();
        cfg.mcp.enabled = true;
        config_service.save(&cfg).await.unwrap();

        let (status, body) = post_mcp(&app, Some("Bearer sj_mcp_test_secret"), init_req).await;
        assert_eq!(status, StatusCode::OK);
        let parsed: Value = serde_json::from_str(&body).unwrap();
        assert_eq!(parsed["result"]["serverInfo"]["name"], "shijian-mcp");
    }

    #[tokio::test]
    async fn mcp_redlines_exclude_raw_writes_backup_and_webdav() {
        let (_dir, app, pool, config_service) = setup_test_env(true, "sj_mcp_test_secret").await;
        let auth = Some("Bearer sj_mcp_test_secret");

        // 1. tools/list 不得包含任何原始时长写删、备份恢复、WebDAV 工具
        let (status, body) = post_mcp(
            &app,
            auth,
            json!({ "jsonrpc": "2.0", "id": 1, "method": "tools/list" }),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        let parsed: Value = serde_json::from_str(&body).unwrap();
        let names: Vec<String> = parsed["result"]["tools"]
            .as_array()
            .unwrap()
            .iter()
            .map(|t| t["name"].as_str().unwrap().to_string())
            .collect();
        for forbidden in ["clear", "import", "rollback", "webdav", "duration"] {
            assert!(
                !names.iter().any(|n| n.to_lowercase().contains(forbidden)),
                "unexpected forbidden tool matching {forbidden}: {names:?}"
            );
        }

        // 2. get_settings 必须剥离 webDav 与 mcp.token
        let (_, body) = post_mcp(
            &app,
            auth,
            json!({
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": { "name": "get_settings", "arguments": {} }
            }),
        )
        .await;
        assert!(!body.contains("super_secret_pw"));
        assert!(!body.contains("sj_mcp_test_secret"));
        assert!(!body.contains("dav.example.com"));

        // 3. update_settings 即使恶意传入 webDav / mcp 也必须保留原值不动
        let (_, _) = post_mcp(
            &app,
            auth,
            json!({
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "update_settings",
                    "arguments": {
                        "general": { "language": 1 },
                        "add_ignore_processes": ["notepad"],
                        "webDav": { "password": "hacked" }
                    }
                }
            }),
        )
        .await;
        let updated_cfg = config_service.get_or_load().await.unwrap();
        assert_eq!(updated_cfg.general.language, 1);
        assert!(updated_cfg
            .behavior
            .ignore_process_list
            .contains(&"notepad".to_string()));
        assert_eq!(updated_cfg.webdav.password, "super_secret_pw");
        assert_eq!(updated_cfg.mcp.token, "sj_mcp_test_secret");

        // 4. assign_apps_category 改分类与别名时绝不修改原始 TotalTime
        sqlx::query("INSERT INTO AppModels (Name, TotalTime, CategoryID) VALUES ('Code', 3600, 1)")
            .execute(&pool)
            .await
            .unwrap();
        let app_id: i64 = sqlx::query_scalar("SELECT ID FROM AppModels WHERE Name = 'Code'")
            .fetch_one(&pool)
            .await
            .unwrap();

        let (status, resp_body) = post_mcp(
            &app,
            auth,
            json!({
                "jsonrpc": "2.0",
                "id": 4,
                "method": "tools/call",
                "params": {
                    "name": "assign_apps_category",
                    "arguments": {
                        "items": [{ "app_id": app_id, "alias": "VS Code" }]
                    }
                }
            }),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        let rpc_res: Value = serde_json::from_str(&resp_body).unwrap();
        assert_eq!(rpc_res["result"]["isError"], false);

        let (alias, total_time): (Option<String>, i64) =
            sqlx::query_as("SELECT Alias, TotalTime FROM AppModels WHERE ID = ?")
                .bind(app_id)
                .fetch_one(&pool)
                .await
                .unwrap();
        assert_eq!(alias.as_deref(), Some("VS Code"));
        assert_eq!(total_time, 3600);

        // 5. configure_plugin 支持保存 Lua 脚本
        let (_, plugin_body) = post_mcp(
            &app,
            auth,
            json!({
                "jsonrpc": "2.0",
                "id": 5,
                "method": "tools/call",
                "params": {
                    "name": "configure_plugin",
                    "arguments": {
                        "plugin_id": "reminder",
                        "action": "save_config",
                        "values": { "mode": "Lua 脚本", "script": "return true" }
                    }
                }
            }),
        )
        .await;
        let plugin_res: Value = serde_json::from_str(&plugin_body).unwrap();
        assert_eq!(plugin_res["result"]["isError"], false);
        assert!(plugin_body.contains("return true"));
    }
}
