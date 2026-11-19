use chrono::{Local, NaiveDate, NaiveDateTime, NaiveTime};
use serde::Deserialize;
use serde_json::{json, Value};
use sqlx::SqlitePool;
use std::collections::HashSet;
use std::sync::Arc;
use tokio::sync::watch;

use crate::error::AppError;
use crate::models::app::AppModel;
use crate::models::config::ConfigModel;
use crate::models::request::{
    CreateCategoryRequest, UpdateAppRequest, UpdateCategoryRequest, UpdateSitesCategoryRequest,
};
use crate::models::web::{WebSiteCategoryModel, WebSiteModel};
use crate::plugins::{manifest, service as plugin_service, PluginState};
use crate::routes::config::{to_camel_case_keys, to_pascal_case_keys};
use crate::services::app_data::AppDataService;
use crate::services::category::CategoryService;
use crate::services::category_summary::CategorySummaryService;
use crate::services::config::ConfigService;
use crate::services::data::DataService;
use crate::services::entity::EntityService;
use crate::services::web_data::WebDataService;
use crate::utils::{host_timezone, parse_timezone};

#[derive(Clone)]
pub struct McpContext {
    pub pool: SqlitePool,
    pub config_service: Arc<ConfigService>,
    pub web_enabled_tx: watch::Sender<bool>,
    pub plugin_state: Arc<PluginState>,
}

pub async fn call_tool(ctx: &McpContext, name: &str, args: Value) -> Result<Value, String> {
    let args = if args.is_null() { json!({}) } else { args };
    match name {
        "get_usage_overview" => tool_get_usage_overview(ctx, args).await,
        "get_usage_timeline" => tool_get_usage_timeline(ctx, args).await,
        "get_category_breakdown" => tool_get_category_breakdown(ctx, args).await,
        "get_app_sessions" => tool_get_app_sessions(ctx, args).await,
        "get_hourly_logs" => tool_get_hourly_logs(ctx, args).await,
        "get_web_browse_logs" => tool_get_web_browse_logs(ctx, args).await,
        "list_apps" => tool_list_apps(ctx, args).await,
        "list_websites" => tool_list_websites(ctx, args).await,
        "list_categories" => tool_list_categories(ctx).await,
        "manage_category" => tool_manage_category(ctx, args).await,
        "assign_apps_category" => tool_assign_apps_category(ctx, args).await,
        "assign_sites_category" => tool_assign_sites_category(ctx, args).await,
        "run_auto_classify" => tool_run_auto_classify(ctx, args).await,
        "list_merge_groups" => tool_list_merge_groups(ctx).await,
        "manage_merge_group" => tool_manage_merge_group(ctx, args).await,
        "get_settings" => tool_get_settings(ctx).await,
        "update_settings" => tool_update_settings(ctx, args).await,
        "list_plugins" => tool_list_plugins(ctx, args).await,
        "configure_plugin" => tool_configure_plugin(ctx, args).await,
        _ => Err(format!("未知或不允许的 MCP 工具: {name}")),
    }
}

pub async fn read_resource(ctx: &McpContext, uri: &str) -> Result<Value, String> {
    match uri {
        "shijian://overview/today" => tool_get_usage_overview(ctx, json!({})).await,
        "shijian://categories" => tool_list_categories(ctx).await,
        "shijian://settings/public" => tool_get_settings(ctx).await,
        _ => Err(format!("未知资源 URI: {uri}")),
    }
}

fn resolve_tz(raw: Option<&str>) -> String {
    match raw.map(str::trim).filter(|s| !s.is_empty()) {
        Some(tz) => parse_timezone(tz).name().to_string(),
        None => host_timezone().name().to_string(),
    }
}

fn today_in_tz(tz_id: &str) -> NaiveDate {
    let tz = parse_timezone(tz_id);
    chrono::Utc::now().with_timezone(&tz).date_naive()
}

fn parse_date_range(
    start_raw: Option<&str>,
    end_raw: Option<&str>,
    tz_id: &str,
) -> Result<(NaiveDate, NaiveDate), String> {
    let today = today_in_tz(tz_id);
    let start = match start_raw.map(str::trim).filter(|s| !s.is_empty()) {
        Some(s) => NaiveDate::parse_from_str(s, "%Y-%m-%d")
            .map_err(|_| format!("start_date 格式无效，需要 YYYY-MM-DD: {s}"))?,
        None => today,
    };
    let end = match end_raw.map(str::trim).filter(|s| !s.is_empty()) {
        Some(s) => NaiveDate::parse_from_str(s, "%Y-%m-%d")
            .map_err(|_| format!("end_date 格式无效，需要 YYYY-MM-DD: {s}"))?,
        None => start,
    };
    if end < start {
        return Err("end_date 不能早于 start_date".to_string());
    }
    Ok((start, end))
}

fn date_start_dt(d: NaiveDate) -> NaiveDateTime {
    NaiveDateTime::new(d, NaiveTime::from_hms_opt(0, 0, 0).unwrap())
}

fn date_end_dt(d: NaiveDate) -> NaiveDateTime {
    NaiveDateTime::new(d, NaiveTime::from_hms_opt(23, 59, 59).unwrap())
}

fn map_err(e: AppError) -> String {
    e.to_string()
}

fn clamp_limit(v: Option<usize>, default: usize, max: usize) -> usize {
    v.unwrap_or(default).clamp(1, max)
}

async fn tool_get_usage_overview(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        timezone: Option<String>,
        #[serde(default)]
        browser_merged: bool,
        #[serde(default)]
        compare_previous: bool,
        top_limit: Option<usize>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let top_limit = clamp_limit(req.top_limit, 15, 100);

    let summary = EntityService::summary(
        &ctx.pool,
        &ctx.config_service,
        start,
        end,
        &tz,
        req.browser_merged,
    )
    .await
    .map_err(map_err)?;

    let list = EntityService::list(
        &ctx.pool,
        &ctx.config_service,
        start,
        end,
        &tz,
        req.browser_merged,
        Some(top_limit),
    )
    .await
    .map_err(map_err)?;

    let categories = DataService::get_category_range_data(
        &ctx.pool,
        start,
        end,
        &tz,
        &ctx.config_service,
    )
    .await
    .map_err(map_err)?;

    let category_meta = CategoryService::get_categories(&ctx.pool)
        .await
        .map_err(map_err)?;
    let cat_breakdown: Vec<Value> = categories
        .into_iter()
        .filter_map(|col| {
            let cid = col.category_id?;
            let secs: f64 = col.values.into_iter().sum();
            let name = category_meta
                .iter()
                .find(|c| c.id == cid)
                .and_then(|c| c.name.clone())
                .unwrap_or_else(|| format!("Category#{cid}"));
            Some(json!({
                "categoryId": cid,
                "name": name,
                "seconds": secs.round() as i64
            }))
        })
        .collect();

    let previous_summary = if req.compare_previous {
        let span_days = (end - start).num_days() + 1;
        let prev_end = start - chrono::Duration::days(1);
        let prev_start = prev_end - chrono::Duration::days(span_days - 1);
        let prev = EntityService::summary(
            &ctx.pool,
            &ctx.config_service,
            prev_start,
            prev_end,
            &tz,
            req.browser_merged,
        )
        .await
        .map_err(map_err)?;
        Some(json!({
            "startDate": prev_start.to_string(),
            "endDate": prev_end.to_string(),
            "summary": prev
        }))
    } else {
        None
    };

    Ok(json!({
        "readOnly": true,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "browserMerged": req.browser_merged,
        "summary": summary,
        "topEntities": list.rows,
        "appCategoryBreakdown": cat_breakdown,
        "previousPeriod": previous_summary,
    }))
}

async fn tool_get_usage_timeline(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        buckets: Option<String>,
        #[serde(default)]
        browser_merged: bool,
        top_entities: Option<usize>,
        timezone: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let buckets = match req.buckets.as_deref() {
        Some(b @ ("hour" | "day" | "month")) => b.to_string(),
        Some(other) => return Err(format!("无效 buckets 值 '{other}'，仅支持 hour/day/month")),
        None => {
            if start == end {
                "hour".to_string()
            } else {
                "day".to_string()
            }
        }
    };
    let top_entities = clamp_limit(req.top_entities, 15, 50);

    let mut hours = EntityService::hours(
        &ctx.pool,
        &ctx.config_service,
        start,
        end,
        &tz,
        &buckets,
        req.browser_merged,
    )
    .await
    .map_err(map_err)?;

    hours.rows.sort_by(|a, b| {
        let sum_a: i64 = a.values.iter().sum();
        let sum_b: i64 = b.values.iter().sum();
        sum_b.cmp(&sum_a)
    });
    hours.rows.truncate(top_entities);

    Ok(json!({
        "readOnly": true,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "buckets": buckets,
        "timeline": hours
    }))
}

async fn tool_get_category_breakdown(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        scope: Option<String>,
        category_id: Option<i64>,
        #[serde(default)]
        compare_previous: bool,
        timezone: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let scope = req.scope.as_deref().unwrap_or("unified");
    if !matches!(scope, "unified" | "app" | "web") {
        return Err(format!("无效 scope '{scope}'，仅支持 unified/app/web"));
    }

    if let Some(cid) = req.category_id {
        let (prev_start, prev_end) = if req.compare_previous {
            let span_days = (end - start).num_days() + 1;
            let pe = start - chrono::Duration::days(1);
            let ps = pe - chrono::Duration::days(span_days - 1);
            (Some(ps), Some(pe))
        } else {
            (None, None)
        };

        let summary = match scope {
            "app" => {
                CategorySummaryService::get_app_category_summary(
                    &ctx.pool,
                    cid,
                    start,
                    end,
                    prev_start,
                    prev_end,
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
            "web" => {
                CategorySummaryService::get_web_category_summary(
                    &ctx.pool,
                    cid,
                    start,
                    end,
                    prev_start,
                    prev_end,
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
            _ => {
                CategorySummaryService::get_unified_category_summary(
                    &ctx.pool,
                    cid,
                    start,
                    end,
                    prev_start,
                    prev_end,
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
        }
        .map_err(map_err)?;

        let members = match scope {
            "app" => {
                CategorySummaryService::get_app_category_members(
                    &ctx.pool,
                    cid,
                    date_start_dt(start),
                    date_end_dt(end),
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
            "web" => {
                CategorySummaryService::get_web_category_members(
                    &ctx.pool,
                    cid,
                    date_start_dt(start),
                    date_end_dt(end),
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
            _ => {
                CategorySummaryService::get_unified_category_members(
                    &ctx.pool,
                    cid,
                    date_start_dt(start),
                    date_end_dt(end),
                    &tz,
                    &ctx.config_service,
                )
                .await
            }
        }
        .map_err(map_err)?;

        return Ok(json!({
            "readOnly": true,
            "scope": scope,
            "timezone": tz,
            "summary": summary,
            "members": members
        }));
    }

    let list = EntityService::list(
        &ctx.pool,
        &ctx.config_service,
        start,
        end,
        &tz,
        scope == "app",
        None,
    )
    .await
    .map_err(map_err)?;

    let mut by_cat: std::collections::HashMap<(String, i64), (String, i64, usize)> =
        std::collections::HashMap::new();
    for row in &list.rows {
        if scope == "app" && row.kind == "site" {
            continue;
        }
        if scope == "web" && row.kind != "site" {
            continue;
        }
        let key = (row.category_kind.clone(), row.category_id);
        let entry = by_cat.entry(key).or_insert_with(|| {
            (
                row.category_name
                    .clone()
                    .unwrap_or_else(|| "未分类".to_string()),
                0,
                0,
            )
        });
        entry.1 += row.seconds;
        entry.2 += 1;
    }

    let mut items: Vec<Value> = by_cat
        .into_iter()
        .map(|((kind, id), (name, seconds, entity_count))| {
            json!({
                "categoryId": id,
                "categoryKind": kind,
                "name": name,
                "seconds": seconds,
                "entityCount": entity_count
            })
        })
        .collect();
    items.sort_by(|a, b| {
        b["seconds"]
            .as_i64()
            .unwrap_or(0)
            .cmp(&a["seconds"].as_i64().unwrap_or(0))
    });

    Ok(json!({
        "readOnly": true,
        "scope": scope,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "categories": items
    }))
}

async fn tool_get_app_sessions(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        app_id: Option<i64>,
        app_name: Option<String>,
        min_duration_seconds: Option<i64>,
        limit: Option<usize>,
        offset: Option<usize>,
        timezone: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let limit = clamp_limit(req.limit, 100, 500);
    let offset = req.offset.unwrap_or(0);
    let needle = req.app_name.as_deref().map(|s| s.trim().to_lowercase());

    let sessions = DataService::get_app_sessions(
        &ctx.pool,
        date_start_dt(start),
        date_end_dt(end),
        &tz,
        &ctx.config_service,
    )
    .await
    .map_err(map_err)?;

    let filtered: Vec<_> = sessions
        .into_iter()
        .filter(|s| req.app_id.map_or(true, |id| s.app_model_id == id))
        .filter(|s| req.min_duration_seconds.map_or(true, |min| s.duration >= min))
        .filter(|s| match &needle {
            None => true,
            Some(q) if q.is_empty() => true,
            Some(q) => s.app_model.as_ref().map_or(false, |app| {
                app.name.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || app.alias.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || app.description.as_deref().unwrap_or("").to_lowercase().contains(q)
            }),
        })
        .collect();

    let total = filtered.len();
    let page: Vec<_> = filtered.into_iter().skip(offset).take(limit).collect();

    Ok(json!({
        "readOnly": true,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "total": total,
        "offset": offset,
        "limit": limit,
        "items": page
    }))
}

async fn tool_get_hourly_logs(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        app_id: Option<i64>,
        limit: Option<usize>,
        offset: Option<usize>,
        timezone: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let limit = clamp_limit(req.limit, 100, 500);
    let offset = req.offset.unwrap_or(0);

    let logs = DataService::get_hours_range_log_list(
        &ctx.pool,
        date_start_dt(start),
        date_end_dt(end),
        &tz,
        &ctx.config_service,
    )
    .await
    .map_err(map_err)?;

    let filtered: Vec<_> = logs
        .into_iter()
        .filter(|row| req.app_id.map_or(true, |id| row.app_model_id == id))
        .collect();

    let total = filtered.len();
    let page: Vec<_> = filtered.into_iter().skip(offset).take(limit).collect();

    Ok(json!({
        "readOnly": true,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "total": total,
        "offset": offset,
        "limit": limit,
        "items": page
    }))
}

async fn tool_get_web_browse_logs(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        start_date: Option<String>,
        end_date: Option<String>,
        site_id: Option<i64>,
        domain: Option<String>,
        limit: Option<usize>,
        offset: Option<usize>,
        timezone: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let tz = resolve_tz(req.timezone.as_deref());
    let (start, end) = parse_date_range(req.start_date.as_deref(), req.end_date.as_deref(), &tz)?;
    let limit = clamp_limit(req.limit, 100, 500);
    let offset = req.offset.unwrap_or(0);
    let domain_needle = req.domain.as_deref().map(|s| s.trim().to_lowercase());

    let logs = WebDataService::get_browse_log_list(
        &ctx.pool,
        date_start_dt(start),
        date_end_dt(end),
        req.site_id.unwrap_or(0),
        &tz,
        &ctx.config_service,
    )
    .await
    .map_err(map_err)?;

    let filtered: Vec<_> = logs
        .into_iter()
        .filter(|row| match &domain_needle {
            None => true,
            Some(q) if q.is_empty() => true,
            Some(q) => row
                .site
                .as_ref()
                .and_then(|s| s.domain.as_deref())
                .unwrap_or("")
                .to_lowercase()
                .contains(q),
        })
        .collect();

    let total = filtered.len();
    let page: Vec<_> = filtered.into_iter().skip(offset).take(limit).collect();

    Ok(json!({
        "readOnly": true,
        "timezone": tz,
        "startDate": start.to_string(),
        "endDate": end.to_string(),
        "total": total,
        "offset": offset,
        "limit": limit,
        "items": page
    }))
}

async fn tool_list_apps(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        category_id: Option<i64>,
        #[serde(default)]
        only_uncategorized: bool,
        search: Option<String>,
        limit: Option<usize>,
        offset: Option<usize>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let limit = clamp_limit(req.limit, 100, 500);
    let offset = req.offset.unwrap_or(0);
    let needle = req.search.as_deref().map(|s| s.trim().to_lowercase());

    let sys_cat_id = if req.only_uncategorized {
        Some(
            CategoryService::get_system_category_id(&ctx.pool)
                .await
                .map_err(map_err)?,
        )
    } else {
        None
    };

    let mut apps = AppDataService::get_all_apps(&ctx.pool)
        .await
        .map_err(map_err)?;
    apps.sort_by(|a, b| b.total_time.cmp(&a.total_time));

    let filtered: Vec<AppModel> = apps
        .into_iter()
        .filter(|a| req.category_id.map_or(true, |cid| a.category_id == cid))
        .filter(|a| sys_cat_id.map_or(true, |sid| a.category_id == sid || a.category_id == 0))
        .filter(|a| match &needle {
            None => true,
            Some(q) if q.is_empty() => true,
            Some(q) => {
                a.name.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || a.alias.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || a.description.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || a.file.as_deref().unwrap_or("").to_lowercase().contains(q)
            }
        })
        .collect();

    let total = filtered.len();
    let page: Vec<_> = filtered.into_iter().skip(offset).take(limit).collect();

    Ok(json!({
        "readOnly": true,
        "total": total,
        "offset": offset,
        "limit": limit,
        "items": page
    }))
}

async fn tool_list_websites(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        category_id: Option<i64>,
        #[serde(default)]
        only_uncategorized: bool,
        search: Option<String>,
        limit: Option<usize>,
        offset: Option<usize>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let limit = clamp_limit(req.limit, 100, 500);
    let offset = req.offset.unwrap_or(0);
    let needle = req.search.as_deref().map(|s| s.trim().to_lowercase());

    let mut sites = if req.only_uncategorized {
        WebDataService::get_unset_category_web_sites(&ctx.pool, &ctx.config_service)
            .await
            .map_err(map_err)?
    } else {
        WebDataService::get_web_sites(&ctx.pool, req.category_id, &ctx.config_service)
            .await
            .map_err(map_err)?
    };
    sites.sort_by(|a, b| b.duration.cmp(&a.duration));

    let filtered: Vec<WebSiteModel> = sites
        .into_iter()
        .filter(|s| match &needle {
            None => true,
            Some(q) if q.is_empty() => true,
            Some(q) => {
                s.domain.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || s.title.as_deref().unwrap_or("").to_lowercase().contains(q)
                    || s.alias.as_deref().unwrap_or("").to_lowercase().contains(q)
            }
        })
        .collect();

    let total = filtered.len();
    let page: Vec<_> = filtered.into_iter().skip(offset).take(limit).collect();

    Ok(json!({
        "readOnly": true,
        "total": total,
        "offset": offset,
        "limit": limit,
        "items": page
    }))
}

async fn tool_list_categories(ctx: &McpContext) -> Result<Value, String> {
    let app_categories = CategoryService::get_categories(&ctx.pool)
        .await
        .map_err(map_err)?;
    let web_categories = WebDataService::get_web_site_categories(&ctx.pool)
        .await
        .map_err(map_err)?;
    Ok(json!({
        "appCategories": app_categories,
        "webCategories": web_categories
    }))
}

async fn tool_manage_category(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Args {
        scope: Option<String>,
        action: String,
        id: Option<i64>,
        name: Option<String>,
        color: Option<String>,
        icon_file: Option<String>,
        is_directory_match: Option<bool>,
        directories: Option<String>,
        is_url_match: Option<bool>,
        url_patterns: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let scope = req.scope.as_deref().unwrap_or("app");
    if !matches!(scope, "app" | "web") {
        return Err(format!("无效 scope '{scope}'，仅支持 app/web"));
    }

    match req.action.as_str() {
        "create" => {
            let name = req
                .name
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .ok_or_else(|| "创建分类时 name 不能为空".to_string())?
                .to_string();
            if scope == "web" {
                let created = WebDataService::create_web_site_category(
                    &ctx.pool,
                    WebSiteCategoryModel {
                        id: 0,
                        name,
                        icon_file: req.icon_file,
                        color: Some(req.color.unwrap_or_else(|| "#5E81AC".to_string())),
                        is_url_match: req.is_url_match.unwrap_or(false),
                        url_patterns: req.url_patterns,
                        is_system: false,
                    },
                )
                .await
                .map_err(map_err)?;
                Ok(json!({ "scope": "web", "category": created }))
            } else {
                let created = CategoryService::create_category(
                    &ctx.pool,
                    CreateCategoryRequest {
                        name,
                        icon_file: req.icon_file,
                        color: req.color.or_else(|| Some("#5E81AC".to_string())),
                        is_directory_match: req.is_directory_match.unwrap_or(false),
                        directories: req.directories,
                        is_url_match: req.is_url_match.unwrap_or(false),
                        url_patterns: req.url_patterns,
                    },
                )
                .await
                .map_err(map_err)?;
                Ok(json!({ "scope": "app", "category": created }))
            }
        }
        "update" => {
            let id = req.id.ok_or_else(|| "update 操作需要 id".to_string())?;
            if scope == "web" {
                let existing = WebDataService::get_web_site_categories(&ctx.pool)
                    .await
                    .map_err(map_err)?
                    .into_iter()
                    .find(|c| c.id == id)
                    .ok_or_else(|| format!("分类 {id} 不存在"))?;
                WebDataService::update_web_site_category(
                    &ctx.pool,
                    id,
                    WebSiteCategoryModel {
                        id,
                        name: req.name.unwrap_or(existing.name),
                        icon_file: req.icon_file.or(existing.icon_file),
                        color: req.color.or(existing.color),
                        is_url_match: req.is_url_match.unwrap_or(existing.is_url_match),
                        url_patterns: req.url_patterns.or(existing.url_patterns),
                        is_system: existing.is_system,
                    },
                )
                .await
                .map_err(map_err)?;
            } else {
                let existing = CategoryService::get_category(&ctx.pool, id)
                    .await
                    .map_err(map_err)?
                    .ok_or_else(|| format!("分类 {id} 不存在"))?;
                CategoryService::update_category(
                    &ctx.pool,
                    id,
                    UpdateCategoryRequest {
                        id,
                        name: req
                            .name
                            .or(existing.name)
                            .unwrap_or_else(|| format!("Category#{id}")),
                        icon_file: req.icon_file.or(existing.icon_file),
                        color: req.color.or(existing.color),
                        is_directory_match: req
                            .is_directory_match
                            .unwrap_or(existing.is_directory_match),
                        directories: req.directories.or(existing.directories),
                        is_url_match: req.is_url_match.unwrap_or(existing.is_url_match),
                        url_patterns: req.url_patterns.or(existing.url_patterns),
                    },
                )
                .await
                .map_err(map_err)?;
            }
            Ok(json!({ "ok": true, "id": id, "scope": scope }))
        }
        "delete" => {
            let id = req.id.ok_or_else(|| "delete 操作需要 id".to_string())?;
            if scope == "web" {
                WebDataService::delete_web_site_category(&ctx.pool, id)
                    .await
                    .map_err(map_err)?;
            } else {
                CategoryService::delete_category(&ctx.pool, id)
                    .await
                    .map_err(map_err)?;
            }
            Ok(json!({ "ok": true, "deletedId": id }))
        }
        "restore" => {
            let id = req.id.ok_or_else(|| "restore 操作需要 id".to_string())?;
            let restored = CategoryService::restore_system_category(&ctx.pool, id)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true, "category": restored }))
        }
        other => Err(format!("不支持的 action '{other}'")),
    }
}

async fn tool_assign_apps_category(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Item {
        app_id: i64,
        category_id: Option<i64>,
        alias: Option<Option<String>>,
        description: Option<Option<String>>,
    }
    #[derive(Deserialize)]
    struct Args {
        items: Vec<Item>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    if req.items.is_empty() {
        return Err("items 不能为空".to_string());
    }

    let mut updated = 0usize;
    for item in req.items {
        let existing = AppDataService::get_app(&ctx.pool, item.app_id)
            .await
            .map_err(map_err)?
            .ok_or_else(|| format!("应用 ID {} 不存在", item.app_id))?;

        let update_req = UpdateAppRequest {
            id: existing.id,
            name: existing.name.unwrap_or_default(),
            alias: match item.alias {
                Some(v) => v,
                None => existing.alias,
            },
            description: match item.description {
                Some(v) => v,
                None => existing.description,
            },
            file: existing.file,
            icon_file: existing.icon_file,
            category_id: item.category_id.unwrap_or(existing.category_id),
            // 绝不修改原始 TotalTime；AppDataService::update_app SQL 本身也不改 TotalTime
            total_time: existing.total_time,
        };
        AppDataService::update_app(&ctx.pool, existing.id, update_req)
            .await
            .map_err(map_err)?;
        updated += 1;
    }

    Ok(json!({ "ok": true, "updatedCount": updated }))
}

async fn tool_assign_sites_category(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Item {
        site_id: i64,
        category_id: Option<i64>,
        alias: Option<Option<String>>,
        title: Option<Option<String>>,
    }
    #[derive(Deserialize)]
    struct Args {
        items: Vec<Item>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    if req.items.is_empty() {
        return Err("items 不能为空".to_string());
    }

    let mut updated = 0usize;
    for item in req.items {
        let existing = WebDataService::get_web_site(&ctx.pool, item.site_id)
            .await
            .map_err(map_err)?
            .ok_or_else(|| format!("网站 ID {} 不存在", item.site_id))?;

        if let Some(cid) = item.category_id {
            WebDataService::update_web_sites_category(
                &ctx.pool,
                UpdateSitesCategoryRequest {
                    site_ids: vec![item.site_id],
                    category_id: cid,
                },
            )
            .await
            .map_err(map_err)?;
        }

        if item.alias.is_some() || item.title.is_some() {
            let mut next = existing;
            if let Some(a) = item.alias {
                next.alias = a;
            }
            if let Some(t) = item.title {
                next.title = t;
            }
            WebDataService::update_web_site(&ctx.pool, item.site_id, next)
                .await
                .map_err(map_err)?;
        }

        updated += 1;
    }

    Ok(json!({ "ok": true, "updatedCount": updated }))
}

async fn tool_run_auto_classify(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Args {
        mode: String,
        patterns: Option<Vec<String>>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let count = match req.mode.as_str() {
        "app_auto" => CategoryService::apply_auto_classify(&ctx.pool)
            .await
            .map_err(map_err)?,
        "directory_match" => CategoryService::apply_directory_match(&ctx.pool, req.patterns)
            .await
            .map_err(map_err)?,
        "url_match" => WebDataService::apply_url_match(&ctx.pool, req.patterns)
            .await
            .map_err(map_err)?,
        other => return Err(format!("无效 mode '{other}'")),
    };
    Ok(json!({ "ok": true, "mode": req.mode, "classifiedCount": count }))
}

async fn tool_list_merge_groups(ctx: &McpContext) -> Result<Value, String> {
    let data = EntityService::merge_groups(&ctx.pool)
        .await
        .map_err(map_err)?;
    Ok(serde_json::to_value(data).map_err(|e| e.to_string())?)
}

async fn tool_manage_merge_group(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Args {
        action: String,
        group_id: Option<i64>,
        name: Option<String>,
        primary_ref: Option<String>,
        members: Option<Vec<String>>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    match req.action.as_str() {
        "create" => {
            let members = req
                .members
                .ok_or_else(|| "create 操作需要 members 列表".to_string())?;
            let created = EntityService::create_group(
                &ctx.pool,
                req.name.as_deref().unwrap_or(""),
                req.primary_ref.as_deref().unwrap_or(""),
                &members,
            )
            .await
            .map_err(map_err)?;
            Ok(json!({ "ok": true, "group": created }))
        }
        "update" => {
            let id = req.group_id.ok_or_else(|| "update 操作需要 group_id".to_string())?;
            let updated = EntityService::update_group(
                &ctx.pool,
                id,
                req.name.as_deref(),
                req.primary_ref.as_deref(),
                req.members.as_deref(),
            )
            .await
            .map_err(map_err)?;
            Ok(json!({ "ok": true, "group": updated }))
        }
        "rename" => {
            let id = req.group_id.ok_or_else(|| "rename 操作需要 group_id".to_string())?;
            let name = req
                .name
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .ok_or_else(|| "rename 操作需要非空 name".to_string())?;
            EntityService::rename_group(&ctx.pool, id, name)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true, "groupId": id, "name": name }))
        }
        "dissolve" => {
            let id = req.group_id.ok_or_else(|| "dissolve 操作需要 group_id".to_string())?;
            EntityService::dissolve_group(&ctx.pool, id)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true, "dissolvedGroupId": id }))
        }
        "restore" => {
            EntityService::restore_suppressed(&ctx.pool)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true }))
        }
        other => Err(format!("不支持的 action '{other}'")),
    }
}

fn sanitize_config_for_mcp(config: &ConfigModel) -> Result<Value, String> {
    let mut val = serde_json::to_value(config).map_err(|e| e.to_string())?;
    if let Value::Object(ref mut map) = val {
        map.remove("WebDav");
        map.remove("Mcp");
        map.insert("McpEnabled".to_string(), Value::Bool(config.mcp.enabled));
    }
    to_camel_case_keys(&mut val);
    Ok(val)
}

async fn tool_get_settings(ctx: &McpContext) -> Result<Value, String> {
    let config = ctx.config_service.get_or_load().await.map_err(map_err)?;
    sanitize_config_for_mcp(&config)
}

fn merge_json_object(dst: &mut Value, patch: Value) {
    if let (Value::Object(dst_map), Value::Object(patch_map)) = (dst, patch) {
        for (k, v) in patch_map {
            match dst_map.get_mut(&k) {
                Some(existing) if existing.is_object() && v.is_object() => {
                    merge_json_object(existing, v);
                }
                _ => {
                    dst_map.insert(k, v);
                }
            }
        }
    }
}

fn apply_string_list_delta(list: &mut Vec<String>, add: &[String], remove: &[String]) {
    if !remove.is_empty() {
        let rm_set: HashSet<&str> = remove.iter().map(|s| s.trim()).collect();
        list.retain(|item| !rm_set.contains(item.trim()));
    }
    for item in add {
        let trimmed = item.trim();
        if !trimmed.is_empty() && !list.iter().any(|existing| existing == trimmed) {
            list.push(trimmed.to_string());
        }
    }
}

async fn tool_update_settings(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        general: Option<Value>,
        behavior: Option<Value>,
        shortcut: Option<Value>,
        #[serde(default)]
        add_ignore_processes: Vec<String>,
        #[serde(default)]
        remove_ignore_processes: Vec<String>,
        #[serde(default)]
        add_ignore_urls: Vec<String>,
        #[serde(default)]
        remove_ignore_urls: Vec<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;

    let current = ctx.config_service.get_or_load().await.map_err(map_err)?;
    let old_web_enabled = current.general.is_web_enabled;
    // 保留 WebDav 与 Mcp 不被任何形式篡改
    let preserved_webdav = current.webdav.clone();
    let preserved_mcp = current.mcp.clone();

    let mut current_val = serde_json::to_value(current.as_ref()).map_err(|e| e.to_string())?;
    let mut patch_root = serde_json::Map::new();
    if let Some(mut g) = req.general {
        to_pascal_case_keys(&mut g);
        patch_root.insert("General".to_string(), g);
    }
    if let Some(mut b) = req.behavior {
        to_pascal_case_keys(&mut b);
        patch_root.insert("Behavior".to_string(), b);
    }
    if let Some(mut s) = req.shortcut {
        to_pascal_case_keys(&mut s);
        patch_root.insert("Shortcut".to_string(), s);
    }
    merge_json_object(&mut current_val, Value::Object(patch_root));

    let mut next: ConfigModel = serde_json::from_value(current_val)
        .map_err(|e| format!("配置合并后反序列化失败: {e}"))?;
    next.webdav = preserved_webdav;
    next.mcp = preserved_mcp;

    apply_string_list_delta(
        &mut next.behavior.ignore_process_list,
        &req.add_ignore_processes,
        &req.remove_ignore_processes,
    );
    apply_string_list_delta(
        &mut next.behavior.ignore_url_list,
        &req.add_ignore_urls,
        &req.remove_ignore_urls,
    );

    let new_web_enabled = next.general.is_web_enabled;
    ctx.config_service.save(&next).await.map_err(map_err)?;

    if old_web_enabled != new_web_enabled {
        let _ = ctx.web_enabled_tx.send(new_web_enabled);
    }

    sanitize_config_for_mcp(&next)
}

async fn tool_list_plugins(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize, Default)]
    struct Args {
        plugin_id: Option<String>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    ctx.plugin_state.rescan().await;
    let manifests = ctx.plugin_state.list_manifests().await;

    if let Some(id) = req.plugin_id.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
        let m = manifests
            .into_iter()
            .find(|m| m.id == id)
            .ok_or_else(|| format!("插件 '{id}' 不存在"))?;
        let enabled = plugin_service::is_enabled(&ctx.plugin_state, id).await;
        let config = plugin_service::get_config(&ctx.plugin_state, id).await;
        let status = plugin_service::get_status(&ctx.plugin_state, id).await;
        let records = plugin_service::get_records(&ctx.pool, id, None)
            .await
            .unwrap_or_default();
        return Ok(json!({
            "id": m.id,
            "name": m.name,
            "version": m.version,
            "enabled": enabled,
            "configSchema": m.config_schema,
            "config": config,
            "status": status,
            "records": records
        }));
    }

    let mut items = Vec::with_capacity(manifests.len());
    for m in manifests {
        let enabled = plugin_service::is_enabled(&ctx.plugin_state, &m.id).await;
        let config = plugin_service::get_config(&ctx.plugin_state, &m.id).await;
        let status = plugin_service::get_status(&ctx.plugin_state, &m.id).await;
        items.push(json!({
            "id": m.id,
            "name": m.name,
            "version": m.version,
            "enabled": enabled,
            "configSchema": m.config_schema,
            "config": config,
            "status": status
        }));
    }
    Ok(json!({ "plugins": items }))
}

async fn tool_configure_plugin(ctx: &McpContext, args: Value) -> Result<Value, String> {
    #[derive(Deserialize)]
    struct Args {
        plugin_id: String,
        action: String,
        enabled: Option<bool>,
        values: Option<Value>,
        merge_config: Option<bool>,
        record_key: Option<String>,
        record_value: Option<Value>,
        cmd: Option<String>,
        cmd_data: Option<Value>,
    }
    let req: Args = serde_json::from_value(args).map_err(|e| format!("参数解析失败: {e}"))?;
    let id = req.plugin_id.trim();
    let Some(m) = ctx.plugin_state.manifest_or_rescan(id).await else {
        return Err(format!("插件 '{id}' 不存在"));
    };

    match req.action.as_str() {
        "set_enabled" => {
            let enabled = req
                .enabled
                .ok_or_else(|| "set_enabled 操作需要 enabled".to_string())?;
            if enabled {
                let Some(dir) = ctx.plugin_state.plugin_dir_or_rescan(id).await else {
                    return Err(format!("插件 '{id}' 目录不存在"));
                };
                if manifest::resolve_entry(&dir, &m.entry).is_none() {
                    return Err(format!("插件 '{id}' 入口文件 '{}' 不存在", m.entry));
                }
            }
            plugin_service::set_enabled(&ctx.plugin_state, id, enabled)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true, "pluginId": id, "enabled": enabled }))
        }
        "save_config" => {
            let values = req
                .values
                .ok_or_else(|| "save_config 操作需要 values 对象".to_string())?;
            if !values.is_object() {
                return Err("values 必须是 JSON 对象".to_string());
            }
            let next_values = if req.merge_config.unwrap_or(true) {
                let current = plugin_service::get_config(&ctx.plugin_state, id).await;
                let mut merged = if current.values.is_object() {
                    current.values
                } else {
                    json!({})
                };
                merge_json_object(&mut merged, values);
                merged
            } else {
                values
            };
            let version = plugin_service::save_config(&ctx.plugin_state, id, next_values)
                .await
                .map_err(map_err)?;
            let saved = plugin_service::get_config(&ctx.plugin_state, id).await;
            Ok(json!({
                "ok": true,
                "pluginId": id,
                "configVersion": version,
                "config": saved
            }))
        }
        "upsert_record" => {
            let key = req
                .record_key
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty() && s.len() <= 128)
                .ok_or_else(|| "upsert_record 需要合法的 record_key（1~128 字符）".to_string())?;
            let val = req
                .record_value
                .ok_or_else(|| "upsert_record 需要 record_value".to_string())?;
            plugin_service::upsert_record(&ctx.pool, id, key, &val)
                .await
                .map_err(map_err)?;
            Ok(json!({ "ok": true, "pluginId": id, "key": key }))
        }
        "push_command" => {
            if !plugin_service::is_enabled(&ctx.plugin_state, id).await {
                return Err(format!("插件 '{id}' 当前未启用，无法下发指令"));
            }
            let cmd = req
                .cmd
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty() && s.len() <= 64)
                .ok_or_else(|| "push_command 需要合法的 cmd（1~64 字符）".to_string())?
                .to_string();
            let command = plugin_service::push_command(&ctx.plugin_state, id, cmd, req.cmd_data);
            Ok(json!({ "ok": true, "command": command }))
        }
        other => Err(format!("不支持的 action '{other}'")),
    }
}

#[allow(dead_code)]
fn _unused_local_anchor() -> NaiveDate {
    Local::now().date_naive()
}
