use serde_json::{json, Value};
use std::io::{BufRead, Write};
use std::path::PathBuf;

use crate::constants;
use crate::models::config::ConfigModel;
use crate::parse_data_dir;

struct StdioConfig {
    endpoint: String,
    pinned_token: Option<String>,
    config_path: PathBuf,
}

fn parse_stdio_config(args: &[String]) -> StdioConfig {
    let mut url: Option<String> = None;
    let mut token: Option<String> = None;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--url" if i + 1 < args.len() => {
                url = Some(args[i + 1].clone());
                i += 2;
            }
            "--token" if i + 1 < args.len() => {
                token = Some(args[i + 1].clone());
                i += 2;
            }
            _ => i += 1,
        }
    }

    let base = url
        .or_else(|| std::env::var("SHIJIAN_SERVER").ok())
        .unwrap_or_else(|| format!("http://127.0.0.1:{}", constants::DEFAULT_HTTP_PORT));
    let base = base.trim_end_matches('/');
    let endpoint = if base.ends_with("/mcp") {
        base.to_string()
    } else {
        format!("{base}/mcp")
    };

    let pinned_token = token
        .or_else(|| std::env::var("SHIJIAN_MCP_TOKEN").ok())
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty());

    let data_dir = parse_data_dir(args);
    let config_path = data_dir.join("AppConfig.json");

    StdioConfig {
        endpoint,
        pinned_token,
        config_path,
    }
}

fn load_local_mcp_auth(cfg: &StdioConfig) -> Result<(bool, String), String> {
    if let Some(ref t) = cfg.pinned_token {
        return Ok((true, t.clone()));
    }
    let raw = std::fs::read_to_string(&cfg.config_path).map_err(|e| {
        format!(
            "无法读取时见配置文件 '{}'（{}）。请确认时见服务端已启动过，并在设置 -> AI 与 MCP 中启用 MCP。",
            cfg.config_path.display(),
            e
        )
    })?;
    let parsed: ConfigModel = serde_json::from_str(&raw)
        .map_err(|e| format!("解析 AppConfig.json 失败: {e}"))?;
    Ok((parsed.mcp.enabled, parsed.mcp.token))
}

fn jsonrpc_error(id: Option<Value>, code: i64, message: &str) -> Option<String> {
    let id = id?;
    Some(
        json!({
            "jsonrpc": "2.0",
            "id": id,
            "error": {
                "code": code,
                "message": message
            }
        })
        .to_string(),
    )
}

fn extract_id(line: &str) -> Option<Value> {
    serde_json::from_str::<Value>(line)
        .ok()
        .and_then(|v| v.get("id").cloned())
        .filter(|v| !v.is_null())
}

/// 极薄 STDIO MCP 桥接入口：将标准输入 JSON-RPC 行转发到运行中的 shijian-server `/mcp`。
pub fn run_stdio_bridge(args: &[String]) -> i32 {
    let cfg = parse_stdio_config(args);
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());

    for line_res in stdin.lock().lines() {
        let line = match line_res {
            Ok(l) => l,
            Err(_) => break,
        };
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }

        let req_id = extract_id(trimmed);

        let token = match load_local_mcp_auth(&cfg) {
            Ok((false, _)) => {
                if let Some(err) = jsonrpc_error(
                    req_id,
                    -32003,
                    "时见 MCP 当前处于关闭状态，请在「设置 -> AI 与 MCP」中开启后再试。",
                ) {
                    let _ = writeln!(out, "{err}");
                    let _ = out.flush();
                }
                continue;
            }
            Ok((true, t)) if t.trim().is_empty() => {
                if let Some(err) = jsonrpc_error(
                    req_id,
                    -32001,
                    "尚未生成 MCP Bearer Token，请在「设置 -> AI 与 MCP」中重新启用或生成 Token。",
                ) {
                    let _ = writeln!(out, "{err}");
                    let _ = out.flush();
                }
                continue;
            }
            Ok((true, t)) => t,
            Err(msg) => {
                if let Some(err) = jsonrpc_error(req_id, -32001, &msg) {
                    let _ = writeln!(out, "{err}");
                    let _ = out.flush();
                }
                continue;
            }
        };

        let resp = attohttpc::post(&cfg.endpoint)
            .header("Authorization", format!("Bearer {token}"))
            .header("Content-Type", "application/json")
            .header("Accept", "application/json")
            .text(trimmed)
            .send();

        match resp {
            Ok(res) => {
                let status = res.status().as_u16();
                let body = res.text().unwrap_or_default();
                if status == 202 || body.trim().is_empty() {
                    continue;
                }
                if status >= 400 && serde_json::from_str::<Value>(&body).is_err() {
                    if let Some(err) = jsonrpc_error(
                        req_id,
                        -32000,
                        &format!("ShiJian MCP HTTP {status}: {body}"),
                    ) {
                        let _ = writeln!(out, "{err}");
                        let _ = out.flush();
                    }
                    continue;
                }
                // 若 HTTP 返回 JSON-RPC（含 401/403 包装或正常 200），如果是有效 JSON 则单行输出
                let compact = match serde_json::from_str::<Value>(&body) {
                    Ok(v) if v.get("jsonrpc").is_some() => v.to_string(),
                    Ok(v) => {
                        let msg = v
                            .get("message")
                            .and_then(|m| m.as_str())
                            .unwrap_or(&body);
                        match jsonrpc_error(req_id, -32000, msg) {
                            Some(e) => e,
                            None => continue,
                        }
                    }
                    Err(_) => match jsonrpc_error(req_id, -32000, &body) {
                        Some(e) => e,
                        None => continue,
                    },
                };
                let _ = writeln!(out, "{compact}");
                let _ = out.flush();
            }
            Err(e) => {
                if let Some(err) = jsonrpc_error(
                    req_id,
                    -32002,
                    &format!(
                        "无法连接运行中的时见后台服务 ({})：{}。请先启动时见（ShiJian）。",
                        cfg.endpoint, e
                    ),
                ) {
                    let _ = writeln!(out, "{err}");
                    let _ = out.flush();
                }
            }
        }
    }

    0
}
