use chrono::{DateTime, NaiveDate, NaiveDateTime, Timelike, Utc};
use chrono_tz::Tz;
use sqlx::SqlitePool;
use std::collections::{HashMap, HashSet};
use tracing::debug;

use crate::error::AppError;
use crate::models::category_summary::{
    CategoryMemberModel, CategorySummaryModel, DailyPointModel,
};
use crate::services::config::ConfigService;
use crate::services::entity;
use crate::utils::{parse_timezone, tz_date_to_utc_range, tz_naive_to_utc};

pub struct CategorySummaryService;

impl CategorySummaryService {
    pub async fn get_app_category_summary(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDate,
        end: NaiveDate,
        prev_start: Option<NaiveDate>,
        prev_end: Option<NaiveDate>,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<CategorySummaryModel, AppError> {
        debug!(
            "get_app_category_summary: cat={} start={} end={}",
            category_id, start, end
        );

        let category_name: Option<String> =
            sqlx::query_scalar("SELECT Name FROM CategoryModels WHERE ID = ?")
                .bind(category_id)
                .fetch_optional(pool)
                .await?;
        let category_name = category_name.unwrap_or_else(|| String::from("?"));

        let tz = parse_timezone(tz_id);
        let utc_start = tz_date_to_utc_range(start, &tz).0;
        let utc_end = tz_date_to_utc_range(end, &tz).1;

        let excluded = config_service.get_excluded_app_id_set(pool).await;
        let excluded_vec: Vec<i64> = excluded.into_iter().collect();
        let has_excluded = !excluded_vec.is_empty();

        // 一次 SQL 拉取后在内存聚合
        let rows: Vec<(DateTime<Utc>, i64)> = if !has_excluded {
            sqlx::query_as(
                r#"
                SELECT h.DataTime, h.Time
                FROM HoursLogModels h
                JOIN AppModels a ON h.AppModelID = a.ID
                WHERE a.CategoryID = ?
                  AND h.DataTime >= ? AND h.DataTime < ?
                  AND h.AppModelID != 0
                "#,
            )
            .bind(category_id)
            .bind(utc_start)
            .bind(utc_end)
            .fetch_all(pool)
            .await?
        } else {
            let placeholders = excluded_vec
                .iter()
                .map(|_| "?")
                .collect::<Vec<_>>()
                .join(",");
            let sql = format!(
                r#"
                SELECT h.DataTime, h.Time
                FROM HoursLogModels h
                JOIN AppModels a ON h.AppModelID = a.ID
                WHERE a.CategoryID = ?
                  AND h.DataTime >= ? AND h.DataTime < ?
                  AND h.AppModelID != 0
                  AND h.AppModelID NOT IN ({})
                "#,
                placeholders
            );
            let mut q = sqlx::query_as::<_, (DateTime<Utc>, i64)>(&sql)
                .bind(category_id)
                .bind(utc_start)
                .bind(utc_end);
            for id in &excluded_vec {
                q = q.bind(*id);
            }
            q.fetch_all(pool).await?
        };

        // 环比总时长，纯 SUM 标量查询
        let previous_total_seconds = match (prev_start, prev_end) {
            (Some(ps), Some(pe)) if ps <= pe => {
                sum_app_range_total(pool, category_id, ps, pe, &tz, has_excluded, &excluded_vec)
                    .await
            }
            _ => 0,
        };

        // 按本地日期分桶
        let mut daily: HashMap<NaiveDate, i64> = HashMap::new();
        // 按本地时区小时分桶
        let mut hourly: [i64; 24] = [0; 24];
        let mut total_seconds: i64 = 0;

        for (utc_dt, secs) in &rows {
            let local = utc_dt.with_timezone(&tz);
            let local_date = local.date_naive();
            if local_date < start || local_date > end {
                continue;
            }
            *daily.entry(local_date).or_insert(0) += secs;
            let h = local.time().hour() as usize;
            hourly[h] += secs;
            total_seconds += secs;
        }

        let active_days = daily.values().filter(|&&v| v > 0).count() as i64;
        let average_daily_seconds = if active_days > 0 {
            total_seconds / active_days
        } else {
            0
        };

        let daily_trend = build_daily_trend(start, end, &daily);
        let hourly_distribution = hourly.to_vec();

        Ok(CategorySummaryModel {
            category_id,
            category_name,
            total_seconds,
            previous_total_seconds,
            active_days,
            average_daily_seconds,
            daily_trend,
            hourly_distribution,
        })
    }

    // 网站分类汇总（与概览/分类页保持完全一致的浏览器会话交集与小时预算封顶口径）
    pub async fn get_web_category_summary(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDate,
        end: NaiveDate,
        prev_start: Option<NaiveDate>,
        prev_end: Option<NaiveDate>,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<CategorySummaryModel, AppError> {
        debug!(
            "get_web_category_summary: cat={} start={} end={}",
            category_id, start, end
        );

        let category_name: Option<String> =
            sqlx::query_scalar("SELECT Name FROM CategoryModels WHERE ID = ?")
                .bind(category_id)
                .fetch_optional(pool)
                .await?;
        let category_name = category_name.unwrap_or_else(|| String::from("?"));

        let tz = parse_timezone(tz_id);
        let utc_start = tz_date_to_utc_range(start, &tz).0;
        let utc_end = tz_date_to_utc_range(end, &tz).1;

        let web_data = collect_web_category_data(pool, category_id, utc_start, utc_end, start, end, &tz, config_service).await?;

        // 环比总时长：同样走浏览器会话交集与小时预算封顶
        let previous_total_seconds = match (prev_start, prev_end) {
            (Some(ps), Some(pe)) if ps <= pe => {
                sum_web_range_total(pool, category_id, ps, pe, &tz, config_service).await
            }
            _ => 0,
        };

        let active_days = web_data.daily.values().filter(|&&v| v > 0).count() as i64;
        let average_daily_seconds = if active_days > 0 {
            web_data.total_seconds / active_days
        } else {
            0
        };

        let daily_trend = build_daily_trend(start, end, &web_data.daily);
        let hourly_distribution = web_data.hourly.to_vec();

        Ok(CategorySummaryModel {
            category_id,
            category_name,
            total_seconds: web_data.total_seconds,
            previous_total_seconds,
            active_days,
            average_daily_seconds,
            daily_trend,
            hourly_distribution,
        })
    }

    /// 区间成员明细，用于柱状图点击列展开
    pub async fn get_app_category_members(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDateTime,
        end: NaiveDateTime,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<Vec<CategoryMemberModel>, AppError> {
        let tz = parse_timezone(tz_id);
        let utc_start = tz_naive_to_utc(start, &tz);
        let utc_end = tz_naive_to_utc(end, &tz);

        let excluded = config_service.get_excluded_app_id_set(pool).await;
        let excluded_vec: Vec<i64> = excluded.into_iter().collect();
        let has_excluded = !excluded_vec.is_empty();

        let rows: Vec<(i64, i64)> = if !has_excluded {
            sqlx::query_as(
                r#"
                SELECT a.ID, h.Time
                FROM HoursLogModels h
                JOIN AppModels a ON h.AppModelID = a.ID
                WHERE a.CategoryID = ?
                  AND h.DataTime >= ? AND h.DataTime < ?
                  AND h.AppModelID != 0
                "#,
            )
            .bind(category_id)
            .bind(utc_start)
            .bind(utc_end)
            .fetch_all(pool)
            .await?
        } else {
            let placeholders = excluded_vec
                .iter()
                .map(|_| "?")
                .collect::<Vec<_>>()
                .join(",");
            let sql = format!(
                r#"
                SELECT a.ID, h.Time
                FROM HoursLogModels h
                JOIN AppModels a ON h.AppModelID = a.ID
                WHERE a.CategoryID = ?
                  AND h.DataTime >= ? AND h.DataTime < ?
                  AND h.AppModelID != 0
                  AND h.AppModelID NOT IN ({})
                "#,
                placeholders
            );
            let mut q = sqlx::query_as::<_, (i64, i64)>(&sql)
                .bind(category_id)
                .bind(utc_start)
                .bind(utc_end);
            for id in &excluded_vec {
                q = q.bind(*id);
            }
            q.fetch_all(pool).await?
        };

        let mut by_app: HashMap<i64, i64> = HashMap::new();
        for (app_id, secs) in &rows {
            *by_app.entry(*app_id).or_insert(0) += secs;
        }

        build_top_items_for_apps(pool, &by_app).await
    }

    /// 网站分类区间成员明细（走统一归属与小时预算封顶口径）
    pub async fn get_web_category_members(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDateTime,
        end: NaiveDateTime,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<Vec<CategoryMemberModel>, AppError> {
        let tz = parse_timezone(tz_id);
        let utc_start = tz_naive_to_utc(start, &tz);
        let utc_end = tz_naive_to_utc(end, &tz);

        let cat_site_ids = load_category_site_ids(pool, category_id).await?;
        if cat_site_ids.is_empty() || utc_end <= utc_start {
            return Ok(Vec::new());
        }

        let attrib = entity::compute_window_attribution(pool, config_service, utc_start, utc_end, &tz).await?;
        let mut by_site: HashMap<i64, i64> = HashMap::new();
        for ((_hour_ts, site_id), item) in attrib.site_hours {
            if item.credited > 0 && cat_site_ids.contains(&site_id) {
                *by_site.entry(site_id).or_insert(0) += item.credited;
            }
        }

        build_top_items_for_sites(pool, &by_site).await
    }

    /// 统一分类摘要（同时聚合净应用与归属网站，确保与概览/分类页完全一致）
    pub async fn get_unified_category_summary(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDate,
        end: NaiveDate,
        prev_start: Option<NaiveDate>,
        prev_end: Option<NaiveDate>,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<CategorySummaryModel, AppError> {
        let category_name: Option<String> =
            sqlx::query_scalar("SELECT Name FROM CategoryModels WHERE ID = ?")
                .bind(category_id)
                .fetch_optional(pool)
                .await?;
        let category_name = category_name.unwrap_or_else(|| String::from("?"));

        let tz = parse_timezone(tz_id);
        let utc_start = tz_date_to_utc_range(start, &tz).0;
        let utc_end = tz_date_to_utc_range(end, &tz).1;

        let data = collect_unified_category_data(
            pool,
            category_id,
            utc_start,
            utc_end,
            start,
            end,
            &tz,
            config_service,
        )
        .await?;

        let previous_total_seconds = match (prev_start, prev_end) {
            (Some(ps), Some(pe)) if ps <= pe => {
                let (p_start, _) = tz_date_to_utc_range(ps, &tz);
                let (_, p_end) = tz_date_to_utc_range(pe, &tz);
                collect_unified_category_data(
                    pool,
                    category_id,
                    p_start,
                    p_end,
                    ps,
                    pe,
                    &tz,
                    config_service,
                )
                .await
                .map(|m| m.total_seconds)
                .unwrap_or(0)
            }
            _ => 0,
        };

        let daily_trend = build_daily_trend(start, end, &data.daily);
        let active_days = daily_trend.iter().filter(|d| d.seconds > 0).count() as i64;
        let average_daily_seconds = if active_days > 0 {
            data.total_seconds / active_days
        } else {
            0
        };

        Ok(CategorySummaryModel {
            category_id,
            category_name,
            total_seconds: data.total_seconds,
            previous_total_seconds,
            active_days,
            average_daily_seconds,
            daily_trend,
            hourly_distribution: data.hourly.to_vec(),
        })
    }

    /// 统一分类成员明细（净应用 + 归属网站按时长降序）
    pub async fn get_unified_category_members(
        pool: &SqlitePool,
        category_id: i64,
        start: NaiveDateTime,
        end: NaiveDateTime,
        tz_id: &str,
        config_service: &ConfigService,
    ) -> Result<Vec<CategoryMemberModel>, AppError> {
        let tz = parse_timezone(tz_id);
        let utc_start = tz_naive_to_utc(start, &tz);
        let utc_end = tz_naive_to_utc(end, &tz);
        if utc_end <= utc_start {
            return Ok(Vec::new());
        }

        let start_date = start.date();
        let end_date = end.date();
        let data = collect_unified_category_data(
            pool,
            category_id,
            utc_start,
            utc_end,
            start_date,
            end_date,
            &tz,
            config_service,
        )
        .await?;

        let mut apps = build_top_items_for_apps(pool, &data.by_app).await?;
        let mut sites = build_top_items_for_sites(pool, &data.by_site).await?;
        apps.append(&mut sites);
        apps.sort_by(|a, b| b.seconds.cmp(&a.seconds));
        Ok(apps)
    }
}

fn build_daily_trend(
    start: NaiveDate,
    end: NaiveDate,
    daily: &HashMap<NaiveDate, i64>,
) -> Vec<DailyPointModel> {
    let days = (end - start).num_days();
    if days < 0 {
        return Vec::new();
    }
    let mut out = Vec::with_capacity((days + 1) as usize);
    let mut cur = start;
    while cur <= end {
        out.push(DailyPointModel {
            date: cur,
            seconds: daily.get(&cur).copied().unwrap_or(0),
        });
        match cur.succ_opt() {
            Some(d) => cur = d,
            None => break,
        }
    }
    out
}

async fn build_top_items_for_apps(
    pool: &SqlitePool,
    by_app: &HashMap<i64, i64>,
) -> Result<Vec<CategoryMemberModel>, AppError> {
    if by_app.is_empty() {
        return Ok(Vec::new());
    }

    let app_ids: Vec<i64> = by_app.keys().copied().collect();
    let placeholders = app_ids.iter().map(|_| "?").collect::<Vec<_>>().join(",");
    let sql = format!(
        "SELECT ID, Name, Alias, IconFile FROM AppModels WHERE ID IN ({})",
        placeholders
    );
    let mut q = sqlx::query_as::<_, (i64, Option<String>, Option<String>, Option<String>)>(&sql);
    for id in &app_ids {
        q = q.bind(*id);
    }
    let rows = q.fetch_all(pool).await?;
    let meta_map: HashMap<i64, (String, Option<String>)> = rows
        .into_iter()
        .map(|(id, name, alias, icon)| {
            let display = alias
                .filter(|s| !s.is_empty())
                .or(name)
                .unwrap_or_else(|| String::from("?"));
            (id, (display, icon))
        })
        .collect();

    let mut sorted: Vec<(i64, i64)> = by_app.iter().map(|(k, v)| (*k, *v)).collect();
    sorted.sort_by(|a, b| b.1.cmp(&a.1));

    Ok(reduce_to_top_n(sorted, &meta_map, "app"))
}

async fn build_top_items_for_sites(
    pool: &SqlitePool,
    by_site: &HashMap<i64, i64>,
) -> Result<Vec<CategoryMemberModel>, AppError> {
    if by_site.is_empty() {
        return Ok(Vec::new());
    }

    let site_ids: Vec<i64> = by_site.keys().copied().collect();
    let placeholders = site_ids.iter().map(|_| "?").collect::<Vec<_>>().join(",");
    let sql = format!(
        "SELECT ID, Title, Domain, Alias, IconFile FROM WebSiteModels WHERE ID IN ({})",
        placeholders
    );
    let mut q = sqlx::query_as::<_, (
        i64,
        Option<String>,
        Option<String>,
        Option<String>,
        Option<String>,
    )>(&sql);
    for id in &site_ids {
        q = q.bind(*id);
    }
    let rows = q.fetch_all(pool).await?;
    let meta_map: HashMap<i64, (String, Option<String>)> = rows
        .into_iter()
        .map(|(id, title, domain, alias, icon)| {
            let display = alias
                .filter(|s| !s.is_empty())
                .or_else(|| title.filter(|s| !s.is_empty()))
                .or(domain)
                .unwrap_or_else(|| String::from("?"));
            (id, (display, icon))
        })
        .collect();

    let mut sorted: Vec<(i64, i64)> = by_site.iter().map(|(k, v)| (*k, *v)).collect();
    sorted.sort_by(|a, b| b.1.cmp(&a.1));

    Ok(reduce_to_top_n(sorted, &meta_map, "site"))
}

/// 全量返回成员，按秒降序
fn reduce_to_top_n(
    sorted: Vec<(i64, i64)>,
    meta: &HashMap<i64, (String, Option<String>)>,
    kind: &str,
) -> Vec<CategoryMemberModel> {
    sorted
        .into_iter()
        .map(|(id, secs)| {
            let (name, icon) = meta
                .get(&id)
                .cloned()
                .unwrap_or_else(|| (String::from("?"), None));
            CategoryMemberModel {
                id,
                name,
                icon_file: icon,
                seconds: secs,
                kind: Some(kind.to_string()),
            }
        })
        .collect()
}

/// App 分类区间总时长，纯 SUM 标量查询
async fn sum_app_range_total(
    pool: &SqlitePool,
    category_id: i64,
    start: NaiveDate,
    end: NaiveDate,
    tz: &Tz,
    has_excluded: bool,
    excluded_vec: &[i64],
) -> i64 {
    let utc_start = tz_date_to_utc_range(start, tz).0;
    let utc_end = tz_date_to_utc_range(end, tz).1;

    let total: i64 = if !has_excluded {
        sqlx::query_scalar(
            r#"
            SELECT COALESCE(SUM(h.Time), 0)
            FROM HoursLogModels h
            JOIN AppModels a ON h.AppModelID = a.ID
            WHERE a.CategoryID = ?
              AND h.DataTime >= ? AND h.DataTime < ?
              AND h.AppModelID != 0
            "#,
        )
        .bind(category_id)
        .bind(utc_start)
        .bind(utc_end)
        .fetch_one(pool)
        .await
        .unwrap_or(0)
    } else {
        let placeholders = excluded_vec
            .iter()
            .map(|_| "?")
            .collect::<Vec<_>>()
            .join(",");
        let sql = format!(
            r#"
            SELECT COALESCE(SUM(h.Time), 0)
            FROM HoursLogModels h
            JOIN AppModels a ON h.AppModelID = a.ID
            WHERE a.CategoryID = ?
              AND h.DataTime >= ? AND h.DataTime < ?
              AND h.AppModelID != 0
              AND h.AppModelID NOT IN ({})
            "#,
            placeholders
        );
        let mut q = sqlx::query_scalar::<_, i64>(&sql)
            .bind(category_id)
            .bind(utc_start)
            .bind(utc_end);
        for id in excluded_vec {
            q = q.bind(*id);
        }
        q.fetch_one(pool).await.unwrap_or(0)
    };
    total
}

struct CategoryAggBucket {
    total_seconds: i64,
    daily: HashMap<NaiveDate, i64>,
    hourly: [i64; 24],
    by_app: HashMap<i64, i64>,
    by_site: HashMap<i64, i64>,
}

async fn load_category_site_ids(pool: &SqlitePool, category_id: i64) -> Result<HashSet<i64>, AppError> {
    let ids: Vec<i64> = sqlx::query_scalar("SELECT ID FROM WebSiteModels WHERE CategoryID = ?")
        .bind(category_id)
        .fetch_all(pool)
        .await?;
    Ok(ids.into_iter().collect())
}

fn record_hour_secs(
    ts: i64,
    secs: i64,
    start: NaiveDate,
    end: NaiveDate,
    tz: &Tz,
    daily: &mut HashMap<NaiveDate, i64>,
    hourly: &mut [i64; 24],
) -> bool {
    let Some(utc_dt) = DateTime::<Utc>::from_timestamp(ts, 0) else {
        return false;
    };
    let local = utc_dt.with_timezone(tz);
    let local_date = local.date_naive();
    if local_date < start || local_date > end {
        return false;
    }
    *daily.entry(local_date).or_insert(0) += secs;
    hourly[local.time().hour() as usize] += secs;
    true
}

async fn collect_web_category_data(
    pool: &SqlitePool,
    category_id: i64,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
    start: NaiveDate,
    end: NaiveDate,
    tz: &Tz,
    config_service: &ConfigService,
) -> Result<CategoryAggBucket, AppError> {
    let mut bucket = CategoryAggBucket {
        total_seconds: 0,
        daily: HashMap::new(),
        hourly: [0; 24],
        by_app: HashMap::new(),
        by_site: HashMap::new(),
    };

    let cat_site_ids = load_category_site_ids(pool, category_id).await?;
    if cat_site_ids.is_empty() || utc_end <= utc_start {
        return Ok(bucket);
    }

    let attrib = entity::compute_window_attribution(pool, config_service, utc_start, utc_end, tz).await?;
    for ((hour_ts, site_id), item) in attrib.site_hours {
        if item.credited <= 0 || !cat_site_ids.contains(&site_id) {
            continue;
        }
        if record_hour_secs(
            hour_ts.max(utc_start.timestamp()),
            item.credited,
            start,
            end,
            tz,
            &mut bucket.daily,
            &mut bucket.hourly,
        ) {
            bucket.total_seconds += item.credited;
            *bucket.by_site.entry(site_id).or_insert(0) += item.credited;
        }
    }

    Ok(bucket)
}

async fn collect_unified_category_data(
    pool: &SqlitePool,
    category_id: i64,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
    start: NaiveDate,
    end: NaiveDate,
    tz: &Tz,
    config_service: &ConfigService,
) -> Result<CategoryAggBucket, AppError> {
    let mut bucket = CategoryAggBucket {
        total_seconds: 0,
        daily: HashMap::new(),
        hourly: [0; 24],
        by_app: HashMap::new(),
        by_site: HashMap::new(),
    };
    if utc_end <= utc_start {
        return Ok(bucket);
    }

    let attrib = entity::compute_window_attribution(pool, config_service, utc_start, utc_end, tz).await?;
    let cat_app_ids: HashSet<i64> = attrib
        .apps
        .iter()
        .filter(|agg| agg.app.app_category_id == Some(category_id))
        .filter_map(|agg| agg.app.app_id)
        .collect();
    let cat_site_ids = load_category_site_ids(pool, category_id).await?;

    // 先对属于该分类的应用按 UTC 小时减去被站点领走的部分（非浏览器 app 扣减为 0）
    for app_id in cat_app_ids {
        let Some(entries) = attrib.app_hours.get(&app_id) else {
            continue;
        };
        let mut by_hour: HashMap<i64, i64> = HashMap::new();
        for &(ts, secs) in entries {
            let hour_ts = ts.div_euclid(3600) * 3600;
            *by_hour.entry(hour_ts).or_insert(0) += secs;
        }
        for (hour_ts, raw_secs) in by_hour {
            let deducted = attrib
                .browser_hour_credits
                .get(&app_id)
                .and_then(|m| m.get(&hour_ts))
                .copied()
                .unwrap_or(0);
            let net = (raw_secs - deducted).max(0);
            if net <= 0 {
                continue;
            }
            if record_hour_secs(
                hour_ts.max(utc_start.timestamp()),
                net,
                start,
                end,
                tz,
                &mut bucket.daily,
                &mut bucket.hourly,
            ) {
                bucket.total_seconds += net;
                *bucket.by_app.entry(app_id).or_insert(0) += net;
            }
        }
    }

    // 再叠加归属于该分类的站点（已完成会话交集与小时封顶）
    for ((hour_ts, site_id), item) in attrib.site_hours {
        if item.credited <= 0 || !cat_site_ids.contains(&site_id) {
            continue;
        }
        if record_hour_secs(
            hour_ts.max(utc_start.timestamp()),
            item.credited,
            start,
            end,
            tz,
            &mut bucket.daily,
            &mut bucket.hourly,
        ) {
            bucket.total_seconds += item.credited;
            *bucket.by_site.entry(site_id).or_insert(0) += item.credited;
        }
    }

    Ok(bucket)
}

/// Web 分类区间总时长（复用会话交集与小时预算封顶口径）
async fn sum_web_range_total(
    pool: &SqlitePool,
    category_id: i64,
    start: NaiveDate,
    end: NaiveDate,
    tz: &Tz,
    config_service: &ConfigService,
) -> i64 {
    let utc_start = tz_date_to_utc_range(start, tz).0;
    let utc_end = tz_date_to_utc_range(end, tz).1;
    collect_web_category_data(pool, category_id, utc_start, utc_end, start, end, tz, config_service)
        .await
        .map(|b| b.total_seconds)
        .unwrap_or(0)
}
