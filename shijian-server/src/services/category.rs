use sqlx::SqlitePool;
use std::collections::HashMap;
use tokio::sync::RwLock;
use tracing::{debug, info};

use crate::error::AppError;
use crate::models::category::CategoryModel;
use crate::models::request::{CreateCategoryRequest, UpdateCategoryRequest};
use crate::services::auto_category;

static CATEGORY_CACHE: std::sync::OnceLock<RwLock<Option<Vec<CategoryModel>>>> = std::sync::OnceLock::new();
static DIR_MATCH_CACHE: std::sync::OnceLock<RwLock<Option<Vec<(i64, Vec<String>)>>>> = std::sync::OnceLock::new();

fn category_cache() -> &'static RwLock<Option<Vec<CategoryModel>>> {
    CATEGORY_CACHE.get_or_init(|| RwLock::new(None))
}

fn dir_match_cache() -> &'static RwLock<Option<Vec<(i64, Vec<String>)>>> {
    DIR_MATCH_CACHE.get_or_init(|| RwLock::new(None))
}

pub(crate) async fn invalidate_category_cache() {
    let mut cache = category_cache().write().await;
    *cache = None;
    let mut dir_cache = dir_match_cache().write().await;
    *dir_cache = None;
    crate::services::web_data::invalidate_web_category_caches().await;
}

pub struct CategoryService;

impl CategoryService {
    pub async fn get_system_category_id<'e, E>(executor: E) -> Result<i64, AppError>
    where
        E: sqlx::Executor<'e, Database = sqlx::Sqlite>,
    {
        let id: Option<(i64,)> =
            sqlx::query_as("SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1")
                .fetch_optional(executor)
                .await?;

        match id {
            Some((id,)) => Ok(id),
            None => Err(AppError::Internal(
                "System category not found".to_string(),
            )),
        }
    }

    pub async fn get_categories(pool: &SqlitePool) -> Result<Vec<CategoryModel>, AppError> {
        debug!("get_categories");

        {
            let cache = category_cache().read().await;
            if let Some(cached) = cache.as_ref() {
                debug!("get_categories from cache");
                return Ok(cached.clone());
            }
        }

        let mut categories: Vec<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE IsSystem = 0")
                .fetch_all(pool)
                .await?;

        let system: Option<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE IsSystem = 1 LIMIT 1")
                .fetch_optional(pool)
                .await?;

        let result = if let Some(sys) = system {
            let mut all = vec![sys];
            all.append(&mut categories);
            all
        } else {
            categories
        };

        let mut cache = category_cache().write().await;
        *cache = Some(result.clone());

        Ok(result)
    }

    pub async fn get_category(pool: &SqlitePool, id: i64) -> Result<Option<CategoryModel>, AppError> {
        debug!("get_category: id={}", id);
        let category: Option<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE ID = ?")
                .bind(id)
                .fetch_optional(pool)
                .await?;

        Ok(category)
    }

    pub async fn create_category(
        pool: &SqlitePool,
        req: CreateCategoryRequest,
    ) -> Result<CategoryModel, AppError> {
        info!("create_category: name={}", req.name);
        let id = sqlx::query(
            r#"
            INSERT INTO CategoryModels (Name, IconFile, Color, IsDirectoryMatch, Directories, IsUrlMatch, UrlPatterns)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            "#,
        )
        .bind(&req.name)
        .bind(&req.icon_file)
        .bind(&req.color)
        .bind(req.is_directory_match)
        .bind(&req.directories)
        .bind(req.is_url_match)
        .bind(&req.url_patterns)
        .execute(pool)
        .await?
        .last_insert_rowid();

        invalidate_category_cache().await;

        Ok(CategoryModel {
            id,
            name: Some(req.name),
            icon_file: req.icon_file,
            color: req.color,
            is_directory_match: req.is_directory_match,
            directories: req.directories,
            is_url_match: req.is_url_match,
            url_patterns: req.url_patterns,
            is_system: false,
        })
    }

    pub async fn update_category(
        pool: &SqlitePool,
        id: i64,
        req: UpdateCategoryRequest,
    ) -> Result<(), AppError> {
        info!("update_category: id={}", id);
        let existing: Option<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE ID = ?")
                .bind(id)
                .fetch_optional(pool)
                .await?;

        let Some(existing) = existing else {
            return Err(AppError::Business("分类不存在".to_string()));
        };

        if existing.is_system {
            let req_dirs = req.directories.as_deref().filter(|s| !s.is_empty() && *s != "[]");
            let existing_dirs = existing.directories.as_deref().filter(|s| !s.is_empty() && *s != "[]");
            if req.is_directory_match != existing.is_directory_match || req_dirs != existing_dirs {
                return Err(AppError::Business(
                    "系统分类不能修改目录匹配规则".to_string(),
                ));
            }
            let req_urls = req.url_patterns.as_deref().filter(|s| !s.is_empty() && *s != "[]");
            let existing_urls = existing.url_patterns.as_deref().filter(|s| !s.is_empty() && *s != "[]");
            if req.is_url_match != existing.is_url_match || req_urls != existing_urls {
                return Err(AppError::Business(
                    "系统分类不能修改 URL 匹配规则".to_string(),
                ));
            }

            sqlx::query(
                "UPDATE CategoryModels SET Name = ?, IconFile = ?, Color = ? WHERE ID = ?",
            )
            .bind(&req.name)
            .bind(&req.icon_file)
            .bind(&req.color)
            .bind(id)
            .execute(pool)
            .await?;
        } else {
            sqlx::query(
                r#"
                UPDATE CategoryModels
                SET Name = ?, IconFile = ?, Color = ?, IsDirectoryMatch = ?, Directories = ?, IsUrlMatch = ?, UrlPatterns = ?
                WHERE ID = ?
                "#,
            )
            .bind(&req.name)
            .bind(&req.icon_file)
            .bind(&req.color)
            .bind(req.is_directory_match)
            .bind(&req.directories)
            .bind(req.is_url_match)
            .bind(&req.url_patterns)
            .bind(id)
            .execute(pool)
            .await?;
        }

        invalidate_category_cache().await;

        Ok(())
    }

    pub async fn restore_system_category(
        pool: &SqlitePool,
        id: i64,
    ) -> Result<CategoryModel, AppError> {
        info!("restore_system_category: id={}", id);

        let existing: Option<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE ID = ?")
                .bind(id)
                .fetch_optional(pool)
                .await?;

        let Some(existing) = existing else {
            return Err(AppError::Business("分类不存在".to_string()));
        };

        if !existing.is_system {
            return Err(AppError::Business("不是系统分类".to_string()));
        }

        sqlx::query(
            "UPDATE CategoryModels SET Name = ?, IconFile = ?, Color = ? WHERE ID = ?",
        )
        .bind(crate::db::DEFAULT_CATEGORY)
        .bind(crate::db::DEFAULT_ICON)
        .bind(crate::db::DEFAULT_COLOR)
        .bind(id)
        .execute(pool)
        .await?;

        invalidate_category_cache().await;

        Ok(CategoryModel {
            id,
            name: Some(crate::db::DEFAULT_CATEGORY.to_string()),
            icon_file: Some(crate::db::DEFAULT_ICON.to_string()),
            color: Some(crate::db::DEFAULT_COLOR.to_string()),
            is_directory_match: false,
            directories: Some("[]".to_string()),
            is_url_match: false,
            url_patterns: Some("[]".to_string()),
            is_system: true,
        })
    }

    pub async fn delete_category(pool: &SqlitePool, id: i64) -> Result<(), AppError> {
        info!("delete_category: id={}", id);

        let mut tx = pool.begin().await?;

        let existing: Option<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE ID = ?")
                .bind(id)
                .fetch_optional(&mut *tx)
                .await?;

        let Some(existing) = existing else {
            return Err(AppError::Business("分类不存在".to_string()));
        };

        if existing.is_system {
            return Err(AppError::Business("系统分类不能删除".to_string()));
        }

        let app_count: i64 =
            sqlx::query_scalar("SELECT COUNT(*) FROM AppModels WHERE CategoryID = ?")
                .bind(id)
                .fetch_one(&mut *tx)
                .await?;

        let site_count: i64 =
            sqlx::query_scalar("SELECT COUNT(*) FROM WebSiteModels WHERE CategoryID = ?")
                .bind(id)
                .fetch_one(&mut *tx)
                .await?;

        if app_count > 0 || site_count > 0 {
            let system_category_id: i64 =
                sqlx::query_scalar("SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1")
                    .fetch_one(&mut *tx)
                    .await?;

            if app_count > 0 {
                sqlx::query("UPDATE AppModels SET CategoryID = ? WHERE CategoryID = ?")
                    .bind(system_category_id)
                    .bind(id)
                    .execute(&mut *tx)
                    .await?;
            }

            if site_count > 0 {
                sqlx::query("UPDATE WebSiteModels SET CategoryID = ? WHERE CategoryID = ?")
                    .bind(system_category_id)
                    .bind(id)
                    .execute(&mut *tx)
                    .await?;
            }
        }

        sqlx::query("DELETE FROM CategoryModels WHERE ID = ?")
            .bind(id)
            .execute(&mut *tx)
            .await?;

        tx.commit().await?;
        invalidate_category_cache().await;

        Ok(())
    }

    pub async fn match_category_by_path(pool: &SqlitePool, file_path: Option<&str>) -> Option<i64> {
        let file_path = file_path?;
        if file_path.is_empty() {
            return None;
        }

        {
            let cache = dir_match_cache().read().await;
            if let Some(cached) = cache.as_ref() {
                return match_against_rules(cached, file_path);
            }
        }

        let categories = Self::get_categories(pool).await.ok()?;
        let rules = build_match_rules(&categories);
        let result = match_against_rules(&rules, file_path);

        let mut cache = dir_match_cache().write().await;
        *cache = Some(rules);

        result
    }

   
    pub async fn apply_directory_match(pool: &SqlitePool, patterns: Option<Vec<String>>) -> Result<usize, AppError> {
        let rules = if let Some(ref p) = patterns {
            if p.is_empty() {
                return Ok(0);
            }
            let categories = Self::get_categories(pool).await?;
            let category_map: std::collections::HashMap<i64, &CategoryModel> = categories
                .iter()
                .filter(|c| c.is_directory_match && !c.is_system)
                .map(|c| (c.id, c))
                .collect();

            let mut result: Vec<(i64, Vec<String>)> = Vec::new();
            for cat_id in category_map.keys() {
                if let Some(cat) = category_map.get(cat_id) {
                    if let Some(dirs_str) = &cat.directories {
                        if let Ok(dirs) = serde_json::from_str::<Vec<String>>(dirs_str) {
                            let matched: Vec<String> = dirs
                                .into_iter()
                                .map(|d| d.trim().trim_end_matches(['/', '\\']).to_lowercase())
                                .filter(|d| !d.is_empty())
                                .filter(|d| p.iter().any(|p| d == &p.to_lowercase()))
                                .collect();
                            if !matched.is_empty() {
                                result.push((*cat_id, matched));
                            }
                        }
                    }
                }
            }
            result
        } else {
            let categories = Self::get_categories(pool).await?;
            build_match_rules(&categories)
        };

        if rules.is_empty() {
            return Ok(0);
        }

        let apps: Vec<(i64, Option<String>)> = sqlx::query_as(
            "SELECT ID, File FROM AppModels WHERE File IS NOT NULL"
        )
        .fetch_all(pool)
        .await?;

        let mut to_update: Vec<(i64, i64)> = Vec::new();
        for (app_id, file) in &apps {
            if let Some(path) = file {
                if let Some(cat_id) = match_against_rules(&rules, path) {
                    to_update.push((*app_id, cat_id));
                }
            }
        }

        if to_update.is_empty() {
            return Ok(0);
        }

        let mut tx = pool.begin().await?;
        let mut updated = 0usize;
        for (app_id, category_id) in &to_update {
            let result = sqlx::query(
                "UPDATE AppModels SET CategoryID = ? WHERE ID = ? AND CategoryID != ?"
            )
            .bind(category_id)
            .bind(app_id)
            .bind(category_id)
            .execute(&mut *tx)
            .await?;
            if result.rows_affected() > 0 {
                updated += 1;
            }
        }
        tx.commit().await?;

        Ok(updated)
    }

    /// 按分类名解析未删预设分类 ID（若已被用户删除/重命名则返回 None）
    pub async fn resolve_category_id_by_name<'e, E>(
        executor: E,
        name: &str,
    ) -> Result<Option<i64>, AppError>
    where
        E: sqlx::Executor<'e, Database = sqlx::Sqlite>,
    {
        let id: Option<i64> = sqlx::query_scalar(
            "SELECT ID FROM CategoryModels WHERE Name = ? AND IsSystem = 0 LIMIT 1",
        )
        .bind(name)
        .fetch_optional(executor)
        .await?;

        Ok(id)
    }

    /// 按候选优先级列表解析首个存在的非系统分类 ID
    async fn resolve_first_category_id<'e, E>(
        executor: E,
        candidates: &[&str],
    ) -> Result<Option<i64>, AppError>
    where
        E: sqlx::Executor<'e, Database = sqlx::Sqlite>,
    {
        if candidates.is_empty() {
            return Ok(None);
        }
        let rows: Vec<(String, i64)> =
            sqlx::query_as("SELECT Name, ID FROM CategoryModels WHERE IsSystem = 0")
                .fetch_all(executor)
                .await?;
        for candidate in candidates {
            if let Some((_, id)) = rows.iter().find(|(n, _)| n == candidate) {
                return Ok(Some(*id));
            }
        }
        Ok(None)
    }

    /// 在已有事务/连接上为应用匹配分类：目录规则优先，未命中则按内置应用规则匹配
    pub async fn match_category_for_app_conn(
        conn: &mut sqlx::SqliteConnection,
        name: &str,
        description: Option<&str>,
        file: Option<&str>,
        publisher: Option<&str>,
    ) -> Option<i64> {
        if let Some(path) = file.filter(|p| !p.trim().is_empty()) {
            if let Ok(dir_cats) = sqlx::query_as::<_, CategoryModel>(
                "SELECT * FROM CategoryModels WHERE IsSystem = 0 AND IsDirectoryMatch = 1",
            )
            .fetch_all(&mut *conn)
            .await
            {
                let rules = build_match_rules(&dir_cats);
                if let Some(id) = match_against_rules(&rules, path) {
                    return Some(id);
                }
            }
        }

        let candidates = auto_category::match_builtin_app_candidates(name, description, file, publisher);
        Self::resolve_first_category_id(&mut *conn, candidates)
            .await
            .ok()
            .flatten()
    }

    /// 为应用匹配分类（Pool 便捷入口）：目录规则优先，未命中则按内置应用规则匹配
    pub async fn match_category_for_app(
        pool: &SqlitePool,
        name: &str,
        description: Option<&str>,
        file: Option<&str>,
        publisher: Option<&str>,
    ) -> Option<i64> {
        if let Some(id) = Self::match_category_by_path(pool, file).await {
            return Some(id);
        }

        let candidates = auto_category::match_builtin_app_candidates(name, description, file, publisher);
        Self::resolve_first_category_id(pool, candidates)
            .await
            .ok()
            .flatten()
    }

    /// 按内置域名规则为新网站匹配预设分类 ID
    pub async fn match_category_for_domain<'e, E>(executor: E, domain: &str) -> Option<i64>
    where
        E: sqlx::Executor<'e, Database = sqlx::Sqlite>,
    {
        let candidates = auto_category::match_builtin_domain_candidates(domain);
        Self::resolve_first_category_id(executor, candidates)
            .await
            .ok()
            .flatten()
    }

    /// 批量对仍处于「未分类」（或 CategoryID <= 0 / IS NULL）的应用与网站应用目录匹配 + 内置自动归类规则（不覆盖用户手动分类）
    pub async fn backfill_builtin_categories_tx(
        conn: &mut sqlx::SqliteConnection,
    ) -> Result<usize, AppError> {
        let Some(sys_id) = sqlx::query_scalar::<_, i64>(
            "SELECT ID FROM CategoryModels WHERE IsSystem = 1 LIMIT 1",
        )
        .fetch_optional(&mut *conn)
        .await?
        else {
            return Ok(0);
        };

        let non_sys_cats: Vec<CategoryModel> =
            sqlx::query_as("SELECT * FROM CategoryModels WHERE IsSystem = 0")
                .fetch_all(&mut *conn)
                .await?;
        if non_sys_cats.is_empty() {
            return Ok(0);
        }

        let name_to_id: HashMap<String, i64> = non_sys_cats
            .iter()
            .filter_map(|c| c.name.as_ref().map(|n| (n.clone(), c.id)))
            .collect();
        let dir_rules = build_match_rules(&non_sys_cats);

        let apps: Vec<(i64, String, Option<String>, Option<String>, Option<String>)> =
            sqlx::query_as(
                "SELECT ID, Name, Description, File, Publisher FROM AppModels WHERE CategoryID = ? OR CategoryID <= 0 OR CategoryID IS NULL",
            )
            .bind(sys_id)
            .fetch_all(&mut *conn)
            .await?;

        let mut updated = 0usize;
        for (app_id, name, desc, file, publisher) in apps {
            let dir_matched = file
                .as_deref()
                .and_then(|path| match_against_rules(&dir_rules, path));
            let cat_id = dir_matched.or_else(|| {
                let candidates = auto_category::match_builtin_app_candidates(
                    &name,
                    desc.as_deref(),
                    file.as_deref(),
                    publisher.as_deref(),
                );
                resolve_candidate_id(&name_to_id, candidates)
            });

            if let Some(target_cat_id) = cat_id {
                let res = sqlx::query(
                    "UPDATE AppModels SET CategoryID = ? WHERE ID = ? AND (CategoryID = ? OR CategoryID <= 0 OR CategoryID IS NULL)",
                )
                .bind(target_cat_id)
                .bind(app_id)
                .bind(sys_id)
                .execute(&mut *conn)
                .await?;
                updated += res.rows_affected() as usize;
            }
        }

        let sites: Vec<(i64, String)> = sqlx::query_as(
            "SELECT ID, Domain FROM WebSiteModels WHERE CategoryID = ? OR CategoryID <= 0 OR CategoryID IS NULL",
        )
        .bind(sys_id)
        .fetch_all(&mut *conn)
        .await?;

        for (site_id, domain) in sites {
            let candidates = auto_category::match_builtin_domain_candidates(&domain);
            if let Some(target_cat_id) = resolve_candidate_id(&name_to_id, candidates) {
                let res = sqlx::query(
                    "UPDATE WebSiteModels SET CategoryID = ? WHERE ID = ? AND (CategoryID = ? OR CategoryID <= 0 OR CategoryID IS NULL)",
                )
                .bind(target_cat_id)
                .bind(site_id)
                .bind(sys_id)
                .execute(&mut *conn)
                .await?;
                updated += res.rows_affected() as usize;
            }
        }

        Ok(updated)
    }

    /// 手动触发一键智能归类：先执行自定义 URL 规则，再为所有未分类应用与网站套用目录及内置规则
    pub async fn apply_auto_classify(pool: &SqlitePool) -> Result<usize, AppError> {
        let mut updated = crate::services::web_data::WebDataService::apply_url_match(pool, None).await?;
        let mut tx = pool.begin().await?;
        updated += Self::backfill_builtin_categories_tx(&mut tx).await?;
        tx.commit().await?;
        Ok(updated)
    }
}

fn build_match_rules(categories: &[CategoryModel]) -> Vec<(i64, Vec<String>)> {
    categories
        .iter()
        .filter(|c| c.is_directory_match && !c.is_system)
        .filter_map(|c| {
            let dirs: Vec<String> = c.directories.as_ref()
                .and_then(|d| serde_json::from_str::<Vec<String>>(d).ok())
                .unwrap_or_default()
                .into_iter()
                .map(|d| d.trim().trim_end_matches(['/', '\\']).to_lowercase())
                .filter(|d| !d.is_empty())
                .collect();
            if dirs.is_empty() {
                None
            } else {
                Some((c.id, dirs))
            }
        })
        .collect()
}

#[inline]
fn resolve_candidate_id(name_to_id: &HashMap<String, i64>, candidates: &[&str]) -> Option<i64> {
    candidates.iter().find_map(|name| name_to_id.get(*name).copied())
}

#[inline]
fn match_against_rules(rules: &[(i64, Vec<String>)], file_path: &str) -> Option<i64> {
    let path_lower = file_path.to_lowercase();
    for (category_id, dirs) in rules {
        for dir in dirs {
            if path_lower.starts_with(dir) {
                let after = &path_lower[dir.len()..];
                if after.is_empty() || after.starts_with('\\') || after.starts_with('/') {
                    return Some(*category_id);
                }
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use sqlx::sqlite::SqlitePoolOptions;

    async fn setup_test_pool(include_research: bool) -> SqlitePool {
        let pool = SqlitePoolOptions::new()
            .max_connections(1)
            .connect("sqlite::memory:")
            .await
            .unwrap();

        sqlx::query(
            r#"
            CREATE TABLE CategoryModels (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT,
                IconFile TEXT,
                Directories TEXT,
                IsDirectoryMatch INTEGER NOT NULL DEFAULT 0,
                Color TEXT,
                IsSystem INTEGER NOT NULL DEFAULT 0,
                IsUrlMatch INTEGER NOT NULL DEFAULT 0,
                UrlPatterns TEXT
            );
            CREATE TABLE AppModels (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Description TEXT,
                File TEXT,
                CategoryID INTEGER DEFAULT 0,
                IconFile TEXT,
                TotalTime INTEGER NOT NULL DEFAULT 0,
                Alias TEXT,
                Publisher TEXT
            );
            CREATE TABLE WebSiteModels (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT,
                Domain TEXT,
                IconFile TEXT,
                CategoryID INTEGER NOT NULL DEFAULT 0,
                Alias TEXT,
                Duration INTEGER NOT NULL DEFAULT 0
            );
            "#,
        )
        .execute(&pool)
        .await
        .unwrap();

        sqlx::query("INSERT INTO CategoryModels (ID, Name, IsSystem) VALUES (1, '未分类', 1)")
            .execute(&pool)
            .await
            .unwrap();
        let mut presets = vec![
            (2, "学习"),
            (3, "AI"),
            (5, "开发"),
            (7, "办公"),
            (8, "社交"),
            (10, "影音"),
            (11, "工具"),
        ];
        if include_research {
            presets.push((4, "科研"));
        }
        for (id, name) in presets {
            sqlx::query("INSERT INTO CategoryModels (ID, Name, IsSystem) VALUES (?, ?, 0)")
                .bind(id)
                .bind(name)
                .execute(&pool)
                .await
                .unwrap();
        }
        pool
    }

    #[tokio::test]
    async fn apply_auto_classify_routes_research_and_fallbacks_without_overwriting_manual() {
        let pool = setup_test_pool(true).await;

        sqlx::query("INSERT INTO AppModels (Name, Description, File, CategoryID) VALUES (?, ?, ?, ?)")
            .bind("Qoder CN Launcher")
            .bind(" ")
            .bind("C:\\Users\\demo\\Qoder CN Launcher.exe")
            .bind(1)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO AppModels (Name, Description, File, CategoryID) VALUES (?, ?, ?, ?)")
            .bind("stats")
            .bind("IBM SPSS Statistics 26.0")
            .bind("D:\\Program Files\\SPSS\\stats.exe")
            .bind(1)
            .execute(&pool)
            .await
            .unwrap();
        // 手动分类过的应用不应被覆盖
        sqlx::query("INSERT INTO AppModels (Name, Description, File, CategoryID) VALUES (?, ?, ?, ?)")
            .bind("wpscloudsvr")
            .bind("WPS服务程序")
            .bind(Option::<String>::None)
            .bind(11)
            .execute(&pool)
            .await
            .unwrap();

        sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID) VALUES (?, ?, ?)")
            .bind("Scopus")
            .bind("www.scopus.com")
            .bind(1)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID) VALUES (?, ?, ?)")
            .bind("Exa")
            .bind("exa.ai")
            .bind(0)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID) VALUES (?, ?, ?)")
            .bind("Unknown")
            .bind("unmatched.example.invalid")
            .bind(1)
            .execute(&pool)
            .await
            .unwrap();

        let updated = CategoryService::apply_auto_classify(&pool).await.unwrap();
        assert_eq!(updated, 4);

        let qoder_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM AppModels WHERE Name = 'Qoder CN Launcher'")
            .fetch_one(&pool)
            .await
            .unwrap();
        let stats_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM AppModels WHERE Name = 'stats'")
            .fetch_one(&pool)
            .await
            .unwrap();
        let wps_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM AppModels WHERE Name = 'wpscloudsvr'")
            .fetch_one(&pool)
            .await
            .unwrap();
        let scopus_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE Domain = 'www.scopus.com'")
            .fetch_one(&pool)
            .await
            .unwrap();
        let exa_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE Domain = 'exa.ai'")
            .fetch_one(&pool)
            .await
            .unwrap();

        assert_eq!(qoder_cat, 3); // AI
        assert_eq!(stats_cat, 4); // 科研
        assert_eq!(wps_cat, 11); // 保留用户原有分类
        assert_eq!(scopus_cat, 4); // 科研
        assert_eq!(exa_cat, 3); // AI

        // 无「科研」分类时自动回退至「学习」
        let pool_no_research = setup_test_pool(false).await;
        sqlx::query("INSERT INTO WebSiteModels (Title, Domain, CategoryID) VALUES ('Scopus', 'www.scopus.com', 1)")
            .execute(&pool_no_research)
            .await
            .unwrap();
        let fallback_updated = CategoryService::apply_auto_classify(&pool_no_research).await.unwrap();
        assert_eq!(fallback_updated, 1);
        let fallback_cat: i64 = sqlx::query_scalar("SELECT CategoryID FROM WebSiteModels WHERE Domain = 'www.scopus.com'")
            .fetch_one(&pool_no_research)
            .await
            .unwrap();
        assert_eq!(fallback_cat, 2); // 学习
    }
}
