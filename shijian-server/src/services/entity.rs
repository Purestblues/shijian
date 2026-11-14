//! 统一实体表（N2）：把应用与网站扣成一张互斥的屏幕时间表。
//!
//! 口径（见 `docs/ENTITY-MODEL.md`）：
//! - 应用时长来自 `HoursLogModels`（UTC 小时桶），网站时长来自 `WebBrowseLogModels`；
//! - 浏览器的真实活跃时长来自 `AppSessions`，属于它的站点日志按区间求交后**扣减**，
//!   余额行叫「浏览器内其他页面」（新标签页、未收录域名、扩展没上报的页面）；
//! - 关掉「网站并入浏览器」时：站点独立成行，浏览器只剩余额；
//!   打开时：站点行消失，浏览器拿回完整活跃时长（站点作为成员挂在它下面）；
//! - 总额在两种视图下都是「应用总时长」：差额行有名字，所以不会蒸发。
//!
//! 合并组（子母程序）在这一层应用：母成员保留一行，其余成员折进来并在 `members` 里交代。

use chrono::{DateTime, Datelike, Duration, NaiveDate, Utc};
use sqlx::SqlitePool;
use std::collections::{HashMap, HashSet};

use crate::error::AppError;
use crate::models::app::AppJoinCols;
use crate::models::entity::{
    BrowserOptionModel, EntityHoursModel, EntityHoursRow, EntityListModel, EntityMemberModel,
    EntityModel, EntitySummaryModel, MergeGroupListModel, MergeGroupModel,
};
use crate::models::web::WebSiteJoinCols;
use crate::services::app_merge::{self, MergeCandidate};
use crate::services::config::ConfigService;
use crate::utils::{parse_timezone, tz_date_to_utc_range, tz_naive_to_utc};

/// 每个浏览器标识的候选进程名片段：扩展按构建目标上报 chrome / firefox / safari，
/// 而 Chromium 系（Edge / Brave / Arc）装 chrome 版扩展时也会上报 chrome，
/// 所以这里允许一族进程名映射到同一个标识，取时长最长的那个当实体。
const BROWSER_FAMILIES: [(&str, &[&str]); 3] = [
    (
        "chrome",
        &[
            "chrome", "chromium", "msedge", "brave", "vivaldi", "arc", "thorium", "360se",
            "360chrome", "qqbrowser",
        ],
    ),
    ("firefox", &["firefox", "librewolf", "waterfox", "zen"]),
    ("safari", &["safari"]),
];

pub struct EntityService;

#[derive(Debug, Clone)]
pub(crate) struct AppAgg {
    pub(crate) app: AppJoinCols,
    pub(crate) seconds: i64,
}

#[derive(Debug, Clone, Default)]
pub(crate) struct SiteHourAttribution {
    pub(crate) credited: i64,
    pub(crate) unmatched: i64,
    pub(crate) by_browser: HashMap<i64, i64>,
}

pub(crate) struct WindowAttribution {
    pub(crate) app_seconds: i64,
    pub(crate) apps: Vec<AppAgg>,
    pub(crate) app_hours: HashMap<i64, Vec<(i64, i64)>>,
    pub(crate) browser_apps: HashMap<String, i64>,
    pub(crate) default_browser: String,
    pub(crate) site_hours: HashMap<(i64, i64), SiteHourAttribution>,
    pub(crate) browser_hour_credits: HashMap<i64, HashMap<i64, i64>>,
    pub(crate) overflow_seconds: i64,
    pub(crate) unmatched_seconds: i64,
}

#[derive(Debug, Clone, Default)]
struct SiteAgg {
    domain: String,
    title: Option<String>,
    alias: Option<String>,
    icon_file: Option<String>,
    category_id: i64,
    category_name: Option<String>,
    category_color: Option<String>,
    /// 已按互斥归属扣减后的秒数（可能来自多个浏览器，累加）
    credited: i64,
    /// 未能归属到任何活跃浏览器会话的秒数
    unmatched: i64,
    /// 归属到哪些浏览器（应用 ID + 秒数），`browserMerged=1` 时作为成员展示
    by_browser: HashMap<i64, i64>,
}

/// 在单窗口内完成「HoursLogModels 预算 + 浏览器会话交集 + 同小时多站点比例封顶」归属。
/// `list`、`hours`、`category_summary` 与 `web_data` 统计统一复用这一套口径，保证总额严格守恒。
pub(crate) async fn compute_window_attribution(
    pool: &SqlitePool,
    config_service: &ConfigService,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
    tz: &chrono_tz::Tz,
) -> Result<WindowAttribution, AppError> {
    #[derive(sqlx::FromRow)]
    struct WindowHourRow {
        #[sqlx(rename = "AppModelID")]
        app_id: i64,
        #[sqlx(rename = "Hour")]
        hour: DateTime<Utc>,
        #[sqlx(rename = "total")]
        total: i64,
    }

    let config = config_service.get_or_load().await?;
    let configured_browser = config.general.default_browser.trim().to_lowercase();
    let excluded = config_service.get_excluded_app_id_set(pool).await;

    let all_apps = load_app_aggregates_all(pool).await?;
    let app_meta: HashMap<i64, &AppJoinCols> = all_apps
        .iter()
        .filter_map(|agg| agg.app.app_id.map(|id| (id, &agg.app)))
        .collect();

    let hour_rows: Vec<WindowHourRow> = sqlx::query_as(
        r#"
        SELECT AppModelID, DataTime AS Hour, SUM(Time) AS total
        FROM HoursLogModels
        WHERE DataTime >= ? AND DataTime < ?
        GROUP BY AppModelID, DataTime
        "#,
    )
    .bind(utc_start)
    .bind(utc_end)
    .fetch_all(pool)
    .await?;

    let mut app_seconds: i64 = 0;
    let mut app_totals: HashMap<i64, i64> = HashMap::new();
    let mut app_hours: HashMap<i64, Vec<(i64, i64)>> = HashMap::new();
    let mut app_hour_budgets: HashMap<(i64, i64), i64> = HashMap::new();

    for row in hour_rows {
        if excluded.contains(&row.app_id) || !app_meta.contains_key(&row.app_id) {
            continue;
        }
        let ts = row.hour.timestamp();
        let hour_ts = ts.div_euclid(3600) * 3600;
        app_seconds += row.total;
        *app_totals.entry(row.app_id).or_insert(0) += row.total;
        app_hours.entry(row.app_id).or_default().push((ts, row.total));
        *app_hour_budgets.entry((row.app_id, hour_ts)).or_insert(0) += row.total;
    }

    let apps: Vec<AppAgg> = app_totals
        .into_iter()
        .filter_map(|(app_id, seconds)| {
            app_meta.get(&app_id).map(|meta| AppAgg {
                app: (*meta).clone(),
                seconds,
            })
        })
        .collect();

    let browser_apps = resolve_browser_apps(&apps, &excluded);
    let default_browser = if configured_browser.is_empty() {
        recommend_default_browser(pool, &browser_apps).await?
    } else {
        configured_browser
    };

    let sessions = load_session_intervals(pool, utc_start, utc_end).await?;
    let logs = load_browse_logs(pool, utc_start, utc_end).await?;

    let mut site_hours: HashMap<(i64, i64), SiteHourAttribution> = HashMap::new();
    let mut matched_raw: HashMap<(i64, i64), HashMap<i64, i64>> = HashMap::new();
    let mut browser_hour_credits: HashMap<i64, HashMap<i64, i64>> = HashMap::new();
    let mut overflow_seconds: i64 = 0;
    let mut unmatched_seconds: i64 = 0;

    let win_start = utc_start.timestamp();
    let win_end = utc_end.timestamp();

    // 记录浏览器在哪些本地日期存在 AppSessions（使单日 vs 多日口径严格可加，当天无会话时归入 unmatched）
    let mut session_days: HashSet<(i64, NaiveDate)> = HashSet::new();
    for (&app_id, intervals) in &sessions {
        for &(s, e) in intervals {
            let cs = s.max(win_start);
            let ce = e.min(win_end);
            if ce <= cs {
                continue;
            }
            if let (Some(dt0), Some(dt1)) = (
                DateTime::<Utc>::from_timestamp(cs, 0),
                DateTime::<Utc>::from_timestamp((ce - 1).max(cs), 0),
            ) {
                let mut d = dt0.with_timezone(tz).date_naive();
                let end_d = dt1.with_timezone(tz).date_naive();
                while d <= end_d {
                    session_days.insert((app_id, d));
                    d += Duration::days(1);
                }
            }
        }
    }

    for log in logs {
        let clip_start = log.start.timestamp().max(win_start);
        let clip_end = log.end.timestamp().min(win_end);
        if clip_end <= clip_start {
            continue;
        }

        let reference = if log.browser.trim().is_empty() {
            default_browser.as_str()
        } else {
            log.browser.trim()
        };
        let reference_lower = reference.to_lowercase();
        let browser_app = browser_apps.get(&reference_lower).copied();

        let mut cursor = clip_start;
        while cursor < clip_end {
            let hour_ts = cursor.div_euclid(3600) * 3600;
            let mut seg_end = clip_end.min(hour_ts + 3600);
            let local_date = DateTime::<Utc>::from_timestamp(cursor, 0)
                .map(|dt| dt.with_timezone(tz).date_naive());
            if let Some(d) = local_date {
                let next_midnight_utc = tz_date_to_utc_range(d, tz).1.timestamp();
                if next_midnight_utc > cursor && next_midnight_utc < seg_end {
                    seg_end = next_midnight_utc;
                }
            }
            let seg_len = seg_end - cursor;

            match (browser_app, local_date) {
                (Some(app_id), Some(d)) if session_days.contains(&(app_id, d)) => {
                    if let Some(intervals) = sessions.get(&app_id) {
                        let raw = overlap_seconds(intervals, cursor, seg_end).min(seg_len).max(0);
                        overflow_seconds += seg_len - raw;
                        if raw > 0 {
                            *matched_raw
                                .entry((app_id, hour_ts))
                                .or_default()
                                .entry(log.site_id)
                                .or_insert(0) += raw;
                        }
                    }
                }
                _ => {
                    unmatched_seconds += seg_len;
                    let entry = site_hours.entry((hour_ts, log.site_id)).or_default();
                    entry.credited += seg_len;
                    entry.unmatched += seg_len;
                }
            }

            cursor = seg_end;
        }
    }

    // 同一小时内同一浏览器的所有站点共享该浏览器在本小时的 HoursLogModels 预算，超出部分等比缩限并记入 overflow
    for ((app_id, hour_ts), site_raw_map) in matched_raw {
        let budget = app_hour_budgets
            .get(&(app_id, hour_ts))
            .copied()
            .unwrap_or(0)
            .max(0);
        let total_raw: i64 = site_raw_map.values().sum();
        if total_raw <= 0 {
            continue;
        }
        let mut items: Vec<(i64, i64)> = site_raw_map.into_iter().collect();
        items.sort_by_key(|(site_id, _)| *site_id);

        let allocated = if total_raw <= budget {
            items
        } else {
            overflow_seconds += total_raw - budget;
            allocate_proportional(&items, budget)
        };

        for (site_id, sec) in allocated {
            if sec <= 0 {
                continue;
            }
            let entry = site_hours.entry((hour_ts, site_id)).or_default();
            entry.credited += sec;
            *entry.by_browser.entry(app_id).or_insert(0) += sec;
            *browser_hour_credits
                .entry(app_id)
                .or_default()
                .entry(hour_ts)
                .or_insert(0) += sec;
        }
    }

    Ok(WindowAttribution {
        app_seconds,
        apps,
        app_hours,
        browser_apps,
        default_browser,
        site_hours,
        browser_hour_credits,
        overflow_seconds,
        unmatched_seconds,
    })
}

/// 最大余数法按比例分配整数秒数，严格保证 Σallocated == budget 且不超出 raw。
fn allocate_proportional(items: &[(i64, i64)], budget: i64) -> Vec<(i64, i64)> {
    if budget <= 0 || items.is_empty() {
        return items.iter().map(|&(id, _)| (id, 0)).collect();
    }
    let total_raw: i128 = items.iter().map(|&(_, v)| v.max(0) as i128).sum();
    if total_raw <= 0 {
        return items.iter().map(|&(id, _)| (id, 0)).collect();
    }

    let budget_i128 = budget as i128;
    let mut floors: Vec<(i64, i64, i128)> = Vec::with_capacity(items.len());
    let mut floor_sum: i64 = 0;

    for &(id, raw) in items {
        let numerator = raw.max(0) as i128 * budget_i128;
        let base = (numerator / total_raw) as i64;
        let rem = numerator % total_raw;
        floor_sum += base;
        floors.push((id, base, rem));
    }

    let mut leftover = (budget - floor_sum).max(0) as usize;
    let mut order: Vec<usize> = (0..floors.len()).collect();
    order.sort_by(|&a, &b| {
        floors[b]
            .2
            .cmp(&floors[a].2)
            .then_with(|| floors[a].0.cmp(&floors[b].0))
    });

    for idx in order {
        if leftover == 0 {
            break;
        }
        floors[idx].1 += 1;
        leftover -= 1;
    }

    floors.into_iter().map(|(id, sec, _)| (id, sec)).collect()
}

impl EntityService {
    pub async fn list(
        pool: &SqlitePool,
        config_service: &ConfigService,
        start: NaiveDate,
        end: NaiveDate,
        tz_id: &str,
        browser_merged: bool,
        take: Option<usize>,
    ) -> Result<EntityListModel, AppError> {
        let tz = parse_timezone(tz_id);
        let utc_start = tz_date_to_utc_range(start, &tz).0;
        let utc_end = tz_date_to_utc_range(end, &tz).1;
        let excluded = config_service.get_excluded_app_id_set(pool).await;

        let attrib = compute_window_attribution(pool, config_service, utc_start, utc_end, &tz).await?;
        let app_seconds = attrib.app_seconds;
        let apps = attrib.apps;
        let browser_apps = attrib.browser_apps;
        let browser_app_ids: HashSet<i64> = browser_apps.values().copied().collect();
        let default_browser = attrib.default_browser;
        let overflow_seconds = attrib.overflow_seconds;
        let unmatched_seconds = attrib.unmatched_seconds;

        let mut sites: HashMap<i64, SiteAgg> = HashMap::new();
        let mut credited_by_browser: HashMap<i64, i64> = HashMap::new();
        for ((_hour_ts, site_id), item) in attrib.site_hours {
            if item.credited <= 0 {
                continue;
            }
            let site = sites.entry(site_id).or_default();
            site.credited += item.credited;
            site.unmatched += item.unmatched;
            for (app_id, sec) in item.by_browser {
                *site.by_browser.entry(app_id).or_insert(0) += sec;
                *credited_by_browser.entry(app_id).or_insert(0) += sec;
            }
        }

        // 补全站点静态信息（只查有归属的站点）
        fill_site_details(pool, &mut sites).await?;

        // 4. 合并组：先把自动组落库，再统一按持久化的组（含 app / site / mixed）折叠应用与站点
        sync_auto_groups(pool).await?;
        let groups = load_groups(pool).await?;

        let visible_apps: Vec<&AppAgg> = apps
            .iter()
            .filter(|agg| !excluded.contains(&agg.app.app_id.unwrap_or(0)))
            .collect();

        let mut app_to_group: HashMap<String, usize> = HashMap::new();
        let mut site_to_group: HashMap<String, usize> = HashMap::new();
        for (idx, group) in groups.iter().enumerate() {
            for raw in &group.members {
                match parse_member_ref(raw) {
                    MemberTarget::App(name) => {
                        app_to_group.insert(name, idx);
                    }
                    MemberTarget::Site(domain) => {
                        site_to_group.insert(domain, idx);
                    }
                }
            }
        }

        // 记录哪些站点归入了某个合并组（用于浏览器归属扣减，防重复计算）
        let mut grouped_site_ids: HashSet<i64> = HashSet::new();
        let mut claimed_by_browser: HashMap<i64, i64> = HashMap::new();
        for (site_id, agg) in &sites {
            if agg.credited <= 0 {
                continue;
            }
            if let Some(&g_idx) = site_to_group.get(&agg.domain) {
                grouped_site_ids.insert(*site_id);
                let group = &groups[g_idx];
                let group_app_names: HashSet<String> = group
                    .members
                    .iter()
                    .filter_map(|m| match parse_member_ref(m) {
                        MemberTarget::App(name) => Some(name),
                        MemberTarget::Site(_) => None,
                    })
                    .collect();
                for (&browser_id, &sec) in &agg.by_browser {
                    let browser_in_same_group = visible_apps.iter().any(|a| {
                        a.app.app_id == Some(browser_id)
                            && a.app.app_name.as_ref().is_some_and(|n| group_app_names.contains(n))
                    });
                    if !browser_in_same_group {
                        *claimed_by_browser.entry(browser_id).or_insert(0) += sec;
                    }
                }
            }
        }

        // 5. 组装行：先组装合并组行（app / site / mixed），再组装未入组的独立应用与站点
        let mut rows: Vec<EntityModel> = Vec::new();
        let mut balance_seconds: i64 = 0;

        for (g_idx, group) in groups.iter().enumerate() {
            let group_apps: Vec<&AppAgg> = visible_apps
                .iter()
                .copied()
                .filter(|a| {
                    a.app
                        .app_name
                        .as_deref()
                        .and_then(|n| app_to_group.get(n).copied())
                        == Some(g_idx)
                })
                .collect();
            let group_sites: Vec<(i64, &SiteAgg)> = sites
                .iter()
                .filter(|(_, agg)| {
                    agg.credited > 0 && site_to_group.get(&agg.domain).copied() == Some(g_idx)
                })
                .map(|(id, agg)| (*id, agg))
                .collect();

            if group_apps.is_empty() && group_sites.is_empty() {
                continue;
            }

            let group_app_ids: HashSet<i64> =
                group_apps.iter().filter_map(|a| a.app.app_id).collect();

            let mut total_group_seconds: i64 = group_apps.iter().map(|a| a.seconds).sum();
            for (_, site_agg) in &group_sites {
                let mut site_contrib = site_agg.credited;
                for (&browser_id, &sec) in &site_agg.by_browser {
                    if group_app_ids.contains(&browser_id) {
                        site_contrib -= sec;
                    }
                }
                total_group_seconds += site_contrib.max(0);
            }

            // 如果组内包含了浏览器且关闭「网站并入浏览器」，扣掉未入同组但归属于该浏览器的站点秒数以保证总额守恒
            if !browser_merged {
                for app_agg in &group_apps {
                    let app_id = app_agg.app.app_id.unwrap_or(0);
                    if browser_app_ids.contains(&app_id) {
                        let outside_credited: i64 = sites
                            .iter()
                            .filter(|(sid, _)| !grouped_site_ids.contains(sid))
                            .filter_map(|(_, s)| s.by_browser.get(&app_id))
                            .sum();
                        total_group_seconds = (total_group_seconds - outside_credited).max(0);
                    }
                }
            }

            // 决定母成员（优先取 PrimaryRef；当母成员在本窗口没跑时退化到本窗口时长最长的成员）
            let primary_target = parse_member_ref(&group.primary_ref);
            let chosen_app = match &primary_target {
                MemberTarget::App(name) => group_apps
                    .iter()
                    .copied()
                    .find(|a| a.app.app_name.as_deref() == Some(name.as_str())),
                MemberTarget::Site(_) => None,
            };
            let chosen_site = match &primary_target {
                MemberTarget::Site(domain) => group_sites
                    .iter()
                    .copied()
                    .find(|(_, agg)| agg.domain == *domain),
                MemberTarget::App(_) => None,
            };

            enum ChosenPrimary<'a> {
                App(&'a AppAgg),
                Site(i64, &'a SiteAgg),
            }

            let chosen = if let Some(a) = chosen_app {
                ChosenPrimary::App(a)
            } else if let Some((sid, s)) = chosen_site {
                ChosenPrimary::Site(sid, s)
            } else {
                let best_app = group_apps.iter().copied().max_by_key(|a| a.seconds);
                let best_site = group_sites.iter().copied().max_by_key(|(_, s)| s.credited);
                match (best_app, best_site) {
                    (Some(a), Some((sid, s))) => {
                        if a.seconds >= s.credited {
                            ChosenPrimary::App(a)
                        } else {
                            ChosenPrimary::Site(sid, s)
                        }
                    }
                    (Some(a), None) => ChosenPrimary::App(a),
                    (None, Some((sid, s))) => ChosenPrimary::Site(sid, s),
                    (None, None) => continue,
                }
            };

            let mut members: Vec<EntityMemberModel> = Vec::new();
            let primary_app_id = match chosen {
                ChosenPrimary::App(a) => a.app.app_id,
                ChosenPrimary::Site(_, _) => None,
            };
            let primary_site_id = match chosen {
                ChosenPrimary::App(_) => None,
                ChosenPrimary::Site(sid, _) => Some(sid),
            };

            for a in &group_apps {
                if a.app.app_id == primary_app_id {
                    continue;
                }
                members.push(EntityMemberModel {
                    key: format!("app:{}", a.app.app_id.unwrap_or(0)),
                    name: display_name(&a.app),
                    seconds: a.seconds,
                    icon: a.app.app_icon_file.clone(),
                });
            }
            for (sid, s) in &group_sites {
                if Some(*sid) == primary_site_id {
                    continue;
                }
                members.push(EntityMemberModel {
                    key: format!("site:{}", sid),
                    name: site_display_name(s),
                    seconds: s.credited,
                    icon: s.icon_file.clone(),
                });
            }

            match chosen {
                ChosenPrimary::App(primary) => {
                    let app_id = primary.app.app_id.unwrap_or(0);
                    let is_browser = browser_app_ids.contains(&app_id);
                    if is_browser && browser_merged {
                        for (site_id, per_browser) in &sites {
                            if grouped_site_ids.contains(site_id) {
                                continue;
                            }
                            if let Some(seconds) = per_browser.by_browser.get(&app_id) {
                                let agg = &sites[site_id];
                                members.push(EntityMemberModel {
                                    key: format!("site:{}", site_id),
                                    name: site_display_name(agg),
                                    seconds: *seconds,
                                    icon: agg.icon_file.clone(),
                                });
                            }
                        }
                    }
                    rows.push(EntityModel {
                        key: format!("app:{}", app_id),
                        kind: group.kind.clone(),
                        name: group.name.clone(),
                        icon: primary.app.app_icon_file.clone(),
                        seconds: total_group_seconds,
                        category_id: primary.app.app_category_id.unwrap_or(0),
                        category_kind: "app".to_string(),
                        category_name: primary.app.cat_name.clone(),
                        category_color: primary.app.cat_color.clone(),
                        is_browser,
                        members,
                    });
                }
                ChosenPrimary::Site(site_id, primary) => {
                    rows.push(EntityModel {
                        key: format!("site:{}", site_id),
                        kind: group.kind.clone(),
                        name: group.name.clone(),
                        icon: primary.icon_file.clone(),
                        seconds: total_group_seconds,
                        category_id: primary.category_id,
                        category_kind: "site".to_string(),
                        category_name: primary.category_name.clone(),
                        category_color: primary.category_color.clone(),
                        is_browser: false,
                        members,
                    });
                }
            }
        }

        // 未入组的独立应用
        for primary in &visible_apps {
            let name_key = primary.app.app_name.as_deref().unwrap_or("");
            if app_to_group.contains_key(name_key) {
                continue;
            }
            let app_id = primary.app.app_id.unwrap_or(0);
            let is_browser = browser_app_ids.contains(&app_id);
            let site_credited = credited_by_browser.get(&app_id).copied().unwrap_or(0);

            if is_browser && !browser_merged {
                // 关闭「网站并入浏览器」：浏览器行只剩余额
                let balance = (primary.seconds - site_credited).max(0);
                if balance > 0 {
                    balance_seconds += balance;
                    rows.push(EntityModel {
                        key: format!("other:{}", app_id),
                        kind: "other".to_string(),
                        name: other_row_name(&primary.app, browser_apps.len()),
                        icon: primary.app.app_icon_file.clone(),
                        seconds: balance,
                        // 差额行属于浏览器这个实体：分类跟着浏览器走，
                        // 否则「分类页各分类之和」会比总额少掉浏览器剩下的那段时间
                        category_id: primary.app.app_category_id.unwrap_or(0),
                        category_kind: "app".to_string(),
                        category_name: primary.app.cat_name.clone(),
                        category_color: primary.app.cat_color.clone(),
                        is_browser: true,
                        members: Vec::new(),
                    });
                }
                continue;
            }

            let mut members: Vec<EntityMemberModel> = Vec::new();
            let mut row_seconds = primary.seconds;
            if is_browser && browser_merged {
                // 若有站点被手动归入 mixed / site 组，这里扣除防止在同一页面算两次
                let claimed = claimed_by_browser.get(&app_id).copied().unwrap_or(0);
                row_seconds = (row_seconds - claimed).max(0);
                for (site_id, per_browser) in &sites {
                    if grouped_site_ids.contains(site_id) {
                        continue;
                    }
                    if let Some(seconds) = per_browser.by_browser.get(&app_id) {
                        let agg = &sites[site_id];
                        members.push(EntityMemberModel {
                            key: format!("site:{}", site_id),
                            name: site_display_name(agg),
                            seconds: *seconds,
                            icon: agg.icon_file.clone(),
                        });
                    }
                }
            }

            rows.push(EntityModel {
                key: format!("app:{}", app_id),
                kind: "app".to_string(),
                name: display_name(&primary.app),
                icon: primary.app.app_icon_file.clone(),
                seconds: row_seconds,
                category_id: primary.app.app_category_id.unwrap_or(0),
                category_kind: "app".to_string(),
                category_name: primary.app.cat_name.clone(),
                category_color: primary.app.cat_color.clone(),
                is_browser,
                members,
            });
        }

        // 网站侧总时长（含并入浏览器的部分）
        let site_seconds: i64 = sites.values().map(|agg| agg.credited).sum();

        // 6. 未入组的站点行：关闭合并时全部独立成行；打开合并时只留「未归属到活跃浏览器」的剩余秒数
        for (site_id, agg) in &sites {
            if agg.credited <= 0 || grouped_site_ids.contains(site_id) {
                continue;
            }
            let display_seconds = if browser_merged {
                agg.unmatched
            } else {
                agg.credited
            };
            if display_seconds <= 0 {
                continue;
            }
            rows.push(EntityModel {
                key: format!("site:{}", site_id),
                kind: "site".to_string(),
                name: site_display_name(agg),
                icon: agg.icon_file.clone(),
                seconds: display_seconds,
                category_id: agg.category_id,
                category_kind: "site".to_string(),
                category_name: agg.category_name.clone(),
                category_color: agg.category_color.clone(),
                is_browser: false,
                members: Vec::new(),
            });
        }

        // 关闭合并时，被站点吃掉的那部分已经从浏览器行里扣掉了，站点秒数也计入展示总量
        rows.sort_by(|a, b| {
            b.seconds
                .cmp(&a.seconds)
                .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
        });

        // Top N 与「其他」归并放在服务端：排序规则只有一处，客户端不需要再切一次
        if let Some(take) = take {
            if take > 0 && rows.len() > take {
                let keep = take - 1;
                let rest = rows.split_off(keep);
                rows.push(EntityModel {
                    key: "aggregate:0".to_string(),
                    kind: "aggregate".to_string(),
                    name: "其他".to_string(),
                    icon: None,
                    seconds: rest.iter().map(|row| row.seconds).sum(),
                    category_id: 0,
                    category_kind: "app".to_string(),
                    category_name: None,
                    category_color: None,
                    is_browser: false,
                    members: Vec::new(),
                });
            }
        }

        // 两种视图的总额都等于「应用总时长 + 未对齐的站点时长」：
        // 差额行有名字，所以关掉合并时总额也不会少
        let total_seconds = app_seconds + unmatched_seconds;

        Ok(EntityListModel {
            rows,
            total_seconds,
            app_seconds,
            site_seconds,
            balance_seconds,
            overflow_seconds,
            unmatched_seconds,
            browser_merged,
            default_browser,
        })
    }

    pub async fn merge_groups(pool: &SqlitePool) -> Result<MergeGroupListModel, AppError> {
        // 设置页可能先于概览页被打开：这里也跑一次自动判定，组才不会等到进概览页才出现
        sync_auto_groups(pool).await?;
        let groups = load_groups(pool).await?;
        let suppressed = load_suppressions(pool).await?;
        Ok(MergeGroupListModel {
            groups,
            suppressed: suppressed.into_iter().collect(),
        })
    }

    /// 解散一个自动组：写 suppression，删组。manual 组同样删掉。
    pub async fn dissolve_group(pool: &SqlitePool, id: i64) -> Result<(), AppError> {
        let row: Option<(Option<String>,)> =
            sqlx::query_as("SELECT AutoKey FROM EntityMergeGroups WHERE ID = ?")
                .bind(id)
                .fetch_optional(pool)
                .await?;
        let Some((auto_key,)) = row else {
            return Ok(());
        };
        if let Some(key) = auto_key {
            sqlx::query(
                "INSERT OR REPLACE INTO EntitySuppressions (Ref, Reason, CreatedAt) VALUES (?, 'dissolved', datetime('now'))",
            )
            .bind(key)
            .execute(pool)
            .await?;
        }
        sqlx::query("DELETE FROM EntityMergeMembers WHERE GroupID = ?")
            .bind(id)
            .execute(pool)
            .await?;
        sqlx::query("DELETE FROM EntityMergeGroups WHERE ID = ?")
            .bind(id)
            .execute(pool)
            .await?;
        Ok(())
    }

    pub async fn restore_suppressed(pool: &SqlitePool) -> Result<(), AppError> {
        sqlx::query("DELETE FROM EntitySuppressions")
            .execute(pool)
            .await?;
        Ok(())
    }

    pub async fn rename_group(pool: &SqlitePool, id: i64, name: &str) -> Result<(), AppError> {
        let name = name.trim();
        if name.is_empty() {
            return Ok(());
        }
        sqlx::query("UPDATE EntityMergeGroups SET Name = ?, UpdatedAt = datetime('now') WHERE ID = ?")
            .bind(name.chars().take(64).collect::<String>())
            .bind(id)
            .execute(pool)
            .await?;
        Ok(())
    }

    pub async fn create_group(
        pool: &SqlitePool,
        name: &str,
        primary_ref: &str,
        members: &[String],
    ) -> Result<MergeGroupModel, AppError> {
        let normalized_members = normalize_members(members)?;
        let final_primary = resolve_primary_ref(primary_ref, &normalized_members)?;
        ensure_no_member_conflicts(pool, &normalized_members, None).await?;

        let kind = infer_group_kind(&normalized_members);
        let group_name = if name.trim().is_empty() {
            resolve_target_display_name(pool, &parse_member_ref(&final_primary)).await?
        } else {
            name.trim().chars().take(64).collect()
        };

        let mut tx = pool.begin().await?;
        let res = sqlx::query(
            "INSERT INTO EntityMergeGroups (Name, Kind, AutoKey, PrimaryRef, IsAuto, UpdatedAt) VALUES (?, ?, NULL, ?, 0, datetime('now'))",
        )
        .bind(&group_name)
        .bind(&kind)
        .bind(&final_primary)
        .execute(&mut *tx)
        .await?;
        let group_id = res.last_insert_rowid();

        for member in &normalized_members {
            sqlx::query(
                "INSERT OR IGNORE INTO EntityMergeMembers (GroupID, MemberRef, AddedAt) VALUES (?, ?, datetime('now'))",
            )
            .bind(group_id)
            .bind(member)
            .execute(&mut *tx)
            .await?;
        }
        tx.commit().await?;

        Ok(MergeGroupModel {
            id: group_id,
            name: group_name,
            kind,
            is_auto: false,
            primary_ref: final_primary,
            members: normalized_members,
        })
    }

    pub async fn update_group(
        pool: &SqlitePool,
        id: i64,
        name: Option<&str>,
        primary_ref: Option<&str>,
        members: Option<&[String]>,
    ) -> Result<MergeGroupModel, AppError> {
        let groups = load_groups(pool).await?;
        let Some(existing) = groups.into_iter().find(|g| g.id == id) else {
            return Err(AppError::Business("合并组不存在".to_string()));
        };

        let members_changed = members.is_some();
        let final_members = match members {
            Some(list) => normalize_members(list)?,
            None => existing.members.clone(),
        };

        if members_changed {
            ensure_no_member_conflicts(pool, &final_members, Some(id)).await?;
        }

        let final_primary = match primary_ref {
            Some(p) if !p.trim().is_empty() => resolve_primary_ref(p, &final_members)?,
            _ => {
                let existing_primary = parse_member_ref(&existing.primary_ref);
                final_members
                    .iter()
                    .find(|m| parse_member_ref(m) == existing_primary)
                    .cloned()
                    .unwrap_or_else(|| final_members[0].clone())
            }
        };

        let final_name: String = match name {
            Some(n) if !n.trim().is_empty() => n.trim().chars().take(64).collect(),
            _ => existing.name.clone(),
        };

        let final_kind = infer_group_kind(&final_members);
        let final_is_auto = if members_changed || primary_ref.is_some_and(|p| !p.trim().is_empty()) {
            false
        } else {
            existing.is_auto
        };

        let mut tx = pool.begin().await?;
        sqlx::query(
            "UPDATE EntityMergeGroups SET Name = ?, Kind = ?, PrimaryRef = ?, IsAuto = ?, UpdatedAt = datetime('now') WHERE ID = ?",
        )
        .bind(&final_name)
        .bind(&final_kind)
        .bind(&final_primary)
        .bind(if final_is_auto { 1_i64 } else { 0_i64 })
        .bind(id)
        .execute(&mut *tx)
        .await?;

        if members_changed {
            sqlx::query("DELETE FROM EntityMergeMembers WHERE GroupID = ?")
                .bind(id)
                .execute(&mut *tx)
                .await?;
            for member in &final_members {
                sqlx::query(
                    "INSERT OR IGNORE INTO EntityMergeMembers (GroupID, MemberRef, AddedAt) VALUES (?, ?, datetime('now'))",
                )
                .bind(id)
                .bind(member)
                .execute(&mut *tx)
                .await?;
            }
        }
        tx.commit().await?;

        Ok(MergeGroupModel {
            id,
            name: final_name,
            kind: final_kind,
            is_auto: final_is_auto,
            primary_ref: final_primary,
            members: final_members,
        })
    }

    /// 设置页选「默认浏览器」用：浏览日志里出现过的浏览器 + 应用侧识别到的浏览器实体
    pub async fn browsers(
        pool: &SqlitePool,
        config_service: &ConfigService,
    ) -> Result<Vec<BrowserOptionModel>, AppError> {
        let config = config_service.get_or_load().await?;
        let default_browser = config.general.default_browser.trim().to_lowercase();
        let excluded = config_service.get_excluded_app_id_set(pool).await;

        #[derive(sqlx::FromRow)]
        struct RefRow {
            #[sqlx(rename = "Ref")]
            reference: String,
            #[sqlx(rename = "Seconds")]
            seconds: i64,
        }

        let refs: Vec<RefRow> = sqlx::query_as(
            r#"
            SELECT COALESCE(NULLIF(BrowserRef, ''), '') AS Ref, SUM(Duration) AS Seconds
            FROM WebBrowseLogModels
            GROUP BY COALESCE(NULLIF(BrowserRef, ''), '')
            "#,
        )
        .fetch_all(pool)
        .await?;

        let apps = load_app_aggregates_all(pool).await?;
        let resolved = resolve_browser_apps(&apps, &excluded);

        let mut result: Vec<BrowserOptionModel> = Vec::new();
        let mut seen: HashSet<String> = HashSet::new();
        for row in refs {
            let reference = row.reference.trim().to_lowercase();
            if reference.is_empty() {
                continue;
            }
            seen.insert(reference.clone());
            let app_id = resolved.get(&reference).copied();
            let app = app_id.and_then(|id| {
                apps.iter()
                    .find(|a| a.app.app_id == Some(id))
                    .map(|a| (display_name(&a.app), a.seconds))
            });
            result.push(BrowserOptionModel {
                is_default: default_browser == reference,
                reference,
                app_key: app_id.map(|id| format!("app:{}", id)),
                app_name: app.map(|(name, _)| name),
                seconds: row.seconds,
            });
        }
        // 应用侧识别到、但还没有浏览日志的浏览器也列出来（用户可能刚装上扩展）
        for (reference, app_id) in resolved {
            if seen.contains(&reference) {
                continue;
            }
            let app = apps.iter().find(|a| a.app.app_id == Some(app_id));
            result.push(BrowserOptionModel {
                is_default: default_browser == reference,
                reference,
                app_key: Some(format!("app:{}", app_id)),
                app_name: app.map(|a| display_name(&a.app)),
                seconds: app.map(|a| a.seconds).unwrap_or(0),
            });
        }
        result.sort_by(|a, b| b.seconds.cmp(&a.seconds).then_with(|| a.reference.cmp(&b.reference)));
        Ok(result)
    }
}

impl EntityService {
    /// 实体 × 时段矩阵（`/api/entity/hours`）。
    ///
    /// 桶由服务端按本地时区切好：单日 = 24 小时、区间 = 每天、整年 = 12 个月。
    /// 互斥归属在这里同样是必需的：站点日志按浏览器会话求交后从浏览器里扣掉，
    /// 否则「分类 × 时段」柱图会把同一分钟画两次。
    pub async fn hours(
        pool: &SqlitePool,
        config_service: &ConfigService,
        start: NaiveDate,
        end: NaiveDate,
        tz_id: &str,
        buckets: &str,
        browser_merged: bool,
    ) -> Result<EntityHoursModel, AppError> {
        let tz = parse_timezone(tz_id);
        let windows = bucket_windows(start, end, &tz, buckets);
        let bucket_count = windows.len();
        if bucket_count == 0 {
            return Ok(EntityHoursModel {
                labels: Vec::new(),
                rows: Vec::new(),
                browser_merged,
                bucket_count: 0,
            });
        }
        let utc_start = DateTime::from_timestamp(windows[0].0, 0)
            .ok_or_else(|| AppError::Business("无效的时间范围".to_string()))?;
        let utc_end = DateTime::from_timestamp(windows[bucket_count - 1].1, 0)
            .ok_or_else(|| AppError::Business("无效的时间范围".to_string()))?;

        let attrib = compute_window_attribution(pool, config_service, utc_start, utc_end, &tz).await?;
        let browser_apps = attrib.browser_apps;
        let browser_app_ids: HashSet<i64> = browser_apps.values().copied().collect();

        // 1. 应用 × 桶（UTC 小时桶落到对应桶里）
        let mut app_matrix: HashMap<i64, Vec<i64>> = HashMap::new();
        for (app_id, entries) in &attrib.app_hours {
            for &(hour_ts, total) in entries {
                if let Some(index) = bucket_index(&windows, hour_ts) {
                    let entry = app_matrix
                        .entry(*app_id)
                        .or_insert_with(|| vec![0_i64; bucket_count]);
                    entry[index] += total;
                }
            }
        }

        // 2. 站点 × 桶（复用与 list 相同的小时预算封顶与会话交集结果）
        let mut site_matrix: HashMap<i64, Vec<i64>> = HashMap::new();
        let mut site_unmatched_matrix: HashMap<i64, Vec<i64>> = HashMap::new();
        let mut browser_site_matrix: HashMap<i64, Vec<i64>> = HashMap::new();
        let mut site_by_browser_matrix: HashMap<i64, HashMap<i64, Vec<i64>>> = HashMap::new();
        let mut attributed_sites: HashSet<i64> = HashSet::new();

        for ((hour_ts, site_id), item) in attrib.site_hours {
            if item.credited <= 0 {
                continue;
            }
            let effective_ts = hour_ts.max(windows[0].0);
            let Some(index) = bucket_index(&windows, effective_ts) else {
                continue;
            };

            let site = site_matrix
                .entry(site_id)
                .or_insert_with(|| vec![0_i64; bucket_count]);
            site[index] += item.credited;

            if item.unmatched > 0 {
                let unmatched_entry = site_unmatched_matrix
                    .entry(site_id)
                    .or_insert_with(|| vec![0_i64; bucket_count]);
                unmatched_entry[index] += item.unmatched;
            }

            if !item.by_browser.is_empty() {
                attributed_sites.insert(site_id);
                for (app_id, sec) in item.by_browser {
                    if sec <= 0 {
                        continue;
                    }
                    let entry = browser_site_matrix
                        .entry(app_id)
                        .or_insert_with(|| vec![0_i64; bucket_count]);
                    entry[index] += sec;

                    let per_browser = site_by_browser_matrix
                        .entry(site_id)
                        .or_default()
                        .entry(app_id)
                        .or_insert_with(|| vec![0_i64; bucket_count]);
                    per_browser[index] += sec;
                }
            }
        }

        // 3. 站点静态信息 + 合并组
        let mut site_aggs: HashMap<i64, SiteAgg> = site_matrix
            .keys()
            .map(|site_id| (*site_id, SiteAgg::default()))
            .collect();
        fill_site_details(pool, &mut site_aggs).await?;

        let window_apps: Vec<AppAgg> = attrib.apps;
        sync_auto_groups(pool).await?;
        let groups = load_groups(pool).await?;

        let mut app_to_group: HashMap<String, usize> = HashMap::new();
        let mut site_to_group: HashMap<String, usize> = HashMap::new();
        for (g_idx, group) in groups.iter().enumerate() {
            for member in &group.members {
                match parse_member_ref(member) {
                    MemberTarget::App(name) => {
                        app_to_group.insert(name, g_idx);
                    }
                    MemberTarget::Site(domain) => {
                        site_to_group.insert(domain, g_idx);
                    }
                }
            }
        }

        let mut grouped_site_ids: HashSet<i64> = HashSet::new();
        let mut claimed_by_browser_matrix: HashMap<i64, Vec<i64>> = HashMap::new();
        for (site_id, values) in &site_matrix {
            if values.iter().all(|v| *v <= 0) {
                continue;
            }
            let Some(site_agg) = site_aggs.get(site_id) else {
                continue;
            };
            if let Some(&g_idx) = site_to_group.get(&site_agg.domain) {
                grouped_site_ids.insert(*site_id);
                let group_app_names: HashSet<String> = groups[g_idx]
                    .members
                    .iter()
                    .filter_map(|m| match parse_member_ref(m) {
                        MemberTarget::App(name) => Some(name),
                        MemberTarget::Site(_) => None,
                    })
                    .collect();
                if let Some(per_browser) = site_by_browser_matrix.get(site_id) {
                    for (&browser_id, b_values) in per_browser {
                        let browser_in_same_group = window_apps.iter().any(|a| {
                            a.app.app_id == Some(browser_id)
                                && a.app.app_name.as_ref().is_some_and(|n| group_app_names.contains(n))
                        });
                        if !browser_in_same_group {
                            let entry = claimed_by_browser_matrix
                                .entry(browser_id)
                                .or_insert_with(|| vec![0_i64; bucket_count]);
                            for (idx, val) in b_values.iter().enumerate() {
                                entry[idx] += *val;
                            }
                        }
                    }
                }
            }
        }

        // 4. 组装行：先合并组，再未入组应用，再未入组站点
        let mut rows: Vec<EntityHoursRow> = Vec::new();

        for (g_idx, group) in groups.iter().enumerate() {
            let group_apps: Vec<&AppAgg> = window_apps
                .iter()
                .filter(|a| {
                    a.app
                        .app_name
                        .as_deref()
                        .and_then(|n| app_to_group.get(n).copied())
                        == Some(g_idx)
                })
                .collect();
            let group_sites: Vec<(i64, &SiteAgg, &Vec<i64>)> = site_matrix
                .iter()
                .filter_map(|(sid, values)| {
                    if values.iter().all(|v| *v <= 0) {
                        return None;
                    }
                    let agg = site_aggs.get(sid)?;
                    if site_to_group.get(&agg.domain).copied() == Some(g_idx) {
                        Some((*sid, agg, values))
                    } else {
                        None
                    }
                })
                .collect();

            if group_apps.is_empty() && group_sites.is_empty() {
                continue;
            }

            let group_app_ids: HashSet<i64> =
                group_apps.iter().filter_map(|a| a.app.app_id).collect();
            let mut values = vec![0_i64; bucket_count];
            for a in &group_apps {
                if let Some(app_id) = a.app.app_id {
                    if let Some(app_vals) = app_matrix.get(&app_id) {
                        for (idx, val) in app_vals.iter().enumerate() {
                            values[idx] += *val;
                        }
                    }
                }
            }
            for (sid, _, site_vals) in &group_sites {
                for idx in 0..bucket_count {
                    let mut contrib = site_vals[idx];
                    if let Some(per_browser) = site_by_browser_matrix.get(sid) {
                        for (&browser_id, b_vals) in per_browser {
                            if group_app_ids.contains(&browser_id) {
                                contrib -= b_vals[idx];
                            }
                        }
                    }
                    values[idx] += contrib.max(0);
                }
            }

            if !browser_merged {
                for a in &group_apps {
                    let app_id = a.app.app_id.unwrap_or(0);
                    if browser_app_ids.contains(&app_id) {
                        for (sid, s_vals) in &site_matrix {
                            if grouped_site_ids.contains(sid) {
                                continue;
                            }
                            if let Some(b_vals) =
                                site_by_browser_matrix.get(sid).and_then(|m| m.get(&app_id))
                            {
                                let _ = s_vals;
                                for (idx, val) in b_vals.iter().enumerate() {
                                    values[idx] = (values[idx] - *val).max(0);
                                }
                            }
                        }
                    }
                }
            }

            if values.iter().all(|v| *v <= 0) {
                continue;
            }

            let primary_target = parse_member_ref(&group.primary_ref);
            let chosen_app = match &primary_target {
                MemberTarget::App(name) => group_apps
                    .iter()
                    .copied()
                    .find(|a| a.app.app_name.as_deref() == Some(name.as_str())),
                MemberTarget::Site(_) => None,
            };
            let chosen_site = match &primary_target {
                MemberTarget::Site(domain) => group_sites
                    .iter()
                    .find(|(_, agg, _)| agg.domain == *domain)
                    .map(|(sid, agg, _)| (*sid, *agg)),
                MemberTarget::App(_) => None,
            };

            enum ChosenHoursPrimary<'a> {
                App(&'a AppAgg),
                Site(i64, &'a SiteAgg),
            }

            let chosen = if let Some(a) = chosen_app {
                ChosenHoursPrimary::App(a)
            } else if let Some((sid, s)) = chosen_site {
                ChosenHoursPrimary::Site(sid, s)
            } else {
                let best_app = group_apps.iter().copied().max_by_key(|a| a.seconds);
                let best_site = group_sites
                    .iter()
                    .max_by_key(|(_, _, vals)| vals.iter().sum::<i64>())
                    .map(|(sid, agg, vals)| (*sid, *agg, vals.iter().sum::<i64>()));
                match (best_app, best_site) {
                    (Some(a), Some((sid, s, s_sum))) => {
                        if a.seconds >= s_sum {
                            ChosenHoursPrimary::App(a)
                        } else {
                            ChosenHoursPrimary::Site(sid, s)
                        }
                    }
                    (Some(a), None) => ChosenHoursPrimary::App(a),
                    (None, Some((sid, s, _))) => ChosenHoursPrimary::Site(sid, s),
                    (None, None) => continue,
                }
            };

            let primary_app_id = match chosen {
                ChosenHoursPrimary::App(a) => a.app.app_id,
                ChosenHoursPrimary::Site(_, _) => None,
            };
            let primary_site_id = match chosen {
                ChosenHoursPrimary::App(_) => None,
                ChosenHoursPrimary::Site(sid, _) => Some(sid),
            };

            let mut members: Vec<EntityMemberModel> = Vec::new();
            for a in &group_apps {
                if a.app.app_id == primary_app_id {
                    continue;
                }
                members.push(EntityMemberModel {
                    key: format!("app:{}", a.app.app_id.unwrap_or(0)),
                    name: display_name(&a.app),
                    seconds: a.seconds,
                    icon: a.app.app_icon_file.clone(),
                });
            }
            for (sid, s, s_vals) in &group_sites {
                if Some(*sid) == primary_site_id {
                    continue;
                }
                members.push(EntityMemberModel {
                    key: format!("site:{}", sid),
                    name: site_display_name(s),
                    seconds: s_vals.iter().sum(),
                    icon: s.icon_file.clone(),
                });
            }

            match chosen {
                ChosenHoursPrimary::App(primary) => {
                    let app_id = primary.app.app_id.unwrap_or(0);
                    let is_browser = browser_app_ids.contains(&app_id);
                    if is_browser && browser_merged {
                        for (sid, per_browser) in &site_by_browser_matrix {
                            if grouped_site_ids.contains(sid) {
                                continue;
                            }
                            if let Some(b_vals) = per_browser.get(&app_id) {
                                let sum: i64 = b_vals.iter().sum();
                                if sum > 0 {
                                    if let Some(agg) = site_aggs.get(sid) {
                                        members.push(EntityMemberModel {
                                            key: format!("site:{}", sid),
                                            name: site_display_name(agg),
                                            seconds: sum,
                                            icon: agg.icon_file.clone(),
                                        });
                                    }
                                }
                            }
                        }
                    }
                    rows.push(EntityHoursRow {
                        key: format!("app:{}", app_id),
                        kind: group.kind.clone(),
                        name: group.name.clone(),
                        icon: primary.app.app_icon_file.clone(),
                        category_id: primary.app.app_category_id.unwrap_or(0),
                        category_kind: "app".to_string(),
                        category_name: primary.app.cat_name.clone(),
                        category_color: primary.app.cat_color.clone(),
                        is_browser,
                        values,
                        members,
                    });
                }
                ChosenHoursPrimary::Site(sid, primary) => {
                    rows.push(EntityHoursRow {
                        key: format!("site:{}", sid),
                        kind: group.kind.clone(),
                        name: group.name.clone(),
                        icon: primary.icon_file.clone(),
                        category_id: primary.category_id,
                        category_kind: "site".to_string(),
                        category_name: primary.category_name.clone(),
                        category_color: primary.category_color.clone(),
                        is_browser: false,
                        values,
                        members,
                    });
                }
            }
        }

        // 未入组的独立应用
        for primary in &window_apps {
            let name_key = primary.app.app_name.as_deref().unwrap_or("");
            if app_to_group.contains_key(name_key) {
                continue;
            }
            let app_id = primary.app.app_id.unwrap_or(0);
            let is_browser = browser_app_ids.contains(&app_id);

            let mut values = app_matrix
                .get(&app_id)
                .cloned()
                .unwrap_or_else(|| vec![0_i64; bucket_count]);

            if is_browser && !browser_merged {
                if let Some(credited) = browser_site_matrix.get(&app_id) {
                    for (index, value) in credited.iter().enumerate() {
                        values[index] = (values[index] - *value).max(0);
                    }
                }
                // 浏览器行在这个口径下只剩「其他页面」：全为 0 就不占一行
                if values.iter().all(|value| *value <= 0) {
                    continue;
                }
            } else if is_browser && browser_merged {
                if let Some(claimed) = claimed_by_browser_matrix.get(&app_id) {
                    for (index, value) in claimed.iter().enumerate() {
                        values[index] = (values[index] - *value).max(0);
                    }
                }
            }
            if values.iter().all(|value| *value <= 0) {
                continue;
            }

            let name = if is_browser && !browser_merged {
                other_row_name(&primary.app, browser_apps.len())
            } else {
                display_name(&primary.app)
            };

            let mut members: Vec<EntityMemberModel> = Vec::new();
            if is_browser && browser_merged {
                for (sid, per_browser) in &site_by_browser_matrix {
                    if grouped_site_ids.contains(sid) {
                        continue;
                    }
                    if let Some(b_vals) = per_browser.get(&app_id) {
                        let sum: i64 = b_vals.iter().sum();
                        if sum > 0 {
                            if let Some(agg) = site_aggs.get(sid) {
                                members.push(EntityMemberModel {
                                    key: format!("site:{}", sid),
                                    name: site_display_name(agg),
                                    seconds: sum,
                                    icon: agg.icon_file.clone(),
                                });
                            }
                        }
                    }
                }
            }

            rows.push(EntityHoursRow {
                key: if is_browser && !browser_merged {
                    format!("other:{}", app_id)
                } else {
                    format!("app:{}", app_id)
                },
                kind: if is_browser && !browser_merged { "other" } else { "app" }.to_string(),
                name,
                icon: primary.app.app_icon_file.clone(),
                // 差额行的分类跟着浏览器走（与 list 一致），否则分类维度会漏掉「其他页面」
                category_id: primary.app.app_category_id.unwrap_or(0),
                category_kind: "app".to_string(),
                category_name: primary.app.cat_name.clone(),
                category_color: primary.app.cat_color.clone(),
                is_browser,
                values,
                members,
            });
        }

        // 未入组的站点行：关闭合并时全部出现；打开合并时只留「未归属到活跃浏览器」的剩余秒数
        for (site_id, values) in &site_matrix {
            if values.iter().all(|value| *value <= 0) || grouped_site_ids.contains(site_id) {
                continue;
            }
            let emitted_values = if browser_merged {
                match site_unmatched_matrix.get(site_id) {
                    Some(unmatched_vals) => {
                        if unmatched_vals.iter().all(|v| *v <= 0) {
                            continue;
                        }
                        unmatched_vals.clone()
                    }
                    None => {
                        if attributed_sites.contains(site_id) {
                            continue;
                        }
                        values.clone()
                    }
                }
            } else {
                values.clone()
            };
            let agg = site_aggs.get(site_id);
            rows.push(EntityHoursRow {
                key: format!("site:{}", site_id),
                kind: "site".to_string(),
                name: agg.map(site_display_name).unwrap_or_default(),
                icon: agg.and_then(|agg| agg.icon_file.clone()),
                category_id: agg.map(|agg| agg.category_id).unwrap_or(0),
                category_kind: "site".to_string(),
                category_name: agg.and_then(|agg| agg.category_name.clone()),
                category_color: agg.and_then(|agg| agg.category_color.clone()),
                is_browser: false,
                values: emitted_values,
                members: Vec::new(),
            });
        }

        rows.sort_by(|a, b| {
            let sum_a: i64 = a.values.iter().sum();
            let sum_b: i64 = b.values.iter().sum();
            sum_b
                .cmp(&sum_a)
                .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
        });

        let labels = windows
            .iter()
            .filter_map(|(from, _)| {
                DateTime::from_timestamp(*from, 0).map(|dt| dt.with_timezone(&tz).to_rfc3339())
            })
            .collect();

        Ok(EntityHoursModel {
            labels,
            rows,
            browser_merged,
            bucket_count: bucket_count as i32,
        })
    }

    /// `/api/entity/summary`：指标卡所需的聚合统计（总额 / 实体数 / 会话数 / 最长单次 / 网页数）
    pub async fn summary(
        pool: &SqlitePool,
        config_service: &ConfigService,
        start: NaiveDate,
        end: NaiveDate,
        tz_id: &str,
        browser_merged: bool,
    ) -> Result<EntitySummaryModel, AppError> {
        let tz = parse_timezone(tz_id);
        let utc_start = tz_date_to_utc_range(start, &tz).0;
        let utc_end = tz_date_to_utc_range(end, &tz).1;
        let excluded = config_service.get_excluded_app_id_set(pool).await;

        let list = Self::list(pool, config_service, start, end, tz_id, browser_merged, None).await?;
        let app_net_seconds: i64 = list
            .rows
            .iter()
            .filter(|row| row.kind != "site")
            .map(|row| row.seconds)
            .sum();
        let app_count = list.rows.iter().filter(|row| row.kind != "site").count() as i32;
        let site_count = list.rows.iter().filter(|row| row.kind == "site").count() as i32;
        let top_app = list.rows.iter().find(|row| row.kind != "site").cloned();
        let top_site = list.rows.iter().find(|row| row.kind == "site").cloned();

        let (
            session_count,
            average_session_seconds,
            longest_session_seconds,
            longest_session_start,
            first_session_start,
            last_session_end,
        ) = summarize_app_sessions(pool, utc_start, utc_end, &excluded).await?;

        let (
            web_page_count,
            web_record_count,
            longest_browse_seconds,
            first_browse_start,
            last_browse_end,
        ) = summarize_browse_logs(pool, utc_start, utc_end).await?;

        Ok(EntitySummaryModel {
            total_seconds: list.total_seconds,
            app_seconds: list.app_seconds,
            app_net_seconds,
            site_seconds: list.site_seconds,
            app_count,
            site_count,
            session_count,
            average_session_seconds,
            longest_session_seconds,
            longest_session_start,
            first_session_start,
            last_session_end,
            web_page_count,
            web_record_count,
            longest_browse_seconds,
            first_browse_start,
            last_browse_end,
            top_app,
            top_site,
        })
    }
}

/// 把本地时间轴切成桶：单日=24 小时 / 区间=每天 / 整年=12 个月
fn bucket_windows(
    start: NaiveDate,
    end: NaiveDate,
    tz: &chrono_tz::Tz,
    buckets: &str,
) -> Vec<(i64, i64)> {
    let mut result: Vec<(i64, i64)> = Vec::new();
    match buckets {
        "hour" => {
            let mut day = start;
            while day <= end {
                let next_day = day.succ_opt();
                for hour in 0..24u32 {
                    let from_local = day.and_hms_opt(hour, 0, 0).unwrap();
                    let to_local = if hour == 23 {
                        next_day.unwrap().and_hms_opt(0, 0, 0).unwrap()
                    } else {
                        day.and_hms_opt(hour + 1, 0, 0).unwrap()
                    };
                    result.push((
                        tz_naive_to_utc(from_local, tz).timestamp(),
                        tz_naive_to_utc(to_local, tz).timestamp(),
                    ));
                }
                match next_day {
                    Some(next) => day = next,
                    None => break,
                }
            }
        }
        "month" => {
            for month in 1..=12u32 {
                let from_local = NaiveDate::from_ymd_opt(start.year(), month, 1)
                    .unwrap()
                    .and_hms_opt(0, 0, 0)
                    .unwrap();
                let next = if month == 12 {
                    NaiveDate::from_ymd_opt(start.year() + 1, 1, 1)
                } else {
                    NaiveDate::from_ymd_opt(start.year(), month + 1, 1)
                };
                result.push((
                    tz_naive_to_utc(from_local, tz).timestamp(),
                    tz_naive_to_utc(next.unwrap().and_hms_opt(0, 0, 0).unwrap(), tz).timestamp(),
                ));
            }
        }
        _ => {
            let mut day = start;
            while day <= end {
                let from_local = day.and_hms_opt(0, 0, 0).unwrap();
                let to_local = day.succ_opt().unwrap().and_hms_opt(0, 0, 0).unwrap();
                result.push((
                    tz_naive_to_utc(from_local, tz).timestamp(),
                    tz_naive_to_utc(to_local, tz).timestamp(),
                ));
                match day.succ_opt() {
                    Some(next) => day = next,
                    None => break,
                }
            }
        }
    }
    result
}

fn bucket_index(windows: &[(i64, i64)], timestamp: i64) -> Option<usize> {
    windows
        .iter()
        .position(|(from, to)| timestamp >= *from && timestamp < *to)
}

// ---------------------------------------------------------------- 数据装载

#[derive(Debug, Clone, sqlx::FromRow)]
struct AppAggRow {
    #[sqlx(rename = "total")]
    total: i64,
    #[sqlx(flatten)]
    app: AppJoinCols,
}

/// 不分时间范围的应用时长（用于「默认浏览器」候选列表）
async fn load_app_aggregates_all(pool: &SqlitePool) -> Result<Vec<AppAgg>, AppError> {
    let rows: Vec<AppAggRow> = sqlx::query_as(
        r#"
        SELECT a.ID AS "AppModelID", a.TotalTime AS total, a.ID AS app_id, a.Name AS app_name,
               a.Alias AS app_alias, a.Description AS app_description, a.File AS app_file,
               a.CategoryID AS app_category_id, a.IconFile AS app_icon_file, a.TotalTime AS app_total_time,
               a.Publisher AS app_publisher,
               c.ID AS cat_id, c.Name AS cat_name, c.IconFile AS cat_icon_file, c.Color AS cat_color,
               c.IsDirectoryMatch AS cat_is_directory_match, c.Directories AS cat_directories,
               c.IsUrlMatch AS cat_is_url_match, c.UrlPatterns AS cat_url_patterns, c.IsSystem AS cat_is_system
        FROM AppModels a
        LEFT JOIN CategoryModels c ON a.CategoryID = c.ID
        "#,
    )
    .fetch_all(pool)
    .await?;
    Ok(rows
        .into_iter()
        .filter_map(|row| {
            let app = row.app;
            if app.app_id.is_none() {
                return None;
            }
            Some(AppAgg {
                app,
                seconds: row.total,
            })
        })
        .collect())
}

#[derive(Debug, Clone, sqlx::FromRow)]
struct SessionRow {
    #[sqlx(rename = "AppModelID")]
    app_model_id: i64,
    #[sqlx(rename = "StartTime")]
    start_time: DateTime<Utc>,
    #[sqlx(rename = "EndTime")]
    end_time: DateTime<Utc>,
}

/// 每个应用的会话区间，已按窗口裁剪并按开始时间排序、合并重叠
async fn load_session_intervals(
    pool: &SqlitePool,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
) -> Result<HashMap<i64, Vec<(i64, i64)>>, AppError> {
    let rows: Vec<SessionRow> = sqlx::query_as(
        "SELECT AppModelID, StartTime, EndTime FROM AppSessions WHERE EndTime > ? AND StartTime < ?",
    )
    .bind(utc_start)
    .bind(utc_end)
    .fetch_all(pool)
    .await?;

    let mut by_app: HashMap<i64, Vec<(i64, i64)>> = HashMap::new();
    for row in rows {
        let start = row.start_time.timestamp().max(utc_start.timestamp());
        let end = row.end_time.timestamp().min(utc_end.timestamp());
        if end > start {
            by_app.entry(row.app_model_id).or_default().push((start, end));
        }
    }
    // 会话可能因为崩溃恢复而重叠，先合并再求交，否则同一秒会被算两次
    for intervals in by_app.values_mut() {
        intervals.sort_unstable();
        let mut merged: Vec<(i64, i64)> = Vec::with_capacity(intervals.len());
        for &(start, end) in intervals.iter() {
            match merged.last_mut() {
                Some(last) if start <= last.1 => {
                    if end > last.1 {
                        last.1 = end;
                    }
                }
                _ => merged.push((start, end)),
            }
        }
        *intervals = merged;
    }
    Ok(by_app)
}

#[derive(Debug, Clone)]
struct BrowseLogRow {
    site_id: i64,
    browser: String,
    start: DateTime<Utc>,
    end: DateTime<Utc>,
}

async fn load_browse_logs(
    pool: &SqlitePool,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
) -> Result<Vec<BrowseLogRow>, AppError> {
    #[derive(sqlx::FromRow)]
    struct Row {
        #[sqlx(rename = "SiteId")]
        site_id: i64,
        #[sqlx(rename = "Browser")]
        browser: String,
        #[sqlx(rename = "LogTime")]
        log_time: DateTime<Utc>,
        #[sqlx(rename = "Duration")]
        duration: i64,
    }

    // LogTime 是整点桶：窗口起点可能落在桶中间，往前多取一小时再按区间求交
    let rows: Vec<Row> = sqlx::query_as(
        r#"
        SELECT SiteId, COALESCE(BrowserRef, '') AS Browser, LogTime, Duration
        FROM WebBrowseLogModels
        WHERE LogTime >= ? AND LogTime < ?
        "#,
    )
    .bind(utc_start - Duration::hours(1))
    .bind(utc_end)
    .fetch_all(pool)
    .await?;

    Ok(rows
        .into_iter()
        .filter(|row| row.duration > 0)
        .map(|row| BrowseLogRow {
            site_id: row.site_id,
            browser: row.browser,
            start: row.log_time,
            end: row.log_time + Duration::seconds(row.duration),
        })
        .collect())
}

/// 汇总应用会话（与 `/api/data/sessions` 同口径：修剪微重叠、合并 <=60s 碎片、过滤 <3s；同合并组成员视作同一实体）
async fn summarize_app_sessions(
    pool: &SqlitePool,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
    excluded: &HashSet<i64>,
) -> Result<
    (
        i64,
        i64,
        i64,
        Option<DateTime<Utc>>,
        Option<DateTime<Utc>>,
        Option<DateTime<Utc>>,
    ),
    AppError,
> {
    const OVERLAP_TRIM_SECS: i64 = 2;
    const SAME_ENTITY_MERGE_GAP_SECS: i64 = 60;
    const MIN_DURATION_SECS: i64 = 3;

    let rows: Vec<SessionRow> = sqlx::query_as(
        "SELECT AppModelID, StartTime, EndTime FROM AppSessions WHERE EndTime > ? AND StartTime < ? ORDER BY StartTime ASC",
    )
    .bind(utc_start)
    .bind(utc_end)
    .fetch_all(pool)
    .await?;

    // 合并组成员的连续会话视作同一个实体
    let groups = load_groups(pool).await?;
    let mut app_to_entity: HashMap<i64, i64> = HashMap::new();
    if !groups.is_empty() {
        let app_names: Vec<(i64, String)> = sqlx::query_as("SELECT ID, Name FROM AppModels")
            .fetch_all(pool)
            .await?;
        let name_to_id: HashMap<String, i64> =
            app_names.into_iter().map(|(id, name)| (name, id)).collect();
        for group in groups
            .iter()
            .filter(|group| group.kind == "app" || group.kind == "mixed")
        {
            for member in &group.members {
                if let MemberTarget::App(name) = parse_member_ref(member) {
                    if let Some(&app_id) = name_to_id.get(&name) {
                        app_to_entity.insert(app_id, group.id);
                    }
                }
            }
        }
    }

    #[derive(Clone)]
    struct Span {
        entity_key: i64,
        start: DateTime<Utc>,
        end: DateTime<Utc>,
        duration: i64,
    }

    let raw: Vec<Span> = rows
        .into_iter()
        .filter(|row| !excluded.contains(&row.app_model_id))
        .filter_map(|row| {
            let start = row.start_time.max(utc_start);
            let end = row.end_time.min(utc_end);
            let duration = (end - start).num_seconds();
            if duration < MIN_DURATION_SECS {
                return None;
            }
            let entity_key = app_to_entity
                .get(&row.app_model_id)
                .copied()
                .unwrap_or(-row.app_model_id);
            Some(Span {
                entity_key,
                start,
                end,
                duration,
            })
        })
        .collect();

    if raw.is_empty() {
        return Ok((0, 0, 0, None, None, None));
    }

    let mut linear: Vec<Span> = Vec::with_capacity(raw.len());
    let mut iter = raw.into_iter();
    let mut current = iter.next().unwrap();
    for mut next in iter {
        let overlap = (current.end - next.start).num_seconds();
        if overlap > 0 {
            if overlap <= OVERLAP_TRIM_SECS {
                next.start = current.end;
                next.duration = (next.end - next.start).num_seconds();
                if next.duration <= 0 {
                    continue;
                }
            } else {
                current.end = next.start;
                current.duration = (current.end - current.start).num_seconds();
                if current.duration <= 0 {
                    current = next;
                    continue;
                }
            }
        }
        linear.push(current);
        current = next;
    }
    linear.push(current);

    let mut merged: Vec<Span> = Vec::with_capacity(linear.len());
    let mut iter = linear.into_iter();
    let mut current = iter.next().unwrap();
    for next in iter {
        if current.entity_key == next.entity_key {
            let gap = (next.start - current.end).num_seconds();
            if gap <= SAME_ENTITY_MERGE_GAP_SECS {
                if next.end > current.end {
                    current.end = next.end;
                }
                current.duration = (current.end - current.start).num_seconds();
                continue;
            }
        }
        merged.push(current);
        current = next;
    }
    merged.push(current);

    let count = merged.len() as i64;
    let total: i64 = merged.iter().map(|span| span.duration).sum();
    let average = if count > 0 { total / count } else { 0 };
    let first_start = merged.iter().map(|span| span.start).min();
    let last_end = merged.iter().map(|span| span.end).max();
    let longest = merged.iter().max_by_key(|span| span.duration);
    let longest_seconds = longest.map(|span| span.duration).unwrap_or(0);
    let longest_start = longest.map(|span| span.start);

    Ok((
        count,
        average,
        longest_seconds,
        longest_start,
        first_start,
        last_end,
    ))
}

async fn summarize_browse_logs(
    pool: &SqlitePool,
    utc_start: DateTime<Utc>,
    utc_end: DateTime<Utc>,
) -> Result<(i64, i64, i64, Option<DateTime<Utc>>, Option<DateTime<Utc>>), AppError> {
    #[derive(sqlx::FromRow)]
    struct Row {
        #[sqlx(rename = "UrlId")]
        url_id: i64,
        #[sqlx(rename = "LogTime")]
        log_time: DateTime<Utc>,
        #[sqlx(rename = "Duration")]
        duration: i64,
    }

    let rows: Vec<Row> = sqlx::query_as(
        r#"
        SELECT UrlId, LogTime, Duration
        FROM WebBrowseLogModels
        WHERE LogTime >= ? AND LogTime < ?
        "#,
    )
    .bind(utc_start - Duration::hours(1))
    .bind(utc_end)
    .fetch_all(pool)
    .await?;

    let mut distinct_urls: HashSet<i64> = HashSet::new();
    let mut record_count: i64 = 0;
    let mut longest_seconds: i64 = 0;
    let mut first_start: Option<DateTime<Utc>> = None;
    let mut last_end: Option<DateTime<Utc>> = None;

    for row in rows {
        if row.duration <= 0 {
            continue;
        }
        let start = row.log_time.max(utc_start);
        let end = (row.log_time + Duration::seconds(row.duration)).min(utc_end);
        let clipped = (end - start).num_seconds();
        if clipped <= 0 {
            continue;
        }
        distinct_urls.insert(row.url_id);
        record_count += 1;
        if clipped > longest_seconds {
            longest_seconds = clipped;
        }
        first_start = Some(match first_start {
            Some(prev) => prev.min(start),
            None => start,
        });
        last_end = Some(match last_end {
            Some(prev) => prev.max(end),
            None => end,
        });
    }

    Ok((
        distinct_urls.len() as i64,
        record_count,
        longest_seconds,
        first_start,
        last_end,
    ))
}

async fn fill_site_details(
    pool: &SqlitePool,
    sites: &mut HashMap<i64, SiteAgg>,
) -> Result<(), AppError> {
    if sites.is_empty() {
        return Ok(());
    }
    let ids: Vec<i64> = sites.keys().copied().collect();
    let placeholders = vec!["?"; ids.len()].join(",");
    let sql = format!(
        r#"
        SELECT ws.ID AS site_id, ws.Title AS site_title, ws.Domain AS site_domain, ws.Alias AS site_alias,
               ws.CategoryID AS site_category_id, ws.IconFile AS site_icon_file, ws.Duration AS site_duration,
               c.ID AS cat_id, c.Name AS cat_name, c.IconFile AS cat_icon_file, c.Color AS cat_color,
               c.IsUrlMatch AS cat_is_url_match, c.UrlPatterns AS cat_url_patterns, c.IsSystem AS cat_is_system
        FROM WebSiteModels ws
        LEFT JOIN CategoryModels c ON ws.CategoryID = c.ID
        WHERE ws.ID IN ({})
        "#,
        placeholders
    );
    let mut query = sqlx::query_as::<_, WebSiteJoinCols>(&sql);
    for id in &ids {
        query = query.bind(*id);
    }
    let rows = query.fetch_all(pool).await?;

    for row in rows {
        let Some(site_id) = row.site_id else { continue };
        let Some(agg) = sites.get_mut(&site_id) else {
            continue;
        };
        agg.domain = row.site_domain.clone().unwrap_or_default();
        agg.title = row.site_title.clone();
        agg.alias = row.site_alias.clone();
        agg.icon_file = row.site_icon_file.clone();
        agg.category_id = row.site_category_id.unwrap_or(0);
        agg.category_name = row.cat_name.clone();
        agg.category_color = row.cat_color.clone();
    }
    Ok(())
}

// ---------------------------------------------------------------- 合并组

const GROUP_ORDER_MAX: usize = 32;

async fn load_groups(pool: &SqlitePool) -> Result<Vec<MergeGroupModel>, AppError> {
    #[derive(sqlx::FromRow)]
    struct GroupRow {
        #[sqlx(rename = "ID")]
        id: i64,
        #[sqlx(rename = "Name")]
        name: String,
        #[sqlx(rename = "Kind")]
        kind: String,
        #[sqlx(rename = "PrimaryRef")]
        primary_ref: String,
        #[sqlx(rename = "IsAuto")]
        is_auto: i64,
    }

    let groups: Vec<GroupRow> =
        sqlx::query_as("SELECT ID, Name, Kind, PrimaryRef, IsAuto FROM EntityMergeGroups ORDER BY ID")
            .fetch_all(pool)
            .await?;

    let members: Vec<(i64, String)> =
        sqlx::query_as("SELECT GroupID, MemberRef FROM EntityMergeMembers ORDER BY GroupID, AddedAt")
            .fetch_all(pool)
            .await?;

    let mut by_group: HashMap<i64, Vec<String>> = HashMap::new();
    for (group_id, member_ref) in members {
        by_group.entry(group_id).or_default().push(member_ref);
    }

    Ok(groups
        .into_iter()
        .map(|row| MergeGroupModel {
            id: row.id,
            name: row.name,
            kind: row.kind,
            is_auto: row.is_auto != 0,
            primary_ref: row.primary_ref,
            members: by_group.remove(&row.id).unwrap_or_default(),
        })
        .collect())
}

async fn load_suppressions(pool: &SqlitePool) -> Result<HashSet<String>, AppError> {
    let rows: Vec<(String,)> = sqlx::query_as("SELECT Ref FROM EntitySuppressions")
        .fetch_all(pool)
        .await?;
    Ok(rows.into_iter().map(|(r,)| r).collect())
}

/// 自动组落库（幂等）。判定用全量应用表：只看本窗口会让「今天没跑的成员」整组消失。
async fn sync_auto_groups(pool: &SqlitePool) -> Result<(), AppError> {
    let all = load_app_aggregates_all(pool).await?;
    let no_excluded: HashSet<i64> = HashSet::new();
    let browser_ids: HashSet<i64> = resolve_browser_apps(&all, &no_excluded)
        .values()
        .copied()
        .collect();

    let manual_rows: Vec<(String,)> = sqlx::query_as(
        r#"
        SELECT m.MemberRef
        FROM EntityMergeMembers m
        INNER JOIN EntityMergeGroups g ON g.ID = m.GroupID
        WHERE g.IsAuto = 0
        "#,
    )
    .fetch_all(pool)
    .await?;
    let manual_app_names: HashSet<String> = manual_rows
        .into_iter()
        .filter_map(|(member,)| match parse_member_ref(&member) {
            MemberTarget::App(name) => Some(name),
            MemberTarget::Site(_) => None,
        })
        .collect();

    let candidates: Vec<MergeCandidate> = all
        .iter()
        .filter(|agg| !browser_ids.contains(&agg.app.app_id.unwrap_or(0)))
        .filter(|agg| {
            agg.app
                .app_name
                .as_ref()
                .is_none_or(|name| !manual_app_names.contains(name))
        })
        .map(|agg| MergeCandidate {
            name: agg.app.app_name.clone().unwrap_or_default(),
            file: agg.app.app_file.clone(),
            publisher: agg.app.app_publisher.clone(),
            seconds: agg.seconds,
        })
        .collect();

    let suppressed = load_suppressions(pool).await?;
    let auto_groups = app_merge::build_groups(&candidates, &suppressed);
    if auto_groups.is_empty() {
        return Ok(());
    }

    let existing_keys: HashSet<String> = {
        let rows: Vec<(String,)> =
            sqlx::query_as("SELECT AutoKey FROM EntityMergeGroups WHERE AutoKey IS NOT NULL")
                .fetch_all(pool)
                .await?;
        rows.into_iter().map(|(key,)| key).collect()
    };

    for group in &auto_groups {
        if existing_keys.contains(&group.key) {
            continue;
        }
        let primary = group
            .members
            .first()
            .cloned()
            .unwrap_or_else(|| group.name.clone());
        let result = sqlx::query(
            "INSERT INTO EntityMergeGroups (Name, Kind, AutoKey, PrimaryRef, IsAuto, UpdatedAt) VALUES (?, 'app', ?, ?, 1, datetime('now'))",
        )
        .bind(&group.name)
        .bind(&group.key)
        .bind(&primary)
        .execute(pool)
        .await?;
        let group_id = result.last_insert_rowid();
        for member in group.members.iter().take(GROUP_ORDER_MAX) {
            sqlx::query(
                "INSERT OR IGNORE INTO EntityMergeMembers (GroupID, MemberRef, AddedAt) VALUES (?, ?, datetime('now'))",
            )
            .bind(group_id)
            .bind(member)
            .execute(pool)
            .await?;
        }
    }

    Ok(())
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
enum MemberTarget {
    App(String),
    Site(String),
}

fn parse_member_ref(raw: &str) -> MemberTarget {
    let trimmed = raw.trim();
    if let Some(domain) = trimmed.strip_prefix("site:") {
        MemberTarget::Site(domain.trim().to_string())
    } else if let Some(name) = trimmed.strip_prefix("app:") {
        MemberTarget::App(name.trim().to_string())
    } else {
        MemberTarget::App(trimmed.to_string())
    }
}

fn canonical_member_ref(raw: &str) -> Result<(MemberTarget, String), AppError> {
    let target = parse_member_ref(raw);
    let canonical = match &target {
        MemberTarget::App(name) => {
            if name.is_empty() {
                return Err(AppError::Business("成员标识不能为空".to_string()));
            }
            format!("app:{}", name)
        }
        MemberTarget::Site(domain) => {
            if domain.is_empty() {
                return Err(AppError::Business("站点域名不能为空".to_string()));
            }
            format!("site:{}", domain)
        }
    };
    Ok((target, canonical))
}

fn normalize_members(members: &[String]) -> Result<Vec<String>, AppError> {
    let mut seen: HashSet<MemberTarget> = HashSet::new();
    let mut normalized: Vec<String> = Vec::new();
    for raw in members {
        if normalized.len() >= GROUP_ORDER_MAX {
            break;
        }
        let (target, canonical) = canonical_member_ref(raw)?;
        if seen.insert(target) {
            normalized.push(canonical);
        }
    }
    if normalized.len() < 2 {
        return Err(AppError::Business("合并组至少需要 2 个不同成员".to_string()));
    }
    Ok(normalized)
}

fn resolve_primary_ref(primary_ref: &str, members: &[String]) -> Result<String, AppError> {
    if primary_ref.trim().is_empty() {
        return Ok(members[0].clone());
    }
    let (target, canonical) = canonical_member_ref(primary_ref)?;
    if let Some(found) = members.iter().find(|m| parse_member_ref(m) == target) {
        Ok(found.clone())
    } else {
        Err(AppError::Business(format!("母成员「{}」不在成员列表内", canonical)))
    }
}

fn infer_group_kind(members: &[String]) -> String {
    let mut has_app = false;
    let mut has_site = false;
    for member in members {
        match parse_member_ref(member) {
            MemberTarget::App(_) => has_app = true,
            MemberTarget::Site(_) => has_site = true,
        }
    }
    match (has_app, has_site) {
        (true, true) => "mixed".to_string(),
        (false, true) => "site".to_string(),
        _ => "app".to_string(),
    }
}

async fn ensure_no_member_conflicts(
    pool: &SqlitePool,
    members: &[String],
    exclude_group_id: Option<i64>,
) -> Result<(), AppError> {
    let groups = load_groups(pool).await?;
    let mut occupied: HashMap<MemberTarget, String> = HashMap::new();
    for group in groups {
        if Some(group.id) == exclude_group_id {
            continue;
        }
        for m in group.members {
            occupied.insert(parse_member_ref(&m), group.name.clone());
        }
    }
    for member in members {
        let target = parse_member_ref(member);
        if let Some(group_name) = occupied.get(&target) {
            return Err(AppError::Business(format!(
                "成员已在合并组「{}」中，请先从原组移除",
                group_name
            )));
        }
    }
    Ok(())
}

async fn resolve_target_display_name(
    pool: &SqlitePool,
    target: &MemberTarget,
) -> Result<String, AppError> {
    match target {
        MemberTarget::App(name) => {
            let row: Option<(Option<String>, Option<String>)> =
                sqlx::query_as("SELECT Alias, Description FROM AppModels WHERE Name = ? LIMIT 1")
                    .bind(name)
                    .fetch_optional(pool)
                    .await?;
            if let Some((alias, desc)) = row {
                let candidate = alias
                    .as_deref()
                    .filter(|v| !v.trim().is_empty())
                    .or_else(|| desc.as_deref().filter(|v| !v.trim().is_empty()))
                    .unwrap_or(name);
                return Ok(candidate.chars().take(64).collect());
            }
            Ok(name.chars().take(64).collect())
        }
        MemberTarget::Site(domain) => {
            let row: Option<(Option<String>, Option<String>)> =
                sqlx::query_as("SELECT Alias, Title FROM WebSiteModels WHERE Domain = ? LIMIT 1")
                    .bind(domain)
                    .fetch_optional(pool)
                    .await?;
            if let Some((alias, title)) = row {
                let candidate = alias
                    .as_deref()
                    .filter(|v| !v.trim().is_empty())
                    .or_else(|| title.as_deref().filter(|v| !v.trim().is_empty()))
                    .unwrap_or(domain);
                return Ok(candidate.chars().take(64).collect());
            }
            Ok(domain.chars().take(64).collect())
        }
    }
}

/// 默认浏览器的自动推荐：浏览日志里出现最多的标识 → 只有一条就归它 → 都没就只有唯一识别到的浏览器
async fn recommend_default_browser(
    pool: &SqlitePool,
    browser_apps: &HashMap<String, i64>,
) -> Result<String, AppError> {
    let row: Option<(String,)> = sqlx::query_as(
        r#"
        SELECT BrowserRef FROM WebBrowseLogModels
        WHERE BrowserRef IS NOT NULL AND BrowserRef <> ''
        GROUP BY BrowserRef
        ORDER BY COUNT(*) DESC
        LIMIT 1
        "#,
    )
    .fetch_optional(pool)
    .await?;

    if let Some((reference,)) = row {
        return Ok(reference.trim().to_lowercase());
    }

    if browser_apps.len() == 1 {
        if let Some((reference, _)) = browser_apps.iter().next() {
            return Ok(reference.clone());
        }
    }

    Ok(String::new())
}

// ---------------------------------------------------------------- 纯函数

/// 一个区间与一串（已排序、可重叠的）会话区间的交集长度
fn overlap_seconds(intervals: &[(i64, i64)], start: i64, end: i64) -> i64 {
    let mut total = 0;
    for &(s, e) in intervals {
        if e <= start {
            continue;
        }
        if s >= end {
            break;
        }
        let from = s.max(start);
        let to = e.min(end);
        if to > from {
            total += to - from;
        }
    }
    total
}

fn display_name(app: &AppJoinCols) -> String {
    app.app_alias
        .as_deref()
        .filter(|v| !v.trim().is_empty())
        .or_else(|| app.app_description.as_deref().filter(|v| !v.trim().is_empty()))
        .or_else(|| app.app_name.as_deref())
        .unwrap_or("")
        .to_string()
}

fn site_display_name(agg: &SiteAgg) -> String {
    agg.alias
        .as_deref()
        .filter(|v| !v.trim().is_empty())
        .or_else(|| agg.title.as_deref().filter(|v| !v.trim().is_empty()))
        .unwrap_or(&agg.domain)
        .to_string()
}

fn other_row_name(app: &AppJoinCols, browser_count: usize) -> String {
    let name = display_name(app);
    if browser_count > 1 {
        format!("{} · 其他页面", name)
    } else {
        "浏览器内其他页面".to_string()
    }
}

/// 浏览器标识 → 应用 ID。同一标识族里取时长最长的进程（Chrome 本体优先于渲染器）
fn resolve_browser_apps(
    apps: &[AppAgg],
    excluded: &HashSet<i64>,
) -> HashMap<String, i64> {
    let mut resolved: HashMap<String, (i64, i64)> = HashMap::new();
    for (reference, family) in BROWSER_FAMILIES {
        for agg in apps {
            let app_id = match agg.app.app_id {
                Some(id) => id,
                None => continue,
            };
            if excluded.contains(&app_id) {
                continue;
            }
            let name = agg.app.app_name.clone().unwrap_or_default().to_lowercase();
            if name.is_empty() {
                continue;
            }
            if !family.iter().any(|stem| name.contains(stem)) {
                continue;
            }
            let entry = resolved.entry(reference.to_string()).or_insert((app_id, agg.seconds));
            if agg.seconds > entry.1 {
                *entry = (app_id, agg.seconds);
            }
        }
    }
    resolved
        .into_iter()
        .map(|(reference, (app_id, _))| (reference, app_id))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    fn clip_interval(
        start: DateTime<Utc>,
        end: DateTime<Utc>,
        window_start: i64,
        window_end: i64,
    ) -> i64 {
        let start = start.timestamp().max(window_start);
        let end = end.timestamp().min(window_end);
        (end - start).max(0)
    }

    fn ts(value: i64) -> DateTime<Utc> {
        Utc.timestamp_opt(value, 0).unwrap()
    }

    #[test]
    fn clip_respects_window() {
        assert_eq!(clip_interval(ts(10), ts(20), 0, 100), 10);
        assert_eq!(clip_interval(ts(10), ts(20), 15, 100), 5);
        assert_eq!(clip_interval(ts(10), ts(20), 0, 15), 5);
        assert_eq!(clip_interval(ts(30), ts(20), 0, 100), 0);
    }

    #[test]
    fn overlap_sums_only_intersections() {
        let intervals = vec![(0, 60), (120, 180)];
        assert_eq!(overlap_seconds(&intervals, 30, 150), 30 + 30);
        assert_eq!(overlap_seconds(&intervals, 60, 120), 0);
        assert_eq!(overlap_seconds(&intervals, -10, 200), 120);
        assert_eq!(overlap_seconds(&[], 0, 100), 0);
    }

    #[test]
    fn overlap_handles_overlapping_sessions() {
        // 重叠会话由 load_session_intervals 合并，这里只处理已合并区间
        let merged = vec![(0, 150)];
        assert_eq!(overlap_seconds(&merged, 0, 150), 150);
    }

    /// 集成测试：在内存库上跑真实迁移 + 真实 SQL。
    /// 纯函数测不出「列名写错 / 迁移没建表」这类错，只有真的查一次才知道。
    async fn test_pool() -> SqlitePool {
        let pool = sqlx::sqlite::SqlitePoolOptions::new()
            .max_connections(1)
            .connect("sqlite::memory:")
            .await
            .expect("open in-memory sqlite");
        crate::migrations::run(&pool, "Asia/Shanghai")
            .await
            .expect("run migrations");
        pool
    }

    fn test_config_dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("shijian-entity-test-{}-{}", tag, std::process::id()));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    /// 造一份最小数据：chrome 一小时，其中半小时是 example.com，另半小时是「其他页面」
    async fn seed(pool: &SqlitePool) -> (i64, i64) {
        let category_id: Option<(i64,)> = sqlx::query_as("SELECT ID FROM CategoryModels LIMIT 1")
            .fetch_optional(pool)
            .await
            .unwrap();
        let category_id = match category_id {
            Some((id,)) => (id,),
            None => {
                sqlx::query("INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsSystem) VALUES ('未分类', NULL, '#e4e3df', 0, NULL, 1)")
                    .execute(pool)
                    .await
                    .unwrap();
                sqlx::query_as("SELECT ID FROM CategoryModels LIMIT 1")
                    .fetch_one(pool)
                    .await
                    .unwrap()
            }
        };
        let app = sqlx::query(
            r#"INSERT INTO AppModels (Name, Alias, CategoryID, TotalTime, File, IconFile, Description, Publisher)
               VALUES ('chrome', NULL, ?, 3600, 'C:\Program Files\Google\Chrome\chrome.exe', NULL, 'Google Chrome', NULL)"#,
        )
        .bind(category_id.0)
        .execute(pool)
        .await
        .unwrap();
        let app_id = app.last_insert_rowid();

        sqlx::query(
            "INSERT INTO HoursLogModels (DataTime, AppModelID, Time) VALUES ('2026-11-12T04:00:00+00:00', ?, 3600)",
        )
        .bind(app_id)
        .execute(pool)
        .await
        .unwrap();

        // 会话 04:00–05:00（整点，正好覆盖浏览日志所在的小时桶）
        sqlx::query(
            "INSERT INTO AppSessions (AppModelID, StartTime, EndTime, Duration) VALUES (?, '2026-11-12T04:00:00+00:00', '2026-11-12T05:00:00+00:00', 3600)",
        )
        .bind(app_id)
        .execute(pool)
        .await
        .unwrap();

        let site = sqlx::query(
            "INSERT INTO WebSiteModels (Title, Domain, Alias, CategoryID, IconFile, Duration) VALUES ('Example', 'example.com', NULL, 0, NULL, 1800)",
        )
        .execute(pool)
        .await
        .unwrap();
        let site_id = site.last_insert_rowid();

        let url = sqlx::query("INSERT INTO WebUrlModels (Title, Url, IconFile) VALUES ('Example', 'https://example.com/', NULL)")
            .execute(pool)
            .await
            .unwrap();
        let url_id = url.last_insert_rowid();

        sqlx::query(
            "INSERT INTO WebBrowseLogModels (UrlId, LogTime, Duration, SiteId, BrowserRef) VALUES (?, '2026-11-12T04:00:00+00:00', 1800, ?, 'chrome')",
        )
        .bind(url_id)
        .bind(site_id)
        .execute(pool)
        .await
        .unwrap();

        (app_id, site_id)
    }

    #[tokio::test]
    async fn list_splits_browser_into_balance_and_sites() {
        let pool = test_pool().await;
        let (_app_id, _site_id) = seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("off"));

        let list = EntityService::list(
            &pool,
            &config,
            chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap(),
            chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap(),
            "Asia/Shanghai",
            false,
            None,
        )
        .await
        .expect("entity list");

        assert_eq!(list.app_seconds, 3600);
        assert_eq!(list.site_seconds, 1800);
        assert_eq!(list.balance_seconds, 1800);
        assert_eq!(list.total_seconds, 3600);
        assert_eq!(list.overflow_seconds, 0);

        let other = list.rows.iter().find(|row| row.kind == "other").expect("balance row");
        assert_eq!(other.seconds, 1800);
        let site = list.rows.iter().find(|row| row.kind == "site").expect("site row");
        assert_eq!(site.seconds, 1800);
        // 关闭合并时浏览器本身不占行，它的时间全在站点行与差额行里
        assert!(!list.rows.iter().any(|row| row.kind == "app"));
        assert_eq!(list.rows.iter().map(|row| row.seconds).sum::<i64>(), 3600);
    }

    #[tokio::test]
    async fn hours_matrix_subtracts_sites_from_browser() {
        let pool = test_pool().await;
        let (_app_id, _site_id) = seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("hours"));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();

        let model = EntityService::hours(&pool, &config, day, day, "Asia/Shanghai", "hour", false)
            .await
            .expect("entity hours");

        assert_eq!(model.bucket_count, 24);
        assert_eq!(model.labels.len(), 24);
        // 04:00 UTC = 12:00 本地，站点半小时、浏览器余额半小时
        let site = model.rows.iter().find(|row| row.kind == "site").expect("site row");
        assert_eq!(site.values[12], 1800);
        let other = model.rows.iter().find(|row| row.kind == "other").expect("balance row");
        assert_eq!(other.values[12], 1800);
        assert_eq!(
            model.rows.iter().flat_map(|row| row.values.iter()).sum::<i64>(),
            3600
        );
    }

    #[tokio::test]
    async fn hours_can_fold_sites_into_browser() {
        let pool = test_pool().await;
        seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("hours-merged"));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();

        let model = EntityService::hours(&pool, &config, day, day, "Asia/Shanghai", "hour", true)
            .await
            .expect("entity hours");

        assert!(!model.rows.iter().any(|row| row.kind == "site"));
        let browser = model.rows.iter().find(|row| row.is_browser).expect("browser row");
        assert_eq!(browser.values[12], 3600);
    }

    #[tokio::test]
    async fn list_take_folds_rest_into_aggregate_row() {
        let pool = test_pool().await;
        seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("take"));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();

        let list = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", false, Some(1))
            .await
            .expect("entity list");

        assert_eq!(list.rows.len(), 1);
        let row = &list.rows[0];
        assert_eq!(row.kind, "aggregate");
        assert_eq!(row.seconds, 3600);
    }

    #[tokio::test]
    async fn list_merges_sites_into_browser() {
        let pool = test_pool().await;
        let (_app_id, _site_id) = seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("on"));

        let list = EntityService::list(
            &pool,
            &config,
            chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap(),
            chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap(),
            "Asia/Shanghai",
            true,
            None,
        )
        .await
        .expect("entity list");

        assert_eq!(list.total_seconds, 3600);
        assert!(!list.rows.iter().any(|row| row.kind == "site"));

        let browser = list
            .rows
            .iter()
            .find(|row| row.is_browser)
            .expect("browser row");
        assert_eq!(browser.seconds, 3600);
        assert_eq!(browser.members.len(), 1);
        assert_eq!(browser.members[0].seconds, 1800);
    }

    #[tokio::test]
    async fn auto_groups_are_persisted_and_dissolving_sticks() {
        let pool = test_pool().await;
        let (app_id, _site_id) = seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("group"));

        // 再插一个同目录、同品牌（PiDeck）的变体，凑成一个自动组。
        // CategoryID 必须真实存在：AppModels 的外键在迁移里是开的
        let category: (i64,) = sqlx::query_as("SELECT ID FROM CategoryModels LIMIT 1")
            .fetch_one(&pool)
            .await
            .unwrap();
        let setup = sqlx::query(
            r#"INSERT INTO AppModels (Name, Alias, CategoryID, TotalTime, File, IconFile, Description, Publisher)
               VALUES ('PiDeck-0.9.5-setup', NULL, ?, 600, 'C:\Program Files\PiDeck\PiDeck-0.9.5-setup.exe', NULL, 'PiDeck', NULL)"#,
        )
        .bind(category.0)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();
        let main = sqlx::query(
            r#"INSERT INTO AppModels (Name, Alias, CategoryID, TotalTime, File, IconFile, Description, Publisher)
               VALUES ('PiDeck', NULL, ?, 900, 'C:\Program Files\PiDeck\PiDeck.exe', NULL, 'PiDeck', NULL)"#,
        )
        .bind(category.0)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        for (id, seconds) in [(main, 900_i64), (setup, 600_i64)] {
            sqlx::query(
                "INSERT INTO HoursLogModels (DataTime, AppModelID, Time) VALUES ('2026-11-12T04:00:00+00:00', ?, ?)",
            )
            .bind(id)
            .bind(seconds)
            .execute(&pool)
            .await
            .unwrap();
        }

        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();
        let list = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", false, None)
            .await
            .expect("entity list");
        let group_row = list
            .rows
            .iter()
            .find(|row| row.name == "PiDeck")
            .expect("merged group row");
        assert_eq!(group_row.seconds, 1500);
        assert_eq!(group_row.members.len(), 1);

        let groups = EntityService::merge_groups(&pool).await.expect("groups");
        let group = groups
            .groups
            .iter()
            .find(|group| group.name == "PiDeck")
            .expect("persisted group");
        assert!(group.is_auto);

        EntityService::dissolve_group(&pool, group.id)
            .await
            .expect("dissolve");

        let list = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", false, None)
            .await
            .expect("entity list after dissolve");
        assert!(!list.rows.iter().any(|row| row.name == "PiDeck" && !row.members.is_empty()));

        let groups = EntityService::merge_groups(&pool).await.expect("groups");
        assert!(groups.groups.is_empty());
        assert_eq!(groups.suppressed.len(), 1);
        let _ = app_id;
    }

    #[test]
    fn other_row_name_mentions_browser_when_ambiguous() {
        let app = AppJoinCols {
            app_id: Some(1),
            app_name: Some("chrome".to_string()),
            app_alias: None,
            app_description: Some("Google Chrome".to_string()),
            app_file: None,
            app_category_id: Some(0),
            app_icon_file: None,
            app_total_time: Some(0),
            app_publisher: None,
            cat_id: None,
            cat_name: None,
            cat_icon_file: None,
            cat_color: None,
            cat_is_directory_match: None,
            cat_directories: None,
            cat_is_url_match: None,
            cat_url_patterns: None,
            cat_is_system: None,
        };
        assert_eq!(other_row_name(&app, 1), "浏览器内其他页面");
        assert_eq!(other_row_name(&app, 3), "Google Chrome · 其他页面");
    }

    #[tokio::test]
    async fn summary_aggregates_sessions_and_browse_stats() {
        let pool = test_pool().await;
        seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("summary"));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();

        let summary = EntityService::summary(&pool, &config, day, day, "Asia/Shanghai", false)
            .await
            .expect("entity summary");

        assert_eq!(summary.total_seconds, 3600);
        assert_eq!(summary.app_seconds, 3600);
        assert_eq!(summary.app_net_seconds, 1800);
        assert_eq!(summary.site_seconds, 1800);
        assert_eq!(summary.app_count, 1);
        assert_eq!(summary.site_count, 1);
        assert_eq!(summary.session_count, 1);
        assert_eq!(summary.average_session_seconds, 3600);
        assert_eq!(summary.longest_session_seconds, 3600);
        assert_eq!(summary.web_page_count, 1);
        assert_eq!(summary.web_record_count, 1);
        assert_eq!(summary.longest_browse_seconds, 1800);
        assert!(summary.top_app.is_some());
        assert!(summary.top_site.is_some());
    }

    #[tokio::test]
    async fn manual_mixed_group_create_update_and_conservation() {
        let pool = test_pool().await;
        let (_, site_id) = seed(&pool).await;
        let desktop_cat = sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsSystem) VALUES ('DesktopCat', NULL, '#112233', 0, NULL, 0)",
        )
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();
        let site_cat = sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) VALUES ('SiteCat', NULL, '#445566', 0, '[]', 0, NULL, 0)",
        )
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        sqlx::query("UPDATE WebSiteModels SET CategoryID = ? WHERE ID = ?")
            .bind(site_cat)
            .bind(site_id)
            .execute(&pool)
            .await
            .unwrap();

        let desktop_id = sqlx::query(
            r#"INSERT INTO AppModels (Name, Alias, CategoryID, TotalTime, File, IconFile, Description, Publisher)
               VALUES ('ExampleDesktop', NULL, ?, 1200, 'C:\Apps\ExampleDesktop.exe', NULL, 'Example Desktop', NULL)"#,
        )
        .bind(desktop_cat)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        sqlx::query(
            "INSERT INTO HoursLogModels (DataTime, AppModelID, Time) VALUES ('2026-11-12T04:00:00+00:00', ?, 1200)",
        )
        .bind(desktop_id)
        .execute(&pool)
        .await
        .unwrap();

        let created = EntityService::create_group(
            &pool,
            "",
            "app:ExampleDesktop",
            &[
                "app:ExampleDesktop".to_string(),
                "site:example.com".to_string(),
            ],
        )
        .await
        .expect("create mixed group");
        assert_eq!(created.kind, "mixed");
        assert!(!created.is_auto);
        assert_eq!(created.name, "Example Desktop");
        assert_eq!(created.primary_ref, "app:ExampleDesktop");

        // 成员冲突校验
        let conflict = EntityService::create_group(
            &pool,
            "Conflict",
            "app:chrome",
            &["app:chrome".to_string(), "ExampleDesktop".to_string()],
        )
        .await;
        assert!(conflict.is_err());

        let config = ConfigService::new(&test_config_dir("mixed"));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();

        // browser_merged = true 时：混合组独占站点时长 1800s，从 chrome 扣除，总额严格守恒
        let merged = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", true, None)
            .await
            .expect("list merged");
        assert_eq!(merged.total_seconds, 4800);
        let sum_merged: i64 = merged.rows.iter().map(|r| r.seconds).sum();
        assert_eq!(sum_merged, 4800);
        let mixed_row = merged
            .rows
            .iter()
            .find(|r| r.key == format!("app:{}", desktop_id))
            .expect("mixed group row");
        assert_eq!(mixed_row.kind, "mixed");
        assert_eq!(mixed_row.category_kind, "app");
        assert_eq!(mixed_row.category_id, desktop_cat);
        assert_eq!(mixed_row.seconds, 3000);
        assert_eq!(mixed_row.members.len(), 1);
        let chrome_row = merged
            .rows
            .iter()
            .find(|r| r.name == "Google Chrome" || r.is_browser)
            .expect("chrome row");
        assert_eq!(chrome_row.seconds, 1800);

        // browser_merged = false 时总额同样守恒
        let split = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", false, None)
            .await
            .expect("list split");
        assert_eq!(split.total_seconds, 4800);
        assert_eq!(split.rows.iter().map(|r| r.seconds).sum::<i64>(), 4800);

        // hours 矩阵也保持与 list 一致
        let hours = EntityService::hours(&pool, &config, day, day, "hour", "Asia/Shanghai", true)
            .await
            .expect("hours merged");
        let mixed_hours = hours
            .rows
            .iter()
            .find(|r| r.key == format!("app:{}", desktop_id))
            .expect("mixed hours row");
        assert_eq!(mixed_hours.kind, "mixed");
        assert_eq!(mixed_hours.values.iter().sum::<i64>(), 3000);
        assert_eq!(
            hours
                .rows
                .iter()
                .map(|r| r.values.iter().sum::<i64>())
                .sum::<i64>(),
            4800
        );

        // 更新母成员为 site:example.com，分类命名空间随之切到 site
        let updated = EntityService::update_group(
            &pool,
            created.id,
            Some("Example All"),
            Some("site:example.com"),
            None,
        )
        .await
        .expect("update mixed group");
        assert_eq!(updated.name, "Example All");
        assert_eq!(updated.primary_ref, "site:example.com");

        let after_update = EntityService::list(&pool, &config, day, day, "Asia/Shanghai", true, None)
            .await
            .expect("list after update");
        let site_primary_row = after_update
            .rows
            .iter()
            .find(|r| r.key == format!("site:{}", site_id))
            .expect("site primary row");
        assert_eq!(site_primary_row.kind, "mixed");
        assert_eq!(site_primary_row.category_kind, "site");
        assert_eq!(site_primary_row.category_id, site_cat);
        assert_eq!(site_primary_row.seconds, 3000);
    }

    #[tokio::test]
    async fn multi_tab_same_hour_and_multi_day_conserve_list_hours_category_and_webdata() {
        let pool = test_pool().await;
        let (_chrome_id, _) = seed(&pool).await;
        let config = ConfigService::new(&test_config_dir("conservation-multi"));

        let ent_cat = sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) VALUES ('娱乐', NULL, '#E07A5F', 0, '[]', 0, NULL, 0)",
        )
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        let ai_cat = sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) VALUES ('AI', NULL, '#6366F1', 0, '[]', 0, NULL, 0)",
        )
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        let bilibili_site = sqlx::query(
            "INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('Bilibili', 'bilibili.com', ?, 5000)",
        )
        .bind(ent_cat)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        let chatgpt_site = sqlx::query(
            "INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('ChatGPT', 'chatgpt.com', ?, 4000)",
        )
        .bind(ai_cat)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        let ai_app = sqlx::query(
            r#"INSERT INTO AppModels (Name, Alias, CategoryID, TotalTime, File, IconFile, Description, Publisher)
               VALUES ('Cursor', NULL, ?, 1200, 'C:\Apps\Cursor.exe', NULL, 'Cursor', NULL)"#,
        )
        .bind(ai_cat)
        .execute(&pool)
        .await
        .unwrap()
        .last_insert_rowid();

        // Day 1 (2026-11-12): seed() 已有 chrome HoursLogModels(04:00Z, 3600s), AppSessions(04:00Z..05:00Z), example.com(04:00Z, 1800s).
        // 再向 04:00Z 同小时插入 Bilibili(2400s) + ChatGPT(1800s)（多标签叠加超过 3600s 预算！），
        // 以及 05:00Z（无浏览器前台会话）中的 Bilibili 后台挂机 900s（应在会话存在当天被判为 overflow=900, credited=0）。
        let bili_url1 = sqlx::query("INSERT INTO WebUrlModels (Url, Title) VALUES ('https://www.bilibili.com/video/1', 'v1')")
            .execute(&pool)
            .await
            .unwrap()
            .last_insert_rowid();
        let bili_url2 = sqlx::query("INSERT INTO WebUrlModels (Url, Title) VALUES ('https://www.bilibili.com/video/2', 'v2')")
            .execute(&pool)
            .await
            .unwrap()
            .last_insert_rowid();
        let gpt_url = sqlx::query("INSERT INTO WebUrlModels (Url, Title) VALUES ('https://chatgpt.com/c/1', 'gpt')")
            .execute(&pool)
            .await
            .unwrap()
            .last_insert_rowid();

        for (url_id, log_time, dur, site_id) in [
            (bili_url1, "2026-11-12T04:00:00+00:00", 2400_i64, bilibili_site),
            (gpt_url, "2026-11-12T04:00:00+00:00", 1800_i64, chatgpt_site),
            (bili_url2, "2026-11-12T05:00:00+00:00", 900_i64, bilibili_site),
        ] {
            sqlx::query("INSERT INTO WebBrowseLogModels (UrlId, LogTime, Duration, SiteId, BrowserRef) VALUES (?, ?, ?, ?, 'chrome')")
                .bind(url_id)
                .bind(log_time)
                .bind(dur)
                .bind(site_id)
                .execute(&pool)
                .await
                .unwrap();
        }

        sqlx::query("INSERT INTO HoursLogModels (DataTime, AppModelID, Time) VALUES ('2026-11-12T04:00:00+00:00', ?, 1200)")
            .bind(ai_app)
            .execute(&pool)
            .await
            .unwrap();

        // Day 2 (2026-11-13): 纯未对齐网站（当天浏览器无 AppSessions，unmatched=600），再加跨午夜日志从 Day1 23:30Z 跨到 Day2
        let day2_url = sqlx::query("INSERT INTO WebUrlModels (Url, Title) VALUES ('https://www.bilibili.com/video/3', 'v3')")
            .execute(&pool)
            .await
            .unwrap()
            .last_insert_rowid();
        sqlx::query("INSERT INTO WebBrowseLogModels (UrlId, LogTime, Duration, SiteId, BrowserRef) VALUES (?, '2026-11-13T15:30:00+00:00', 3600, ?, 'chrome')")
            .bind(day2_url)
            .bind(bilibili_site)
            .execute(&pool)
            .await
            .unwrap();

        let d1 = chrono::NaiveDate::from_ymd_opt(2026, 11, 12).unwrap();
        let d2 = chrono::NaiveDate::from_ymd_opt(2026, 11, 13).unwrap();

        for (start, end, buckets) in [
            (d1, d1, "hour"),
            (d1, d2, "day"),
            (d1, d2, "hour"),
        ] {
            for merged in [false, true] {
                let list = EntityService::list(&pool, &config, start, end, "Asia/Shanghai", merged, None)
                    .await
                    .expect("list");
                let hours = EntityService::hours(&pool, &config, start, end, "Asia/Shanghai", buckets, merged)
                    .await
                    .expect("hours");

                let list_rows_sum: i64 = list.rows.iter().map(|r| r.seconds).sum();
                let hours_rows_sum: i64 = hours.rows.iter().flat_map(|r| r.values.iter()).sum();

                assert_eq!(list.total_seconds, list_rows_sum, "list rows sum mismatch on {start}..{end} merged={merged}");
                assert_eq!(list.total_seconds, hours_rows_sum, "hours rows sum mismatch on {start}..{end} ({buckets}) merged={merged}");
            }
        }

        // 检查同一区间下 Bilibili（娱乐）和 AI 分类在实体列表、WebSiteDetailPage 统计接口、CategorySummaryService 中的严格一致
        let list_2d = EntityService::list(&pool, &config, d1, d2, "Asia/Shanghai", false, None).await.unwrap();
        let bilibili_entity_secs = list_2d
            .rows
            .iter()
            .find(|r| r.key == format!("site:{}", bilibili_site))
            .map(|r| r.seconds)
            .unwrap_or(0);
        let chatgpt_entity_secs = list_2d
            .rows
            .iter()
            .find(|r| r.key == format!("site:{}", chatgpt_site))
            .map(|r| r.seconds)
            .unwrap_or(0);

        // Day1: 04:00Z 预算 3600 被 example(1800) + bili(2400) + gpt(1800) = 6000 按比例分得 1080 + 1440 + 1080 = 3600
        // Day2: 2026-11-13 15:30Z = 23:30 UTC+8 (1800s 在 11/13，1800s 在 11/14 不在 d1..d2) -> Day2 unmatched = 1800
        assert_eq!(bilibili_entity_secs, 1440 + 1800);
        assert_eq!(chatgpt_entity_secs, 1080);

        let web_stats = crate::services::web_data::WebDataService::get_browse_data_statistics(
            &pool,
            d1.and_hms_opt(0, 0, 0).unwrap(),
            d2.and_hms_opt(23, 59, 59).unwrap(),
            bilibili_site,
            "Asia/Shanghai",
            &config,
        )
        .await
        .expect("web stats");
        let web_stats_total: i64 = web_stats.iter().map(|m| m.value).sum();
        assert_eq!(web_stats_total, bilibili_entity_secs);

        let web_cat_summary = crate::services::category_summary::CategorySummaryService::get_web_category_summary(
            &pool,
            ent_cat,
            d1,
            d2,
            None,
            None,
            "Asia/Shanghai",
            &config,
        )
        .await
        .expect("web cat summary");
        assert_eq!(web_cat_summary.total_seconds, bilibili_entity_secs);

        let ai_unified_summary = crate::services::category_summary::CategorySummaryService::get_unified_category_summary(
            &pool,
            ai_cat,
            d1,
            d2,
            None,
            None,
            "Asia/Shanghai",
            &config,
        )
        .await
        .expect("ai unified summary");
        assert_eq!(ai_unified_summary.total_seconds, 1200 + chatgpt_entity_secs);
    }
}
