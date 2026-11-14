use crate::constants;
use crate::db::{DEFAULT_CATEGORY, DEFAULT_COLOR, DEFAULT_ICON};
use crate::services::web_domain::{is_generic_title_word, loopback_port_key};
use anyhow::Context;
use chrono::TimeZone;
use sqlx::QueryBuilder;
use sqlx::SqlitePool;
use std::collections::HashMap;
use tracing::{info, warn};

pub(crate) const MIGRATION_STEPS: [&str; 27] = [
    "baseline",
    "add_columns",
    "rename_columns",
    "data_fixes",
    "indexes",
    "utc_hourslog",
    "utc_webbrowse",
    "utc_appduration",
    "utc_daily",
    "utc_daily_rebuild",
    "defaults",
    "fix_orphaned_categories",
    "perf_indexes",
    "app_sessions",
    "web_category_url_match",
    "web_localhost_split",
    "macos_icon_paths",
    "web_favicons_dir",
    "plugin_records",
    "app_duration_session_id",
    "web_browser_ref",
    "app_publisher",
    "entity_merge_tables",
    "unified_categories",
    "svg_category_icons",
    "preset_categories",
    "builtin_auto_category",
];

const BASELINE_SQL: &str = r#"
CREATE TABLE IF NOT EXISTS CategoryModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    IconFile TEXT,
    Color TEXT,
    IsDirectoryMatch INTEGER NOT NULL DEFAULT 0,
    Directories TEXT,
    IsSystem INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS AppModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Alias TEXT,
    Description TEXT,
    File TEXT,
    CategoryID INTEGER NOT NULL DEFAULT 0,
    IconFile TEXT,
    TotalTime INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (CategoryID) REFERENCES CategoryModels(ID)
);

CREATE TABLE IF NOT EXISTS DailyLogModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Date TEXT NOT NULL,
    AppModelID INTEGER NOT NULL,
    Time INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS HoursLogModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    DataTime TEXT NOT NULL,
    AppModelID INTEGER NOT NULL,
    Time INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS WebSiteCategoryModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    IconFile TEXT,
    Color TEXT,
    IsUrlMatch INTEGER NOT NULL DEFAULT 0,
    UrlPatterns TEXT,
    IsSystem INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS WebSiteModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT,
    Domain TEXT NOT NULL,
    Alias TEXT,
    CategoryID INTEGER NOT NULL DEFAULT 0,
    IconFile TEXT,
    Duration INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS WebUrlModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT,
    Url TEXT NOT NULL,
    IconFile TEXT
);

CREATE TABLE IF NOT EXISTS WebBrowseLogModels (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    UrlId INTEGER NOT NULL,
    LogTime TEXT NOT NULL,
    Duration INTEGER NOT NULL DEFAULT 0,
    SiteId INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS AppDurationRequests (
    SessionId TEXT NOT NULL PRIMARY KEY,
    ProcessName TEXT NOT NULL,
    StartDateTime TEXT NOT NULL,
    Duration INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS AppSessions (
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    AppModelID INTEGER NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT NOT NULL,
    Duration INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_appsessions_start ON AppSessions(StartTime);
CREATE INDEX IF NOT EXISTS idx_appsessions_app_start ON AppSessions(AppModelID, StartTime);

CREATE INDEX IF NOT EXISTS idx_appmodels_name ON AppModels(Name);
CREATE INDEX IF NOT EXISTS idx_appmodels_categoryid ON AppModels(CategoryID);
CREATE INDEX IF NOT EXISTS idx_websitemodels_domain ON WebSiteModels(Domain);
CREATE INDEX IF NOT EXISTS idx_websitemodels_categoryid ON WebSiteModels(CategoryID);
CREATE INDEX IF NOT EXISTS idx_weburlmodels_url ON WebUrlModels(Url);
CREATE INDEX IF NOT EXISTS idx_webbrowselog_logtime_siteid ON WebBrowseLogModels(LogTime, SiteId);
"#;

const DATA_FIXES_SQL: &str = r#"
UPDATE AppModels SET CategoryID = COALESCE((SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1), 0) WHERE CategoryID = 0;
UPDATE WebSiteModels SET CategoryID = COALESCE((SELECT ID FROM WebSiteCategoryModels WHERE IsSystem = 1 LIMIT 1), 0) WHERE CategoryID = 0;
UPDATE DailyLogModels SET Date = substr(Date, 1, 10) WHERE length(Date) > 10;
UPDATE WebSiteModels SET IconFile = SUBSTR(IconFile, INSTR(IconFile, 'WebFavicons' || char(92))) WHERE IconFile LIKE '%' || char(92) || 'WebFavicons' || char(92) || '%';
UPDATE WebUrlModels SET IconFile = SUBSTR(IconFile, INSTR(IconFile, 'WebFavicons' || char(92))) WHERE IconFile LIKE '%' || char(92) || 'WebFavicons' || char(92) || '%';
UPDATE WebSiteModels SET IconFile = 'WebFavicons' || char(92) || IconFile WHERE IconFile IS NOT NULL AND IconFile != '' AND IconFile NOT LIKE 'WebFavicons%';
UPDATE WebUrlModels SET IconFile = 'WebFavicons' || char(92) || IconFile WHERE IconFile IS NOT NULL AND IconFile != '' AND IconFile NOT LIKE 'WebFavicons%';
UPDATE AppModels SET IconFile = SUBSTR(IconFile, INSTR(IconFile, 'AppIcons' || char(92))) WHERE IconFile LIKE '%' || char(92) || 'AppIcons' || char(92) || '%';
"#;

const INDEXES_SQL: &str = r#"
DROP INDEX IF EXISTS idx_dailylog_date_appid;
DELETE FROM DailyLogModels WHERE EXISTS (
    SELECT 1 FROM DailyLogModels AS d2
    WHERE d2.Date = DailyLogModels.Date
    AND d2.AppModelID = DailyLogModels.AppModelID
    AND d2.ID < DailyLogModels.ID
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_dailylog_date_appid ON DailyLogModels(Date, AppModelID);

DROP INDEX IF EXISTS idx_hourslog_appid_datetime;
DELETE FROM HoursLogModels WHERE EXISTS (
    SELECT 1 FROM HoursLogModels AS h2
    WHERE h2.DataTime = HoursLogModels.DataTime
    AND h2.AppModelID = HoursLogModels.AppModelID
    AND h2.ID < HoursLogModels.ID
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_hourslog_appid_datetime ON HoursLogModels(AppModelID, DataTime);

DELETE FROM WebBrowseLogModels WHERE EXISTS (
    SELECT 1 FROM WebBrowseLogModels AS w2
    WHERE w2.LogTime = WebBrowseLogModels.LogTime
    AND w2.UrlId = WebBrowseLogModels.UrlId
    AND w2.ID < WebBrowseLogModels.ID
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_webbrowselog_logtime_urlid ON WebBrowseLogModels(LogTime, UrlId);
"#;

const PERF_INDEXES_SQL: &str = r#"
CREATE INDEX IF NOT EXISTS idx_hourslog_datetime ON HoursLogModels(DataTime);
CREATE INDEX IF NOT EXISTS idx_webbrowselog_siteid ON WebBrowseLogModels(SiteId);
ANALYZE;
"#;

/// 是否有待执行的迁移步骤（用于决定是否备份旧库）
pub(crate) async fn has_pending_migrations(pool: &SqlitePool) -> bool {
    let table_exists: Option<(i64,)> = sqlx::query_as(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='_migrations'"
    )
    .fetch_optional(pool)
    .await
    .unwrap_or(None);

    if table_exists.is_none() {
        return true; // 全新数据库，需要执行迁移
    }

    for step in MIGRATION_STEPS {
        let row: Option<(i64,)> = sqlx::query_as("SELECT 1 FROM _migrations WHERE step = ?")
            .bind(step)
            .fetch_optional(pool)
            .await
            .unwrap_or(None);
        if row.is_none() {
            return true;
        }
    }
    false
}

/// 执行全部迁移步骤（单事务，任一失败整体回滚）
pub(crate) async fn run(pool: &SqlitePool, tz_id: &str) -> anyhow::Result<()> {
    let mut tx = pool.begin().await.context("开启迁移事务失败")?;

    sqlx::query(
        "CREATE TABLE IF NOT EXISTS _migrations (
            step TEXT PRIMARY KEY,
            executed_at TEXT NOT NULL DEFAULT (datetime('now'))
        )"
    )
    .execute(&mut *tx)
    .await?;

    if !is_done(&mut tx, "baseline").await? {
        execute_batch(&mut tx, BASELINE_SQL).await.context("基线表结构")?;
        mark_done(&mut tx, "baseline").await?;
    }

    if !is_done(&mut tx, "add_columns").await? {
        add_column_if_missing(&mut tx, "CategoryModels", "IsSystem", "INTEGER NOT NULL DEFAULT 0").await?;
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "IsSystem", "INTEGER NOT NULL DEFAULT 0").await?;
        mark_done(&mut tx, "add_columns").await?;
    }

    if !is_done(&mut tx, "rename_columns").await? {
        rename_column_if_exists(&mut tx, "CategoryModels", "IsDirectoryMath", "IsDirectoryMatch").await?;
        mark_done(&mut tx, "rename_columns").await?;
    }

    if !is_done(&mut tx, "data_fixes").await? {
        execute_batch(&mut tx, DATA_FIXES_SQL).await.context("数据修复")?;
        mark_done(&mut tx, "data_fixes").await?;
    }

    if !is_done(&mut tx, "indexes").await? {
        execute_batch(&mut tx, INDEXES_SQL).await.context("索引重建")?;
        mark_done(&mut tx, "indexes").await?;
    }

    if !is_done(&mut tx, "utc_hourslog").await? {
        convert_local_to_utc(&mut tx, "HoursLogModels", "DataTime", "ID", tz_id).await?;
        mark_done(&mut tx, "utc_hourslog").await?;
    }

    if !is_done(&mut tx, "utc_webbrowse").await? {
        convert_local_to_utc(&mut tx, "WebBrowseLogModels", "LogTime", "ID", tz_id).await?;
        mark_done(&mut tx, "utc_webbrowse").await?;
    }

    if !is_done(&mut tx, "utc_appduration").await? {
        convert_local_to_utc(&mut tx, "AppDurationRequests", "StartDateTime", "rowid", tz_id).await?;
        mark_done(&mut tx, "utc_appduration").await?;
    }

    // DailyLogModels.Date 在 C# 时代为本地日期，直接按 00:00 转 UTC 会导致日期错位
    // 此处改为基于 HoursLogModels（已转为 UTC）按 UTC 日期重新聚合重建
    if !is_done(&mut tx, "utc_daily").await? {
        rebuild_daily_log_from_hours(&mut tx).await?;
        mark_done(&mut tx, "utc_daily").await?;
    }

    // 对已经执行过旧版 utc_daily 的数据库进行修复，重新从 HoursLog 重建
    if !is_done(&mut tx, "utc_daily_rebuild").await? {
        rebuild_daily_log_from_hours(&mut tx).await?;
        mark_done(&mut tx, "utc_daily_rebuild").await?;
    }

    if !is_done(&mut tx, "defaults").await? {
        ensure_default_categories(&mut tx).await?;
        mark_done(&mut tx, "defaults").await?;
    }

    // data_fixes 运行 early 阶段时系统分类尚未创建，需在此处补修孤立记录
    if !is_done(&mut tx, "fix_orphaned_categories").await? {
        sqlx::query(
            "UPDATE AppModels SET CategoryID = (SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1) WHERE CategoryID IS NULL OR CategoryID = 0 OR CategoryID NOT IN (SELECT ID FROM CategoryModels)"
        )
        .execute(&mut *tx)
        .await?;

        sqlx::query(
            "UPDATE WebSiteModels SET CategoryID = (SELECT ID FROM WebSiteCategoryModels WHERE IsSystem = 1 LIMIT 1) WHERE CategoryID IS NULL OR CategoryID = 0 OR CategoryID NOT IN (SELECT ID FROM WebSiteCategoryModels)"
        )
        .execute(&mut *tx)
        .await?;

        mark_done(&mut tx, "fix_orphaned_categories").await?;
    }

    if !is_done(&mut tx, "perf_indexes").await? {
        execute_batch(&mut tx, PERF_INDEXES_SQL).await.context("性能优化索引")?;
        mark_done(&mut tx, "perf_indexes").await?;
    }

    if !is_done(&mut tx, "app_sessions").await? {
        sqlx::query(
            r#"
            CREATE TABLE IF NOT EXISTS AppSessions (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                AppModelID INTEGER NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NOT NULL,
                Duration INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_appsessions_start ON AppSessions(StartTime);
            CREATE INDEX IF NOT EXISTS idx_appsessions_app_start ON AppSessions(AppModelID, StartTime);
            "#
        )
        .execute(&mut *tx)
        .await
        .context("创建 AppSessions 表")?;
        mark_done(&mut tx, "app_sessions").await?;
    }

    // 网页分类 URL 匹配规则字段
    if !is_done(&mut tx, "web_category_url_match").await? {
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "IsUrlMatch", "INTEGER NOT NULL DEFAULT 0").await?;
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "UrlPatterns", "TEXT").await?;
        mark_done(&mut tx, "web_category_url_match").await?;
    }

    // 回环地址（本地开发应用）按端口拆分独立站点
    if !is_done(&mut tx, "web_localhost_split").await? {
        split_localhost_sites(&mut tx).await?;
        mark_done(&mut tx, "web_localhost_split").await?;
    }

    // macOS 图标路径规范化（迁到 Application Support，只执行一次）
    if !is_done(&mut tx, "macos_icon_paths").await? {
        #[cfg(target_os = "macos")]
        migrate_legacy_macos_icon_paths(&mut tx).await?;
        mark_done(&mut tx, "macos_icon_paths").await?;
    }

    if !is_done(&mut tx, "web_favicons_dir").await? {
        #[cfg(target_os = "macos")]
        migrate_legacy_web_favicons_dir().await?;
        mark_done(&mut tx, "web_favicons_dir").await?;
    }

    // 插件记录 KV 表
    if !is_done(&mut tx, "plugin_records").await? {
        execute_batch(
            &mut tx,
            r#"
            CREATE TABLE IF NOT EXISTS PluginRecords (
                PluginID  TEXT NOT NULL,
                RecordKey TEXT NOT NULL,
                Value     TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (PluginID, RecordKey)
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_records_updated ON PluginRecords(UpdatedAt);
            "#,
        )
        .await
        .context("创建 PluginRecords 表")?;
        mark_done(&mut tx, "plugin_records").await?;
    }

    if !is_done(&mut tx, "app_duration_session_id").await? {
        execute_batch(
            &mut tx,
            r#"
            DROP TABLE IF EXISTS AppDurationRequests;
            CREATE TABLE AppDurationRequests (
                SessionId TEXT NOT NULL PRIMARY KEY,
                ProcessName TEXT NOT NULL,
                StartDateTime TEXT NOT NULL,
                Duration INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_appduration_start ON AppDurationRequests(StartDateTime);
            CREATE INDEX IF NOT EXISTS idx_appduration_process_start ON AppDurationRequests(ProcessName, StartDateTime);
            "#,
        )
        .await
        .context("重建 AppDurationRequests 会话主键")?;
        mark_done(&mut tx, "app_duration_session_id").await?;
    }

    // 站点记录的浏览器归属（扩展构建期注入 Browser 字段）；历史行保持 NULL，读取时落「默认浏览器」
    if !is_done(&mut tx, "web_browser_ref").await? {
        add_column_if_missing(&mut tx, "WebBrowseLogModels", "BrowserRef", "TEXT").await?;
        mark_done(&mut tx, "web_browser_ref").await?;
    }

    // 应用的发布者（monitor 读文件版本资源 CompanyName）；历史行 NULL，按路径规则退化
    if !is_done(&mut tx, "app_publisher").await? {
        add_column_if_missing(&mut tx, "AppModels", "Publisher", "TEXT").await?;
        mark_done(&mut tx, "app_publisher").await?;
    }

    // 子母程序合并组（N6 / N2）。合并只在查询期生效，原始表一行不改
    if !is_done(&mut tx, "entity_merge_tables").await? {
        execute_batch(
            &mut tx,
            r#"
            CREATE TABLE IF NOT EXISTS EntityMergeGroups (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL DEFAULT 'app',
                AutoKey TEXT,
                PrimaryRef TEXT NOT NULL,
                IsAuto INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_entity_merge_autokey
                ON EntityMergeGroups(AutoKey) WHERE AutoKey IS NOT NULL;
            CREATE TABLE IF NOT EXISTS EntityMergeMembers (
                GroupID INTEGER NOT NULL,
                MemberRef TEXT NOT NULL,
                OverrideCategoryRef TEXT,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (GroupID, MemberRef)
            );
            CREATE TABLE IF NOT EXISTS EntitySuppressions (
                Ref TEXT NOT NULL PRIMARY KEY,
                Reason TEXT,
                CreatedAt TEXT NOT NULL
            );
            "#,
        )
        .await
        .context("创建子母程序合并组表")?;
        mark_done(&mut tx, "entity_merge_tables").await?;
    }

    if !is_done(&mut tx, "unified_categories").await? {
        add_column_if_missing(
            &mut tx,
            "CategoryModels",
            "IsUrlMatch",
            "INTEGER NOT NULL DEFAULT 0",
        )
        .await?;
        add_column_if_missing(&mut tx, "CategoryModels", "UrlPatterns", "TEXT").await?;
        unify_categories(&mut tx).await.context("统一应用与网站分类表")?;
        mark_done(&mut tx, "unified_categories").await?;
    }

    if !is_done(&mut tx, "svg_category_icons").await? {
        migrate_category_icons_to_svg(&mut tx)
            .await
            .context("迁移分类图标至 SVG 图标库")?;
        mark_done(&mut tx, "svg_category_icons").await?;
    }

    if !is_done(&mut tx, "preset_categories").await? {
        seed_preset_categories(&mut tx)
            .await
            .context("初始化预设常用分类")?;
        mark_done(&mut tx, "preset_categories").await?;
    }

    if !is_done(&mut tx, "builtin_auto_category").await? {
        let updated = crate::services::category::CategoryService::backfill_builtin_categories_tx(&mut tx)
            .await
            .map_err(|e| anyhow::anyhow!(e.to_string()))
            .context("执行内置自动归类回填")?;
        info!(updated = updated, "已为未分类应用与网站套用内置自动归类规则");
        mark_done(&mut tx, "builtin_auto_category").await?;
    }

    tx.commit().await.context("提交迁移事务失败")?;
    crate::services::category::invalidate_category_cache().await;
    info!("数据库迁移完成");
    Ok(())
}

async fn split_localhost_sites(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    // 收集所有回环站点
    let loopback_sites: Vec<(i64, String, i64)> = sqlx::query_as(
        "SELECT ID, Domain, CategoryID FROM WebSiteModels \
         WHERE Domain = 'localhost' OR Domain = '127.0.0.1' OR Domain = '[::1]' \
            OR Domain LIKE 'localhost:%' OR Domain LIKE '127.0.0.1:%' OR Domain LIKE '[::1]:%'"
    )
    .fetch_all(&mut **tx)
    .await?;

    if loopback_sites.is_empty() {
        info!("web_localhost_split: 无回环站点，跳过");
        return Ok(());
    }

    let mut created = 0usize;
    let mut moved_logs = 0u64;

    for (site_id, site_domain, category_id) in loopback_sites {
        // URL 与站点的关联通过 WebBrowseLogModels.SiteId 建立（WebUrlModels 本身无 SiteId 列），
        // 因此取该站点下有浏览日志的 URL，并附带各自的累计浏览时长
        let urls: Vec<(i64, String, String, i64)> = sqlx::query_as(
            r#"
            SELECT wu.ID, wu.Url, wu.Title, COALESCE(SUM(wb.Duration), 0)
            FROM WebUrlModels wu
            JOIN WebBrowseLogModels wb ON wb.UrlId = wu.ID
            WHERE wb.SiteId = ?
            GROUP BY wu.ID, wu.Url, wu.Title
            ORDER BY wu.ID
            "#
        )
        .bind(site_id)
        .fetch_all(&mut **tx)
        .await?;

        // 按 host:port 分组：key -> (url_id, title, duration)
        let mut groups: HashMap<String, Vec<(i64, String, i64)>> = HashMap::new();
        for (url_id, url, title, duration) in urls {
            if let Some(key) = loopback_port_key(&url) {
                if key != site_domain {
                    groups.entry(key).or_default().push((url_id, title, duration));
                }
            }
        }

        for (port_key, url_group) in groups {
            // 已有同域名独立站点的直接复用，否则新建
            let target_id: Option<i64> = sqlx::query_scalar(
                "SELECT ID FROM WebSiteModels WHERE Domain = ? LIMIT 1"
            )
            .bind(&port_key)
            .fetch_optional(&mut **tx)
            .await?;

            let target_id = match target_id {
                Some(id) => id,
                None => {
                    // 标题：取组内浏览时长最长的非占位标题，否则回退为 host:port
                    let title = url_group
                        .iter()
                        .filter(|(_, t, _)| !is_placeholder_title(t))
                        .max_by_key(|(_, _, d)| *d)
                        .map(|(_, t, _)| t.clone())
                        .unwrap_or_else(|| port_key.clone());

                    // 新站点时长：组内 URL 的累计浏览时长之和
                    let duration_sum: i64 = url_group.iter().map(|(_, _, d)| *d).sum();

                    let id = sqlx::query(
                        "INSERT INTO WebSiteModels (Title, Domain, Alias, CategoryID, IconFile, Duration) \
                         VALUES (?, ?, NULL, ?, NULL, ?)"
                    )
                    .bind(&title)
                    .bind(&port_key)
                    .bind(category_id)
                    .bind(duration_sum)
                    .execute(&mut **tx)
                    .await?
                    .last_insert_rowid();

                    created += 1;
                    id
                }
            };

            // 将该组 URL 在此站点下的浏览日志迁移到目标站点
            let mut qb = QueryBuilder::new("UPDATE WebBrowseLogModels SET SiteId = ");
            qb.push_bind(target_id);
            qb.push(" WHERE SiteId = ");
            qb.push_bind(site_id);
            qb.push(" AND UrlId IN (");
            let mut sep = qb.separated(", ");
            for url_id in url_group.iter().map(|(id, _, _)| *id) {
                sep.push_bind(url_id);
            }
            sep.push_unseparated(")");
            let moved = qb.build().execute(&mut **tx).await?.rows_affected();
            moved_logs += moved;
        }

        // 原站点若已无浏览日志则删除
        let remaining: i64 =
            sqlx::query_scalar("SELECT COUNT(*) FROM WebBrowseLogModels WHERE SiteId = ?")
                .bind(site_id)
                .fetch_one(&mut **tx)
                .await?;
        if remaining == 0 {
            sqlx::query("DELETE FROM WebSiteModels WHERE ID = ?")
                .bind(site_id)
                .execute(&mut **tx)
                .await?;
            info!("web_localhost_split: 删除已清空的旧站点 {}", site_domain);
        }
    }

    info!(
        "web_localhost_split: 新建 {} 个站点，迁移 {} 条浏览日志",
        created, moved_logs
    );
    Ok(())
}

/// 占位标题（浏览器新标签页等无意义标题）回退为 host:port
fn is_placeholder_title(title: &str) -> bool {
    let t = title.trim().to_ascii_lowercase();
    t.is_empty() || is_generic_title_word(&t) || t.starts_with("app.")
}

async fn is_done(tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>, step: &str) -> anyhow::Result<bool> {
    let row: Option<(i64,)> = sqlx::query_as("SELECT 1 FROM _migrations WHERE step = ?")
        .bind(step)
        .fetch_optional(&mut **tx)
        .await?;
    Ok(row.is_some())
}

async fn mark_done(tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>, step: &str) -> anyhow::Result<()> {
    sqlx::query("INSERT OR IGNORE INTO _migrations (step) VALUES (?)")
        .bind(step)
        .execute(&mut **tx)
        .await?;
    Ok(())
}

async fn execute_batch(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
    sql: &str,
) -> anyhow::Result<()> {
    for stmt in sql.split(';').map(str::trim).filter(|s| !s.is_empty()) {
        sqlx::query(stmt)
            .execute(&mut **tx)
            .await
            .with_context(|| format!("SQL 执行失败: {}", stmt))?;
    }
    Ok(())
}

async fn add_column_if_missing(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
    table: &str,
    column: &str,
    definition: &str,
) -> anyhow::Result<()> {
    let exists = sqlx::query(
        "SELECT 1 FROM pragma_table_info(?) WHERE name = ?"
    )
    .bind(table)
    .bind(column)
    .fetch_optional(&mut **tx)
    .await?
    .is_some();

    if !exists {
        let sql = format!("ALTER TABLE {} ADD COLUMN {} {}", table, column, definition);
        sqlx::query(&sql).execute(&mut **tx).await?;
        info!("表 {} 添加列 {}", table, column);
    }
    Ok(())
}

async fn rename_column_if_exists(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
    table: &str,
    old_name: &str,
    new_name: &str,
) -> anyhow::Result<()> {
    let exists = sqlx::query(
        "SELECT 1 FROM pragma_table_info(?) WHERE name = ?"
    )
    .bind(table)
    .bind(old_name)
    .fetch_optional(&mut **tx)
    .await?
    .is_some();

    if exists {
        let sql = format!("ALTER TABLE {} RENAME COLUMN {} TO {}", table, old_name, new_name);
        sqlx::query(&sql).execute(&mut **tx).await?;
        info!("表 {} 列 {} -> {} 重命名完成", table, old_name, new_name);
    }
    Ok(())
}

/// 将本地时间字符串（2025-06-04 21:00:00）转为 UTC RFC 3339（2025-06-04T13:00:00+00:00）
/// 按 ID 分批处理，每批 1000 条，已含 T 的行自动跳过
async fn convert_local_to_utc(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
    table: &str,
    column: &str,
    id_column: &str,
    tz_id: &str,
) -> anyhow::Result<()> {
    let tz: chrono_tz::Tz = tz_id.parse()
        .map_err(|_| anyhow::anyhow!("无效时区: {}", tz_id))?;

    let utc_count: i64 = sqlx::query_scalar(
        &format!("SELECT COUNT(*) FROM {} WHERE {} LIKE '%T%'", table, column)
    )
    .fetch_one(&mut **tx)
    .await?;

    if utc_count > 0 {
        info!("表 {} 已包含 {} 条 UTC 格式数据，仅转换剩余旧格式", table, utc_count);
    }

    let total: i64 = sqlx::query_scalar(&format!("SELECT COUNT(*) FROM {}", table))
        .fetch_one(&mut **tx)
        .await?;

    if total == 0 {
        return Ok(());
    }

    info!("转换表 {}.{}（{} 条记录）...", table, column, total);

    let batch_size = constants::MIGRATION_BATCH_SIZE;
    let mut converted = 0;
    let mut last_id = 0i64;

    loop {
        let rows: Vec<(i64, String)> = sqlx::query_as(&format!(
            "SELECT {}, {} FROM {} WHERE {} > ? AND {} NOT LIKE '%T%' ORDER BY {} LIMIT {}",
            id_column, column, table, id_column, column, id_column, batch_size
        ))
        .bind(last_id)
        .fetch_all(&mut **tx)
        .await?;

        if rows.is_empty() {
            break;
        }

        let batch_count = rows.len();
        for (id, time_str) in rows {
            let naive = match chrono::NaiveDateTime::parse_from_str(&time_str, "%Y-%m-%d %H:%M:%S")
                .or_else(|_| chrono::NaiveDateTime::parse_from_str(&time_str, "%Y-%m-%d %H:%M:%S%.f"))
            {
                Ok(dt) => dt,
                Err(e) => {
                    warn!("表 {} 行 {} 时间 '{}' 解析失败: {}，跳过", table, id, time_str, e);
                    last_id = id;
                    continue;
                }
            };

            let utc_str = match tz.from_local_datetime(&naive) {
                chrono::LocalResult::Single(dt) => {
                    dt.with_timezone(&chrono::Utc)
                        .to_rfc3339_opts(chrono::SecondsFormat::Secs, false)
                }
                chrono::LocalResult::Ambiguous(earliest, _) => {
                    earliest
                        .with_timezone(&chrono::Utc)
                        .to_rfc3339_opts(chrono::SecondsFormat::Secs, false)
                }
                chrono::LocalResult::None => {
                    warn!(
                        "表 {} 时间 '{}' 在目标时区 {} 不存在（DST 跳变），按 UTC 处理",
                        table, time_str, tz_id
                    );
                    chrono::Utc
                        .from_utc_datetime(&naive)
                        .to_rfc3339_opts(chrono::SecondsFormat::Secs, false)
                }
            };

            sqlx::query(&format!(
                "UPDATE {} SET {} = ? WHERE {} = ?",
                table, column, id_column
            ))
            .bind(&utc_str)
            .bind(id)
            .execute(&mut **tx)
            .await?;

            last_id = id;
        }

        converted += batch_count;
        info!("表 {} 已处理 {} / {} 条", table, converted, total);
    }

    info!("表 {} 转换完成: {} 条", table, converted);
    Ok(())
}

/// 从 HoursLogModels 按 UTC 日期聚合重建 DailyLogModels
/// 适用于 C# 旧数据迁移：HoursLog 已转为 UTC 后，按 UTC 日期重新生成 DailyLog
/// 该操作是幂等的
async fn rebuild_daily_log_from_hours(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    let before_count: i64 = sqlx::query_scalar("SELECT COUNT(*) FROM DailyLogModels")
        .fetch_one(&mut **tx)
        .await?;

    info!("从 HoursLog 重建 DailyLog（当前 {} 条）...", before_count);

    sqlx::query("DELETE FROM DailyLogModels")
        .execute(&mut **tx)
        .await?;

    let inserted = sqlx::query(
        r#"
        INSERT INTO DailyLogModels (Date, AppModelID, Time)
        SELECT substr(DataTime, 1, 10), AppModelID, SUM(Time)
        FROM HoursLogModels
        GROUP BY substr(DataTime, 1, 10), AppModelID
        "#
    )
    .execute(&mut **tx)
    .await?
    .rows_affected();

    info!("DailyLog 重建完成: 清理 {} 条旧记录，插入 {} 条新记录", before_count, inserted);
    Ok(())
}

async fn ensure_default_categories(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    sqlx::query(
        "INSERT OR IGNORE INTO CategoryModels (Name, IconFile, Color, Directories, IsDirectoryMatch, IsSystem) VALUES (?, ?, ?, '[]', 0, 1)"
    )
    .bind(DEFAULT_CATEGORY)
    .bind(DEFAULT_ICON)
    .bind(DEFAULT_COLOR)
    .execute(&mut **tx)
    .await?;

    sqlx::query(
        "INSERT OR IGNORE INTO WebSiteCategoryModels (Name, IconFile, Color, IsSystem) VALUES (?, ?, ?, 1)"
    )
    .bind(DEFAULT_CATEGORY)
    .bind(DEFAULT_ICON)
    .bind(DEFAULT_COLOR)
    .execute(&mut **tx)
    .await?;

    sqlx::query("UPDATE CategoryModels SET IsSystem = 1 WHERE Name = ? AND IsSystem = 0")
        .bind(DEFAULT_CATEGORY)
        .execute(&mut **tx)
        .await?;

    sqlx::query("UPDATE WebSiteCategoryModels SET IsSystem = 1 WHERE Name = ? AND IsSystem = 0")
        .bind(DEFAULT_CATEGORY)
        .execute(&mut **tx)
        .await?;

    Ok(())
}

#[cfg(target_os = "macos")]
async fn migrate_legacy_macos_icon_paths(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    use sqlx::Row;
    use std::path::Path;

    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::path::PathBuf::from("/Applications/ShiJianTools"));

    let icons_dir = crate::constants::default_data_dir().join("AppIcons");
    std::fs::create_dir_all(&icons_dir)
        .with_context(|| format!("create icon dir {}", icons_dir.display()))?;

    let legacy_sources = [
        exe_dir.join("AppIcons"),
        dirs::cache_dir()
            .unwrap_or_else(|| std::path::PathBuf::from("/tmp"))
            .join("ShiJian")
            .join("Icons"),
    ];

    let rows = sqlx::query(
        "SELECT ID, IconFile FROM AppModels \
         WHERE IconFile LIKE 'AppIcons/%' OR IconFile LIKE '%Caches/ShiJian/Icons/%'",
    )
    .fetch_all(&mut **tx)
    .await?;

    let mut normalized = 0usize;
    let mut cleared = 0usize;
    for row in &rows {
        let id: i64 = row.get("ID");
        let icon_file: String = row.get("IconFile");

        let Some(file_name) = Path::new(&icon_file)
            .file_name()
            .and_then(|n| n.to_str())
            .filter(|n| n.ends_with(".png"))
        else {
            continue;
        };

        let target = icons_dir.join(file_name);
        if !target.exists() {
            let mut recovered = false;
            for src in &legacy_sources {
                let candidate = src.join(file_name);
                if candidate.exists() && std::fs::copy(&candidate, &target).is_ok() {
                    recovered = true;
                    break;
                }
            }
            if !recovered {
                sqlx::query("UPDATE AppModels SET IconFile = NULL WHERE ID = ?")
                    .bind(id)
                    .execute(&mut **tx)
                    .await?;
                cleared += 1;
                continue;
            }
        }

        let normalized_path = format!("AppIcons/{file_name}");
        if normalized_path != icon_file {
            sqlx::query("UPDATE AppModels SET IconFile = ? WHERE ID = ?")
                .bind(&normalized_path)
                .bind(id)
                .execute(&mut **tx)
                .await?;
        }
        normalized += 1;
    }

    info!(
        "normalized {} macOS icon paths (cleared {} unrecoverable)",
        normalized, cleared
    );
    Ok(())
}

#[cfg(target_os = "macos")]
async fn migrate_legacy_web_favicons_dir() -> anyhow::Result<()> {
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::path::PathBuf::from("/Applications/ShiJianTools"));

    let legacy_dir = exe_dir.join("WebFavicons");
    if !legacy_dir.is_dir() {
        return Ok(());
    }

    let target_dir = crate::constants::default_data_dir().join("WebFavicons");
    std::fs::create_dir_all(&target_dir)
        .with_context(|| format!("create favicons dir {}", target_dir.display()))?;

    let mut copied = 0usize;
    for entry in std::fs::read_dir(&legacy_dir)
        .with_context(|| format!("read legacy favicons dir {}", legacy_dir.display()))?
    {
        let entry = entry?;
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let Some(file_name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        let target = target_dir.join(file_name);
        if !target.exists() && std::fs::copy(&path, &target).is_ok() {
            copied += 1;
        }
    }

    info!("migrated {copied} web favicons to Application Support");
    Ok(())
}

/// Matches `CategoryIconCatalog` order in `ShiJian.Client`.
const SVG_ICON_KEYS: [&str; 32] = [
    "svg:code",
    "svg:briefcase",
    "svg:sparkles",
    "svg:gamepad",
    "svg:message",
    "svg:book",
    "svg:palette",
    "svg:film",
    "svg:wrench",
    "svg:tag",
    "svg:inbox",
    "svg:terminal",
    "svg:globe",
    "svg:music",
    "svg:camera",
    "svg:headphones",
    "svg:shopping",
    "svg:heart",
    "svg:star",
    "svg:compass",
    "svg:cloud",
    "svg:shield",
    "svg:file",
    "svg:clock",
    "svg:rocket",
    "svg:zap",
    "svg:chart",
    "svg:folder",
    "svg:cpu",
    "svg:coffee",
    "svg:layers",
    "svg:lightbulb",
];

const PRESET_CATEGORIES: [(&str, &str, &str); 9] = [
    ("开发", "svg:code", "#2563EB"),
    ("办公", "svg:briefcase", "#0D9488"),
    ("AI", "svg:sparkles", "#8B5CF6"),
    ("娱乐", "svg:gamepad", "#D97706"),
    ("社交", "svg:message", "#EC4899"),
    ("学习", "svg:book", "#16A34A"),
    ("设计", "svg:palette", "#0284C7"),
    ("影音", "svg:film", "#E11D48"),
    ("工具", "svg:wrench", "#64748B"),
];

fn merge_json_string_arrays(a: Option<&str>, b: Option<&str>) -> String {
    let mut result: Vec<String> = Vec::new();
    let mut seen = std::collections::HashSet::<String>::new();
    for src in [a, b].into_iter().flatten() {
        if let Ok(items) = serde_json::from_str::<Vec<String>>(src) {
            for item in items {
                let trimmed = item.trim();
                if trimmed.is_empty() {
                    continue;
                }
                let key = trimmed.to_lowercase();
                if seen.insert(key) {
                    result.push(trimmed.to_string());
                }
            }
        }
    }
    serde_json::to_string(&result).unwrap_or_else(|_| "[]".to_string())
}

fn is_blank_or_default_icon(icon: Option<&str>) -> bool {
    match icon.map(str::trim) {
        None | Some("") => true,
        Some("avares://ShiJian/Resources/Icons/shijian.ico") => true,
        _ => false,
    }
}

fn is_blank_or_default_color(color: Option<&str>) -> bool {
    match color.map(str::trim) {
        None | Some("") => true,
        Some(c) if c.eq_ignore_ascii_case(DEFAULT_COLOR) => true,
        _ => false,
    }
}

async fn unify_categories(tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>) -> anyhow::Result<()> {
    // Ensure unified system category exists in CategoryModels
    let sys_cat_id: i64 = match sqlx::query_scalar::<_, i64>(
        "SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1",
    )
    .fetch_optional(&mut **tx)
    .await?
    {
        Some(id) => id,
        None => {
            if let Some(id) = sqlx::query_scalar::<_, i64>(
                "SELECT ID FROM CategoryModels WHERE LOWER(TRIM(Name)) = LOWER(?) LIMIT 1",
            )
            .bind(DEFAULT_CATEGORY)
            .fetch_optional(&mut **tx)
            .await?
            {
                sqlx::query("UPDATE CategoryModels SET IsSystem = 1 WHERE ID = ?")
                    .bind(id)
                    .execute(&mut **tx)
                    .await?;
                id
            } else {
                sqlx::query(
                    "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) \
                     VALUES (?, ?, ?, 0, '[]', 0, '[]', 1)",
                )
                .bind(DEFAULT_CATEGORY)
                .bind(DEFAULT_ICON)
                .bind(DEFAULT_COLOR)
                .execute(&mut **tx)
                .await?
                .last_insert_rowid()
            }
        }
    };

    let has_web_cat_table: Option<(i64,)> = sqlx::query_as(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='WebSiteCategoryModels'",
    )
    .fetch_optional(&mut **tx)
    .await?;
    if has_web_cat_table.is_none() {
        return Ok(());
    }

    #[derive(sqlx::FromRow, Clone)]
    struct CatRow {
        #[sqlx(rename = "ID")]
        id: i64,
        #[sqlx(rename = "Name")]
        name: Option<String>,
        #[sqlx(rename = "IconFile")]
        icon_file: Option<String>,
        #[sqlx(rename = "Color")]
        color: Option<String>,
        #[sqlx(rename = "IsUrlMatch")]
        is_url_match: bool,
        #[sqlx(rename = "UrlPatterns")]
        url_patterns: Option<String>,
        #[sqlx(rename = "IsSystem")]
        is_system: bool,
    }

    let mut unified_cats: Vec<CatRow> = sqlx::query_as(
        "SELECT ID, Name, IconFile, Color, IsUrlMatch, UrlPatterns, IsSystem FROM CategoryModels ORDER BY ID ASC",
    )
    .fetch_all(&mut **tx)
    .await?;

    let web_cats: Vec<CatRow> = sqlx::query_as(
        "SELECT ID, Name, IconFile, Color, IsUrlMatch, UrlPatterns, IsSystem FROM WebSiteCategoryModels ORDER BY ID ASC",
    )
    .fetch_all(&mut **tx)
    .await?;

    let mut web_to_unified: HashMap<i64, i64> = HashMap::new();
    let mut valid_ids: std::collections::HashSet<i64> =
        unified_cats.iter().map(|c| c.id).collect();

    for wc in web_cats {
        let wc_name = wc.name.as_deref().unwrap_or("").trim().to_string();
        if wc.is_system || wc_name == DEFAULT_CATEGORY || wc_name.is_empty() {
            web_to_unified.insert(wc.id, sys_cat_id);
            continue;
        }

        let name_key = wc_name.to_lowercase();
        if let Some(existing) = unified_cats
            .iter_mut()
            .find(|c| !c.is_system && c.name.as_deref().unwrap_or("").trim().to_lowercase() == name_key)
        {
            let merged_patterns = merge_json_string_arrays(
                existing.url_patterns.as_deref(),
                wc.url_patterns.as_deref(),
            );
            let merged_url_match = existing.is_url_match || wc.is_url_match;
            let new_icon = if is_blank_or_default_icon(existing.icon_file.as_deref())
                && !is_blank_or_default_icon(wc.icon_file.as_deref())
            {
                wc.icon_file.clone()
            } else {
                existing.icon_file.clone()
            };
            let new_color = if is_blank_or_default_color(existing.color.as_deref())
                && !is_blank_or_default_color(wc.color.as_deref())
            {
                wc.color.clone()
            } else {
                existing.color.clone()
            };

            sqlx::query(
                "UPDATE CategoryModels SET IconFile = ?, Color = ?, IsUrlMatch = ?, UrlPatterns = ? WHERE ID = ?",
            )
            .bind(&new_icon)
            .bind(&new_color)
            .bind(merged_url_match)
            .bind(&merged_patterns)
            .bind(existing.id)
            .execute(&mut **tx)
            .await?;

            existing.icon_file = new_icon;
            existing.color = new_color;
            existing.is_url_match = merged_url_match;
            existing.url_patterns = Some(merged_patterns);

            web_to_unified.insert(wc.id, existing.id);
        } else {
            let new_id = sqlx::query(
                "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) \
                 VALUES (?, ?, ?, 0, '[]', ?, ?, 0)",
            )
            .bind(&wc_name)
            .bind(&wc.icon_file)
            .bind(&wc.color)
            .bind(wc.is_url_match)
            .bind(&wc.url_patterns)
            .execute(&mut **tx)
            .await?
            .last_insert_rowid();

            valid_ids.insert(new_id);
            web_to_unified.insert(wc.id, new_id);
            unified_cats.push(CatRow {
                id: new_id,
                name: Some(wc_name),
                icon_file: wc.icon_file,
                color: wc.color,
                is_url_match: wc.is_url_match,
                url_patterns: wc.url_patterns,
                is_system: false,
            });
        }
    }

    // Rewrite WebSiteModels.CategoryID row-by-row to avoid transitive ID collisions.
    let sites: Vec<(i64, i64)> = sqlx::query_as("SELECT ID, CategoryID FROM WebSiteModels")
        .fetch_all(&mut **tx)
        .await?;
    let mut updated_sites = 0usize;
    for (site_id, old_cat_id) in sites {
        let target_id = web_to_unified
            .get(&old_cat_id)
            .copied()
            .filter(|id| valid_ids.contains(id))
            .unwrap_or(sys_cat_id);
        if target_id != old_cat_id {
            sqlx::query("UPDATE WebSiteModels SET CategoryID = ? WHERE ID = ?")
                .bind(target_id)
                .bind(site_id)
                .execute(&mut **tx)
                .await?;
            updated_sites += 1;
        }
    }

    info!(
        "分类体系统一完成: 映射 {} 条网站分类，更新 {} 条网站归属",
        web_to_unified.len(),
        updated_sites
    );
    Ok(())
}

fn map_legacy_emoji_to_svg(icon: &str) -> Option<&'static str> {
    let lower = icon.to_ascii_lowercase();
    let pos = lower.find("emoji/(")?;
    let rest = &icon[pos + "emoji/(".len()..];
    let end = rest.find(").png").or_else(|| rest.find(").PNG"))?;
    let idx: usize = rest[..end].parse().ok()?;
    if idx == 0 {
        return Some("svg:tag");
    }
    Some(SVG_ICON_KEYS[(idx - 1) % SVG_ICON_KEYS.len()])
}

fn default_svg_for_category_name(name: &str, is_system: bool) -> &'static str {
    if is_system {
        return DEFAULT_ICON;
    }
    for (preset_name, icon, _) in PRESET_CATEGORIES {
        if name.trim().eq_ignore_ascii_case(preset_name) {
            return icon;
        }
    }
    "svg:tag"
}

async fn migrate_category_icons_to_svg(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    let rows: Vec<(i64, Option<String>, Option<String>, bool)> =
        sqlx::query_as("SELECT ID, Name, IconFile, IsSystem FROM CategoryModels")
            .fetch_all(&mut **tx)
            .await?;

    let mut migrated = 0usize;
    for (id, name, icon_file, is_system) in rows {
        let raw = icon_file.as_deref().unwrap_or("").trim();
        if raw.starts_with("svg:") {
            continue;
        }

        let target = if raw.is_empty() || raw == "avares://ShiJian/Resources/Icons/shijian.ico" {
            Some(default_svg_for_category_name(
                name.as_deref().unwrap_or(""),
                is_system,
            ))
        } else {
            map_legacy_emoji_to_svg(raw)
        };

        if let Some(svg_key) = target {
            sqlx::query("UPDATE CategoryModels SET IconFile = ? WHERE ID = ?")
                .bind(svg_key)
                .bind(id)
                .execute(&mut **tx)
                .await?;
            migrated += 1;
        }
    }

    info!("分类图标 SVG 迁移完成: 更新 {} 条记录", migrated);
    Ok(())
}

async fn seed_preset_categories(
    tx: &mut sqlx::Transaction<'_, sqlx::Sqlite>,
) -> anyhow::Result<()> {
    let existing_names: Vec<(String,)> =
        sqlx::query_as("SELECT Name FROM CategoryModels WHERE Name IS NOT NULL")
            .fetch_all(&mut **tx)
            .await?;
    let mut seen: std::collections::HashSet<String> = existing_names
        .into_iter()
        .map(|(n,)| n.trim().to_lowercase())
        .collect();

    let mut inserted = 0usize;
    for (name, icon, color) in PRESET_CATEGORIES {
        if seen.contains(&name.to_lowercase()) {
            continue;
        }
        sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns, IsSystem) \
             VALUES (?, ?, ?, 0, '[]', 0, '[]', 0)",
        )
        .bind(name)
        .bind(icon)
        .bind(color)
        .execute(&mut **tx)
        .await?;
        seen.insert(name.to_lowercase());
        inserted += 1;
    }

    info!("预设常用分类初始化完成: 新增 {} 个", inserted);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::services::category::CategoryService;
    use crate::services::web_data::WebDataService;
    use crate::services::category_summary::CategorySummaryService;
    use crate::services::config::ConfigService;
    use sqlx::sqlite::SqlitePoolOptions;

    async fn mem_pool() -> SqlitePool {
        SqlitePoolOptions::new()
            .max_connections(1)
            .connect(":memory:")
            .await
            .unwrap()
    }

    #[tokio::test]
    async fn unified_migration_merges_web_categories_migrates_svg_and_seeds_presets() {
        let pool = mem_pool().await;
        // Build legacy schema & run steps up through entity_merge_tables before seeding legacy data
        sqlx::query(
            "CREATE TABLE IF NOT EXISTS _migrations (step TEXT PRIMARY KEY, executed_at TEXT NOT NULL DEFAULT (datetime('now')))",
        )
        .execute(&pool)
        .await
        .unwrap();
        let mut tx = pool.begin().await.unwrap();
        execute_batch(&mut tx, BASELINE_SQL).await.unwrap();
        add_column_if_missing(&mut tx, "WebBrowseLogModels", "BrowserRef", "TEXT").await.unwrap();
        add_column_if_missing(&mut tx, "AppModels", "Publisher", "TEXT").await.unwrap();
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "IsSystem", "INTEGER NOT NULL DEFAULT 0").await.unwrap();
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "IsUrlMatch", "INTEGER NOT NULL DEFAULT 0").await.unwrap();
        add_column_if_missing(&mut tx, "WebSiteCategoryModels", "UrlPatterns", "TEXT").await.unwrap();
        ensure_default_categories(&mut tx).await.unwrap();

        // Simulate legacy user categories:
        // App category "开发" using legacy Emoji (2).png + directory match
        let dev_app_id = sqlx::query(
            "INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsSystem) VALUES ('开发', 'avares://ShiJian/Resources/Emoji/(2).png', '#111111', 1, '[\"C:/Code\"]', 0)",
        )
        .execute(&mut *tx)
        .await
        .unwrap()
        .last_insert_rowid();

        // Web category with SAME name "开发" + URL match rules
        let dev_web_id = sqlx::query(
            "INSERT INTO WebSiteCategoryModels (Name, IconFile, Color, IsUrlMatch, UrlPatterns, IsSystem) VALUES ('开发', 'avares://ShiJian/Resources/Emoji/(1).png', '#222222', 1, '[\"*.github.com\"]', 0)",
        )
        .execute(&mut *tx)
        .await
        .unwrap()
        .last_insert_rowid();

        // Web-only category "阅读"
        let read_web_id = sqlx::query(
            "INSERT INTO WebSiteCategoryModels (Name, IconFile, Color, IsUrlMatch, UrlPatterns, IsSystem) VALUES ('阅读', 'avares://ShiJian/Resources/Emoji/(6).png', '#10B981', 1, '[\"*.medium.com\"]', 0)",
        )
        .execute(&mut *tx)
        .await
        .unwrap()
        .last_insert_rowid();

        // System web category
        let sys_web_id: i64 = sqlx::query_scalar("SELECT ID FROM WebSiteCategoryModels WHERE IsSystem = 1 LIMIT 1")
            .fetch_one(&mut *tx)
            .await
            .unwrap();

        let s1 = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('GitHub', 'github.com', ?, 100)")
            .bind(dev_web_id).execute(&mut *tx).await.unwrap().last_insert_rowid();
        let s2 = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('Medium', 'medium.com', ?, 50)")
            .bind(read_web_id).execute(&mut *tx).await.unwrap().last_insert_rowid();
        let s3 = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('Other', 'other.test', ?, 10)")
            .bind(sys_web_id).execute(&mut *tx).await.unwrap().last_insert_rowid();
        let s4 = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('Orphan', 'orphan.test', 9999, 5)")
            .execute(&mut *tx).await.unwrap().last_insert_rowid();

        for step in &MIGRATION_STEPS[..MIGRATION_STEPS.len() - 4] {
            mark_done(&mut tx, step).await.unwrap();
        }
        tx.commit().await.unwrap();

        // Run migration for unified_categories + svg_category_icons + preset_categories + builtin_auto_category
        run(&pool, "Asia/Shanghai").await.unwrap();

        let cats = CategoryService::get_categories(&pool).await.unwrap();
        let dev = cats.iter().find(|c| c.name.as_deref() == Some("开发")).expect("dev merged");
        assert_eq!(dev.id, dev_app_id);
        assert!(dev.is_directory_match);
        assert!(dev.is_url_match);
        assert_eq!(dev.url_patterns.as_deref(), Some("[\"*.github.com\"]"));
        assert_eq!(dev.icon_file.as_deref(), Some("svg:briefcase")); // Emoji/(2) -> index 1 => svg:briefcase

        let read = cats.iter().find(|c| c.name.as_deref() == Some("阅读")).expect("web-only migrated");
        assert!(read.is_url_match);
        assert_eq!(read.icon_file.as_deref(), Some("svg:book")); // Emoji/(6) -> index 5 => svg:book

        // Check WebSiteModels reassigned accurately
        let c1: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(s1).fetch_one(&pool).await.unwrap();
        let c2: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(s2).fetch_one(&pool).await.unwrap();
        let c3: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(s3).fetch_one(&pool).await.unwrap();
        let c4: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(s4).fetch_one(&pool).await.unwrap();
        let sys_id = CategoryService::get_system_category_id(&pool).await.unwrap();
        assert_eq!(c1, dev_app_id);
        assert_eq!(c2, read.id);
        assert_eq!(c3, sys_id);
        assert_eq!(c4, sys_id);

        // System category icon migrated from shijian.ico / default to svg:inbox
        let sys_cat = cats.iter().find(|c| c.is_system).unwrap();
        assert_eq!(sys_cat.icon_file.as_deref(), Some("svg:inbox"));

        // Presets exist without duplicating existing "开发"
        assert_eq!(cats.iter().filter(|c| c.name.as_deref() == Some("开发")).count(), 1);
        assert!(cats.iter().any(|c| c.name.as_deref() == Some("AI") && c.icon_file.as_deref() == Some("svg:sparkles")));
        assert!(cats.iter().any(|c| c.name.as_deref() == Some("工具") && c.icon_file.as_deref() == Some("svg:wrench")));

        // Delete "阅读" -> deleting again won't resurrect after rerun & moves s2 to sys_id
        CategoryService::delete_category(&pool, read.id).await.unwrap();
        let c2_after: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(s2).fetch_one(&pool).await.unwrap();
        assert_eq!(c2_after, sys_id);
        run(&pool, "Asia/Shanghai").await.unwrap();
        let cats_after = CategoryService::get_categories(&pool).await.unwrap();
        assert!(!cats_after.iter().any(|c| c.name.as_deref() == Some("阅读")));

        // WebDataService::get_web_site_categories returns unified categories too
        let web_cats = WebDataService::get_web_site_categories(&pool).await.unwrap();
        assert!(web_cats.iter().any(|c| c.id == dev.id && c.name == "开发"));

        // Unified category summary works when apps & sites share dev.id
        let cfg = ConfigService::new(&std::env::temp_dir().join(format!("shijian_test_{}", std::process::id())));
        let day = chrono::NaiveDate::from_ymd_opt(2026, 11, 13).unwrap();
        let summary = CategorySummaryService::get_unified_category_summary(
            &pool, dev.id, day, day, None, None, "Asia/Shanghai", &cfg,
        ).await.unwrap();
        assert_eq!(summary.category_name, "开发");
    }

    #[tokio::test]
    async fn builtin_auto_category_backfills_uncategorized_and_preserves_manual() {
        let pool = mem_pool().await;
        run(&pool, "Asia/Shanghai").await.unwrap();

        let sys_id = CategoryService::get_system_category_id(&pool).await.unwrap();
        let cats: Vec<crate::models::category::CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels")
                .fetch_all(&pool)
                .await
                .unwrap();
        let ent_id = cats.iter().find(|c| c.name.as_deref() == Some("娱乐")).unwrap().id;
        let study_id = cats.iter().find(|c| c.name.as_deref() == Some("学习")).unwrap().id;
        let ai_id = cats.iter().find(|c| c.name.as_deref() == Some("AI")).unwrap().id;

        // 1. Delete preset "AI" before migration runs again -> AI-matched items must stay uncategorized
        sqlx::query("DELETE FROM CategoryModels WHERE ID = ?")
            .bind(ai_id)
            .execute(&pool)
            .await
            .unwrap();

        // 2. Insert test rows & simulate builtin_auto_category hasn't run yet
        sqlx::query("DELETE FROM _migrations WHERE step = 'builtin_auto_category'")
            .execute(&pool)
            .await
            .unwrap();

        let app_code = sqlx::query("INSERT INTO AppModels (Name, CategoryID, TotalTime) VALUES ('Code.exe', ?, 10)")
            .bind(sys_id).execute(&pool).await.unwrap().last_insert_rowid();
        let app_manual = sqlx::query("INSERT INTO AppModels (Name, CategoryID, TotalTime) VALUES ('winword', ?, 10)")
            .bind(study_id).execute(&pool).await.unwrap().last_insert_rowid();
        let site_bili = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('Bilibili', 'www.bilibili.com', ?, 10)")
            .bind(sys_id).execute(&pool).await.unwrap().last_insert_rowid();
        let site_chatgpt = sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID, Duration) VALUES ('ChatGPT', 'chatgpt.com', ?, 10)")
            .bind(sys_id).execute(&pool).await.unwrap().last_insert_rowid();

        run(&pool, "Asia/Shanghai").await.unwrap();

        let dev_id = cats.iter().find(|c| c.name.as_deref() == Some("开发")).unwrap().id;
        let c_code: i64 = sqlx::query_scalar("SELECT CategoryID FROM AppModels WHERE ID = ?").bind(app_code).fetch_one(&pool).await.unwrap();
        let c_manual: i64 = sqlx::query_scalar("SELECT CategoryID FROM AppModels WHERE ID = ?").bind(app_manual).fetch_one(&pool).await.unwrap();
        let c_bili: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(site_bili).fetch_one(&pool).await.unwrap();
        let c_gpt: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE ID = ?").bind(site_chatgpt).fetch_one(&pool).await.unwrap();

        assert_eq!(c_code, dev_id);
        assert_eq!(c_manual, study_id); // user's manual choice preserved
        assert_eq!(c_bili, ent_id);
        assert_eq!(c_gpt, sys_id); // deleted AI category is not resurrected
    }
}
