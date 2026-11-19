#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tokio::sync::broadcast;

use routes::web_sentry::SentryState;
use services::backup::BackupPaths;
use services::category::CategoryService;
use services::config::ConfigService;
use services::web_data::WebDataService;
use services::webdav::WebDavService;
use shijian_server::{
    constants, create_app, db, legacy, parse_data_dir, parse_server_addr, pipe, plugins, routes,
    services, single_instance, utils, websocket_manager,
};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--mcp" || a == "--mcp-stdio") {
        let code = shijian_server::mcp::stdio::run_stdio_bridge(&args);
        std::process::exit(code);
    }

    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("Failed to create tokio runtime");

    rt.block_on(async_main());
}

async fn async_main() {
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| std::env::current_dir().unwrap());

    let log_dir = exe_dir.join("Logs");

    let log_filter = if cfg!(debug_assertions) {
        "shijian_server=debug,tower_http=debug,shijian_plugin_manifest=info"
    } else {
        "shijian_server=info,tower_http=warn,shijian_plugin_manifest=info"
    };

    let _logging_guard =
        shijian_logging::init("shijian-server", log_filter, shijian_logging::PanicMode::SyncFile, 30);

    if let Err(e) = run(&exe_dir).await {
        tracing::error!("FATAL: {}", e);

        let now = chrono::Utc::now().format("%Y-%m-%dT%H:%M:%S%.3fZ");
        let message = format!("[{}] ERROR shijian_server: FATAL: {}\n", now, e);

        let today = chrono::Utc::now().format("%Y-%m-%d");
        let log_path = log_dir.join(format!("shijian-server.{}.log", today));
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&log_path)
        {
            use std::io::Write;
            let _ = file.write_all(message.as_bytes());
            let _ = file.flush();
        }

        eprintln!("{}", message);
    }
}

async fn run(exe_dir: &Path) -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().collect();

    // 改名迁移：先把旧品牌目录补进来，再决定数据目录/数据库路径
    legacy::migrate_legacy_brand_dirs();

    let data_dir = parse_data_dir(&args);

    let _single_instance = single_instance::try_acquire("Global\\ShiJianServerSingleInstance")
        .ok_or_else(|| anyhow::anyhow!("Another instance of shijian-server is already running"))?;

    let web_favicons_dir = if cfg!(target_os = "macos") {
        constants::default_data_dir().join("WebFavicons")
    } else {
        exe_dir.join("WebFavicons")
    };

    let db_path = std::env::var("SHIJIAN_DB_PATH")
        .map(PathBuf::from)
        .unwrap_or_else(|_| data_dir.join("data.db"));

    tracing::info!("Using database: {}", db_path.display());
    tracing::info!("Using data dir: {}", data_dir.display());
    tracing::info!("Using web favicons dir: {}", web_favicons_dir.display());

    // 旧版本地时间按宿主机时区迁移 SHIJIAN_TZ 可覆盖
    let pool = db::init_db(
        db_path.to_str().ok_or_else(|| anyhow::anyhow!("Database path contains invalid UTF-8"))?,
        utils::host_timezone().name(),
    ).await?;

    let config_service = Arc::new(ConfigService::new(&data_dir));

    // 读取初始配置，决定 WebSocket 是否启动
    let initial_web_enabled = match config_service.load().await {
        Ok(config) => config.general.is_web_enabled,
        Err(e) => {
            tracing::warn!("Failed to load initial config, defaulting web_enabled to false: {}", e);
            false
        }
    };

    let (web_enabled_tx, web_enabled_rx) = tokio::sync::watch::channel(initial_web_enabled);

    let (tx, _rx) = broadcast::channel(constants::BROADCAST_CHANNEL_CAPACITY);
    let sentry_state = Arc::new(SentryState {
        pool: pool.clone(),
        is_sleep: tokio::sync::RwLock::new(false),
        tx,
        web_favicons_dir: web_favicons_dir.clone(),
        semaphore: Arc::new(tokio::sync::Semaphore::new(constants::MAX_CONCURRENT_PIPE_CLIENTS)),
    });

    let backup_paths = Arc::new(BackupPaths::from_env(&data_dir, &web_favicons_dir, exe_dir));

    let plugins_dir = exe_dir.join(constants::PLUGINS_DIR_NAME);
    let plugin_state =
        Arc::new(plugins::PluginState::init(&plugins_dir, &data_dir).await?);

    let app = create_app(
        pool.clone(),
        config_service.clone(),
        web_enabled_tx.clone(),
        plugin_state.clone(),
        backup_paths.clone(),
        &exe_dir,
    );

    if let Err(e) = WebDataService::warmup_url_match_cache(&pool).await {
        tracing::warn!("Failed to warmup URL match cache: {}", e);
    }

    let addr: SocketAddr = parse_server_addr()?;

    tracing::info!("Server listening on http://{}", addr);

    let listener = tokio::net::TcpListener::bind(addr).await?;
    let http_server = axum::serve(listener, app);

    tokio::spawn(websocket_manager::run(web_enabled_rx, sentry_state.clone()));

    tokio::spawn(pipe::start(
        sentry_state,
        pool.clone(),
        plugin_state.clone(),
    ));

    tokio::spawn(plugins::current::run_idle_watchdog(
        plugin_state.clone(),
        pool.clone(),
    ));

    tokio::spawn(services::app_timer::AppTimerService::run_cleanup_task(
        pool.clone(),
        config_service.clone(),
    ));

    tokio::spawn(WebDavService::run_sync_task(
        pool.clone(),
        (*backup_paths).clone(),
        config_service.clone(),
        utils::host_timezone().name().to_string(),
    ));

    let startup_pool = pool.clone();
    tokio::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_secs(5)).await;

        match CategoryService::apply_directory_match(&startup_pool, None).await {
            Ok(count) => {
                if count > 0 {
                    tracing::info!("Startup directory match: {} apps re-categorized", count);
                }
            }
            Err(e) => {
                tracing::warn!("Startup directory match skipped: {}", e);
            }
        }
    });

    http_server.await?;

    Ok(())
}
