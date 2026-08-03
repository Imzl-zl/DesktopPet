pub mod sys_windows;

use serde::Deserialize;
use std::sync::Mutex;
use std::time::Duration;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{Emitter, Manager, PhysicalPosition, WebviewUrl, WebviewWindowBuilder};

/// Tray menu items kept around so the language switcher can re-label them live.
#[allow(dead_code)] // `tray` is held so the icon stays alive for the app's lifetime
struct TrayItems {
    show_pet: tauri::menu::CheckMenuItem<tauri::Wry>,
    settings: MenuItem<tauri::Wry>,
    quit: MenuItem<tauri::Wry>,
    tray: tauri::tray::TrayIcon<tauri::Wry>,
}

/// The pet's opaque region in physical pixels, relative to the window's top-left.
/// The frontend reports this (canvas + visible bubble) so the background thread
/// can make the transparent rest of the window click-through.
#[derive(Default, Clone)]
#[cfg_attr(not(windows), allow(dead_code))]
struct HitRect {
    x: f64,
    y: f64,
    w: f64,
    h: f64,
}

/// Per-window hit rects: every persistent desktop-pet window reports its own
/// opaque rect so the click-through loop can handle them independently.
type HitRectMap = std::collections::HashMap<String, HitRect>;
type ActivePetDragSet = std::collections::HashSet<String>;

const PET_WINDOW_PREFIX: &str = "pet-";
const MAX_DESKTOP_PETS: usize = 12;
type PetPositionMap = std::collections::HashMap<String, (i32, i32)>;

#[derive(Default)]
struct DesktopPetSyncState {
    generation: u64,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DesktopPetWindow {
    id: String,
    visible: bool,
}

fn is_pet_window(label: &str) -> bool {
    label.starts_with(PET_WINDOW_PREFIX)
}

fn should_ignore_cursor_events(active_drag: bool, cursor_inside_hit_rect: bool) -> bool {
    !active_drag && !cursor_inside_hit_rect
}

fn apply_ignore_state<E>(
    last_ignore: &mut std::collections::HashMap<String, bool>,
    label: &str,
    ignore: bool,
    set_ignore: impl FnOnce(bool) -> Result<(), E>,
) -> bool {
    if last_ignore.get(label) == Some(&ignore) || set_ignore(ignore).is_err() {
        return false;
    }
    last_ignore.insert(label.to_owned(), ignore);
    true
}

fn pet_window_label(id: &str) -> String {
    format!("{PET_WINDOW_PREFIX}{id}")
}

fn valid_pet_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id.chars().all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
}

fn pet_positions_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| d.join("DesktopPet").join("pet-positions.json"))
}

fn read_pet_positions() -> PetPositionMap {
    let Some(path) = pet_positions_file() else { return PetPositionMap::new() };
    let Ok(raw) = std::fs::read_to_string(path) else { return PetPositionMap::new() };
    serde_json::from_str(&raw).unwrap_or_default()
}

fn write_pet_positions(positions: &PetPositionMap) {
    let Some(path) = pet_positions_file() else { return };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(raw) = serde_json::to_string(positions) {
        let _ = std::fs::write(path, raw);
    }
}

fn desktop_pets_visible_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| d.join("DesktopPet").join("pets-visible"))
}

fn read_desktop_pets_visible() -> bool {
    desktop_pets_visible_file()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .map(|value| value.trim() != "0")
        .unwrap_or(true)
}

fn write_desktop_pets_visible(visible: bool) {
    if let Some(path) = desktop_pets_visible_file() {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let _ = std::fs::write(path, if visible { "1" } else { "0" });
    }
}

/// Append a line to %APPDATA%/DesktopPet/debug.log for field diagnostics.
fn dlog(msg: &str) {
    if let Some(path) = dirs::config_dir().map(|dir| dir.join("DesktopPet").join("debug.log")) {
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
            use std::io::Write;
            let timestamp = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|duration| duration.as_secs())
                .unwrap_or(0);
            let _ = writeln!(file, "[{timestamp}] {msg}");
        }
    }
}

#[tauri::command]
fn log_debug(msg: String) {
    dlog(&msg);
}

/// Report a pet window's opaque rectangle (physical px, window-relative) so
/// empty transparent areas of that overlay let clicks pass through to apps
/// below. Each pet window registers under its own label so multi-pet mode
/// doesn't have one window overwrite another's click-through rect.
#[tauri::command]
fn set_hit_rect(app: tauri::AppHandle, label: String, x: f64, y: f64, w: f64, h: f64) {
    if !is_pet_window(&label) {
        return;
    }
    if let Some(state) = app.try_state::<Mutex<HitRectMap>>() {
        if let Ok(mut m) = state.lock() {
            m.insert(label, HitRect { x, y, w, h });
        }
    }
}

#[tauri::command]
fn set_pet_dragging(app: tauri::AppHandle, label: String, dragging: bool) -> Result<(), String> {
    let Some(id) = label.strip_prefix(PET_WINDOW_PREFIX) else {
        return Err("invalid pet window label".into());
    };
    if !valid_pet_id(id) {
        return Err("invalid pet id".into());
    }
    if dragging && app.get_webview_window(&label).is_none() {
        return Err("pet window is unavailable".into());
    }
    let Some(state) = app.try_state::<Mutex<ActivePetDragSet>>() else {
        return Err("pet drag state is unavailable".into());
    };
    let mut active_drags = state.lock().map_err(|_| "pet drag state is unavailable")?;
    if dragging {
        active_drags.insert(label);
    } else {
        active_drags.remove(&label);
    }
    Ok(())
}

/// Persist a pet's current physical position immediately after a user drag.
/// The periodic background save remains a safety net for programmatic motion.
#[tauri::command]
fn persist_pet_position(app: tauri::AppHandle, label: String) -> Result<(), String> {
    let Some(id) = label.strip_prefix(PET_WINDOW_PREFIX) else {
        return Err("invalid pet window label".into());
    };
    if !valid_pet_id(id) {
        return Err("invalid pet id".into());
    }
    let Some(window) = app.get_webview_window(&label) else {
        return Err("pet window is unavailable".into());
    };
    let position = window.outer_position().map_err(|error| error.to_string())?;
    let snapshot = {
        let Some(state) = app.try_state::<Mutex<PetPositionMap>>() else {
            return Err("pet position state is unavailable".into());
        };
        let mut positions = state.lock().map_err(|_| "pet position state is unavailable")?;
        positions.insert(id.to_owned(), (position.x, position.y));
        positions.clone()
    };
    write_pet_positions(&snapshot);
    Ok(())
}

fn lang_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| d.join("DesktopPet").join("lang"))
}

fn read_lang() -> String {
    lang_file()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "en".into())
}

fn write_lang(code: &str) {
    if let Some(p) = lang_file() {
        if let Some(d) = p.parent() {
            let _ = std::fs::create_dir_all(d);
        }
        let _ = std::fs::write(p, code);
    }
}

/// Localised tray labels (the only app text on the Rust side).
fn tray_labels(code: &str) -> (&'static str, &'static str, &'static str) {
    match code {
        "vi" => ("Hiện pet", "Cài đặt", "Thoát DesktopPet"),
        "zh" => ("显示宠物", "设置", "退出 DesktopPet"),
        _ => ("Show pet", "Settings", "Quit DesktopPet"),
    }
}

/// Window creation MUST NOT run inside a sync command / menu callback on
/// Windows (the webview build deadlocks against the blocked event loop), so
/// every caller goes through this thread-spawning wrapper.
fn open_settings_impl(app: tauri::AppHandle) {
    std::thread::spawn(move || {
        dlog("open_settings: worker thread");
        if let Some(w) = app.get_webview_window("settings") {
            dlog("open_settings: existing window, showing");
            let _ = w.show();
            let _ = w.unminimize();
            let _ = w.set_focus();
            return;
        }
        match WebviewWindowBuilder::new(&app, "settings", WebviewUrl::App("settings.html".into()))
            .title("DesktopPet")
            .inner_size(1000.0, 680.0)
            .min_inner_size(760.0, 560.0)
            .resizable(true)
            .build()
        {
            Ok(_) => dlog("open_settings: window created"),
            Err(e) => dlog(&format!("open_settings: BUILD FAILED: {e}")),
        }
    });
}

#[tauri::command]
async fn open_settings(app: tauri::AppHandle) {
    dlog("open_settings called");
    open_settings_impl(app);
}

/// Logical size of the primary monitor's work area (DPI-divided). Returns a
/// safe fallback (1920×1080) if the monitor can't be read , spawns must never
/// land off-screen on display hotplug / headless CI.
fn primary_work_area(app: &tauri::AppHandle) -> (f64, f64) {
    app.primary_monitor()
        .ok()
        .flatten()
        .and_then(|m| {
            let s = m.size();
            let sf = m.scale_factor();
            if s.width > 0 && s.height > 0 {
                Some((s.width as f64 / sf, s.height as f64 / sf))
            } else {
                None
            }
        })
        .unwrap_or((1920.0, 1080.0))
}

/// Open an external link in the default browser (About tab buttons).
#[tauri::command]
fn open_url(url: String) {
    if !(url.starts_with("https://") || url.starts_with("http://")) {
        return;
    }
    #[cfg(windows)]
    {
        let _ = std::process::Command::new("cmd").args(["/c", "start", "", &url]).spawn();
    }
    #[cfg(target_os = "macos")]
    {
        let _ = std::process::Command::new("open").arg(&url).spawn();
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        let _ = std::process::Command::new("xdg-open").arg(&url).spawn();
    }
}

fn saved_pet_position(app: &tauri::AppHandle, id: &str) -> Option<(i32, i32)> {
    app.try_state::<Mutex<PetPositionMap>>()
        .and_then(|state| state.lock().ok().and_then(|positions| positions.get(id).copied()))
}

fn default_pet_position(app: &tauri::AppHandle, index: usize) -> (f64, f64) {
    let (width, height) = primary_work_area(app);
    let x = (width - 280.0 - index as f64 * 48.0).max(20.0);
    let y = (height - 380.0 + index as f64 * 32.0).max(20.0);
    (x, y)
}

fn logical_position_for_physical(app: &tauri::AppHandle, x: i32, y: i32) -> (f64, f64) {
    let scale_factor = app.available_monitors()
        .ok()
        .and_then(|monitors| monitors.into_iter().find(|monitor| {
            let position = monitor.position();
            let size = monitor.size();
            x >= position.x
                && x < position.x + size.width as i32
                && y >= position.y
                && y < position.y + size.height as i32
        }))
        .map(|monitor| monitor.scale_factor())
        .unwrap_or(1.0);
    (x as f64 / scale_factor, y as f64 / scale_factor)
}

fn build_desktop_pet_window(
    app: &tauri::AppHandle,
    instance: &DesktopPetWindow,
    index: usize,
) -> Result<(), String> {
    let label = pet_window_label(&instance.id);
    if let Some(window) = app.get_webview_window(&label) {
        if instance.visible && read_desktop_pets_visible() {
            window.show().map_err(|error| error.to_string())?;
        } else {
            window.hide().map_err(|error| error.to_string())?;
        }
        return Ok(());
    }

    let url = format!("index.html?pet={}", instance.id);
    let mut builder = WebviewWindowBuilder::new(app, &label, WebviewUrl::App(url.into()))
        .title("DesktopPet")
        .inner_size(260.0, 320.0)
        .transparent(true)
        .decorations(false)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(false)
        .shadow(false)
        .focused(false)
        .visible(instance.visible && read_desktop_pets_visible());
    builder = match saved_pet_position(app, &instance.id) {
        Some((x, y)) => {
            let (x, y) = logical_position_for_physical(app, x, y);
            builder.position(x, y)
        }
        None => {
            let (x, y) = default_pet_position(app, index);
            builder.position(x, y)
        }
    };
    builder.build().map_err(|error| error.to_string())?;
    Ok(())
}

fn sync_desktop_pet_windows_impl(app: tauri::AppHandle, pets: Vec<DesktopPetWindow>) {
    use std::collections::HashSet;
    let wanted: HashSet<String> = pets.iter().map(|pet| pet_window_label(&pet.id)).collect();
    for (label, window) in app.webview_windows() {
        if is_pet_window(&label) && !wanted.contains(&label) {
            let _ = window.close();
        }
    }
    for (index, pet) in pets.iter().enumerate() {
        if let Err(error) = build_desktop_pet_window(&app, pet, index) {
            dlog(&format!("desktop pet window failed for {}: {error}", pet.id));
        }
    }
}

/// Reconciles every persistent desktop-pet instance with a single frontend
/// source of truth. Window labels are opaque IDs, never sprite slugs or projects.
#[tauri::command]
async fn sync_desktop_pet_windows(
    app: tauri::AppHandle,
    pets: Vec<DesktopPetWindow>,
) -> Result<(), String> {
    if pets.len() > MAX_DESKTOP_PETS {
        return Err(format!("desktop pet limit is {MAX_DESKTOP_PETS}"));
    }
    let mut ids = std::collections::HashSet::new();
    for pet in &pets {
        if !valid_pet_id(&pet.id) || !ids.insert(&pet.id) {
            return Err("invalid or duplicate desktop pet id".into());
        }
    }
    let generation = app
        .try_state::<Mutex<DesktopPetSyncState>>()
        .and_then(|state| state.lock().ok().map(|mut state| {
            state.generation = state.generation.wrapping_add(1);
            state.generation
        }))
        .ok_or("desktop pet sync state unavailable")?;
    std::thread::spawn(move || {
        let is_current = app
            .try_state::<Mutex<DesktopPetSyncState>>()
            .and_then(|state| state.lock().ok().map(|state| state.generation == generation))
            .unwrap_or(false);
        if is_current {
            sync_desktop_pet_windows_impl(app, pets);
        }
    });
    Ok(())
}

/// The floating ball window label. A single instance lives on the desktop as a
/// stable click target (left = bubble menu, right = Settings) so the user
/// doesn't have to chase a roaming pet.
const FLOATING_BALL_LABEL: &str = "floating-ball";
// The visible orb is 56×56, but the window is 80×80 so shadows and hover
// scale are not clipped by the square window edges.
const BALL_W: f64 = 80.0;
const BALL_H: f64 = 80.0;

fn ball_pos_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| d.join("DesktopPet").join("ball-pos"))
}
fn read_ball_pos() -> Option<(f64, f64)> {
    let s = std::fs::read_to_string(ball_pos_file()?).ok()?;
    let (a, b) = s.trim().split_once(',')?;
    Some((a.trim().parse().ok()?, b.trim().parse().ok()?))
}
fn write_ball_pos(x: f64, y: f64) {
    if let Some(p) = ball_pos_file() {
        if let Some(d) = p.parent() { let _ = std::fs::create_dir_all(d); }
        let _ = std::fs::write(p, format!("{x},{y}"));
    }
}
fn ball_visible_file() -> Option<std::path::PathBuf> {
    dirs::config_dir().map(|d| d.join("DesktopPet").join("ball-visible"))
}
fn read_ball_visible() -> bool {
    ball_visible_file()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .map(|s| s.trim() != "0")
        .unwrap_or(true)
}
fn write_ball_visible(on: bool) {
    if let Some(p) = ball_visible_file() {
        if let Some(d) = p.parent() { let _ = std::fs::create_dir_all(d); }
        let _ = std::fs::write(p, if on { "1" } else { "0" });
    }
}

/// Spawn the floating ball window if it doesn't exist yet. Position is restored
/// from disk (clamped onto a monitor) or defaulted to the bottom-right corner.
/// Must be called from a worker thread, like open_settings_impl.
fn spawn_floating_ball_impl(app: tauri::AppHandle) {
    if app.get_webview_window(FLOATING_BALL_LABEL).is_some() {
        return;
    }
    let (sw, sh) = primary_work_area(&app);
    let (x, y) = read_ball_pos()
        .filter(|&(x, y)| x >= 0.0 && x <= sw && y >= 0.0 && y <= sh)
        .unwrap_or_else(|| (sw - BALL_W - 24.0, sh - BALL_H - 80.0));
    let _ = WebviewWindowBuilder::new(
        &app,
        FLOATING_BALL_LABEL,
        WebviewUrl::App("floating-ball.html".into()),
    )
    .title("DesktopPet")
    .inner_size(BALL_W, BALL_H)
    .position(x.max(0.0), y.max(0.0))
    .transparent(true)
    .decorations(false)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false)
    .shadow(false)
    .focused(false)
    .build();
}

/// Persist the floating ball's drop position after an OS-level drag.
/// This deliberately avoids a second set_position call, which can flash on
/// Windows transparent windows during the drag-end compositor update.
#[tauri::command]
fn persist_floating_ball_position(app: tauri::AppHandle) {
    let Some(win) = app.get_webview_window(FLOATING_BALL_LABEL) else {
        return;
    };
    let Ok(pos) = win.outer_position() else {
        return;
    };
    let Ok(scale_factor) = win.scale_factor() else {
        return;
    };
    write_ball_pos(pos.x as f64 / scale_factor, pos.y as f64 / scale_factor);
}

#[tauri::command]
fn set_floating_ball_visible(app: tauri::AppHandle, visible: bool) {
    write_ball_visible(visible);
    if visible {
        // Spawn lazily if missing (e.g. re-enabled after startup with it off).
        if app.get_webview_window(FLOATING_BALL_LABEL).is_none() {
            spawn_floating_ball_impl(app);
        } else if let Some(w) = app.get_webview_window(FLOATING_BALL_LABEL) {
            let _ = w.show();
        }
    } else if let Some(w) = app.get_webview_window(FLOATING_BALL_LABEL) {
        let _ = w.hide();
    }
}

#[tauri::command]
fn get_floating_ball_visible() -> bool {
    read_ball_visible()
}

/// Persist the chosen language (for the tray on next launch) and re-label the
/// tray menu items now. Called by the Settings language switcher.
#[tauri::command]
fn set_lang(app: tauri::AppHandle, code: String) {
    write_lang(&code);
    let (p, s, q) = tray_labels(&code);
    if let Some(items) = app.try_state::<Mutex<TrayItems>>() {
        if let Ok(it) = items.lock() {
            let _ = it.show_pet.set_text(p);
            let _ = it.settings.set_text(s);
            let _ = it.quit.set_text(q);
        }
    }
}

#[tauri::command]
fn get_desktop_pets_visible() -> bool {
    read_desktop_pets_visible()
}

/// Show the popover (the macOS menu-bar popover equivalent) near the cursor.
fn show_popover(app: &tauri::AppHandle) {
    let win = match app.get_webview_window("popover") {
        Some(w) => w,
        None => {
            match WebviewWindowBuilder::new(app, "popover", WebviewUrl::App("popover.html".into()))
                .title("DesktopPet")
                .inner_size(300.0, 430.0)
                .decorations(false)
                .transparent(true)
                .always_on_top(true)
                .skip_taskbar(true)
                .resizable(false)
                .focused(true)
                .visible(false)
                .build()
            {
                Ok(w) => {
                    dlog("popover: window created");
                    // Transient popover: losing focus hides it (Rust-side net,
                    // independent of the webview's own blur listener).
                    let wh = w.clone();
                    w.on_window_event(move |ev| {
                        if let tauri::WindowEvent::Focused(false) = ev {
                            let _ = wh.hide();
                        }
                    });
                    w
                }
                Err(e) => {
                    dlog(&format!("popover: BUILD FAILED: {e}"));
                    return;
                }
            }
        }
    };
    // Place near the cursor, clamped onto the monitor under it.
    if let Ok(cur) = app.cursor_position() {
        let sf = win.scale_factor().unwrap_or(1.0);
        let (w, h) = (300.0 * sf, 430.0 * sf);
        let mut x = cur.x - w / 2.0;
        let mut y = cur.y - h - 12.0; // prefer above the cursor (tray at bottom)
        if let Ok(Some(mon)) = app.monitor_from_point(cur.x, cur.y) {
            let mp = mon.position();
            let ms = mon.size();
            if y < mp.y as f64 {
                y = cur.y + 12.0; // no room above , drop below
            }
            x = x.max(mp.x as f64).min(mp.x as f64 + ms.width as f64 - w);
            y = y.max(mp.y as f64).min(mp.y as f64 + ms.height as f64 - h);
        }
        let _ = win.set_position(PhysicalPosition::new(x, y));
    }
    let _ = win.show();
    let _ = win.set_focus();
    let _ = win.emit("popover-shown", ());
}

#[tauri::command]
async fn open_popover(app: tauri::AppHandle) {
    dlog("open_popover called");
    std::thread::spawn(move || show_popover(&app));
}

/// Show or hide all desktop-pet windows without changing their instance data.
#[tauri::command]
fn set_desktop_pets_visible(app: tauri::AppHandle, visible: bool) {
    write_desktop_pets_visible(visible);
    for (label, window) in app.webview_windows() {
        if is_pet_window(&label) {
            let _ = if visible { window.show() } else { window.hide() };
        }
    }
    if let Some(items) = app.try_state::<Mutex<TrayItems>>() {
        if let Ok(items) = items.lock() {
            let _ = items.show_pet.set_checked(visible);
        }
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // Must be the first plugin: a second launch (double-clicking the
        // shortcut while the app runs) exits immediately and the running
        // instance opens Settings instead , no duplicate pets.
        .plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
            open_settings_impl(app.clone());
        }))
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            None,
        ))
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init())
        .invoke_handler(tauri::generate_handler![
            open_settings,
            open_url,
            sync_desktop_pet_windows,
            set_lang,
            set_desktop_pets_visible,
            get_desktop_pets_visible,
            open_popover,
            log_debug,
            set_hit_rect,
            set_pet_dragging,
            persist_pet_position,
            persist_floating_ball_position,
            set_floating_ball_visible,
            get_floating_ball_visible,
            sys_windows::list_system_windows
        ])
        .setup(|app| {
            app.manage(Mutex::new(HitRectMap::new()));
            app.manage(Mutex::new(ActivePetDragSet::new()));
            app.manage(Mutex::new(read_pet_positions()));
            app.manage(Mutex::new(DesktopPetSyncState::default()));

            // Background loop: make each persistent overlay click-through
            // outside its opaque rectangle and persist every instance position.
            let handle = app.handle().clone();
            std::thread::spawn(move || {
                let mut last_ignore: std::collections::HashMap<String, bool> = std::collections::HashMap::new();
                let mut flip_logs: u32 = 0;
                let mut last_saved = read_pet_positions();
                let mut tick: u32 = 0;
                loop {
                    // 60ms (≈16Hz): click-through detection doesn't need 33Hz
                    // polling. The cursor rarely traverses a pet's opaque rect
                    // in <60ms, and halving the tick rate halves the Win32
                    // GetCursorPos + GetWindowRect calls per second.
                    std::thread::sleep(Duration::from_millis(60));

                    // Snapshot all pet windows once per tick so spawns/closes
                    // during the loop don't corrupt the iterator.
                    let wins: Vec<(String, tauri::WebviewWindow)> = handle
                        .webview_windows()
                        .into_iter()
                        .filter(|(label, _)| is_pet_window(label))
                        .collect();
                    // Classify visibility once per window. Hidden overlays do
                    // no work; an unknown state stays interactive until a later
                    // poll can classify it safely.
                    let mut visible_wins = Vec::new();
                    let mut unknown_visibility_wins = Vec::new();
                    for window in &wins {
                        match window.1.is_visible() {
                            Ok(true) => visible_wins.push(window),
                            Ok(false) => {}
                            Err(_) => unknown_visibility_wins.push(window),
                        }
                    }
                    for (label, win) in &unknown_visibility_wins {
                        apply_ignore_state(&mut last_ignore, label, false, |ignore| {
                            win.set_ignore_cursor_events(ignore)
                        });
                    }
                    let cur = if visible_wins.is_empty() {
                        None
                    } else {
                        handle.cursor_position().ok()
                    };

                    // Snapshot all hit rects in ONE lock acquisition (instead of
                    // N locks per tick). The clone is ~12 entries × 32 bytes.
                    let rects: HitRectMap = handle
                        .try_state::<Mutex<HitRectMap>>()
                        .and_then(|s| s.lock().ok().map(|m| m.clone()))
                        .unwrap_or_default();
                    let active_drags: ActivePetDragSet = handle
                        .try_state::<Mutex<ActivePetDragSet>>()
                        .and_then(|s| s.lock().ok().map(|m| m.clone()))
                        .unwrap_or_default();

                    for (label, win) in &visible_wins {
                        let Ok(wp) = win.outer_position() else { continue };
                        // Fail-safe: no rect yet (webview still booting) or
                        // cursor unreadable → keep INTERACTIVE.
                        let inside = match &cur {
                            Some(cur) => match rects.get(label) {
                                Some(r) if r.w > 0.0 => {
                                    let rx = cur.x - wp.x as f64;
                                    let ry = cur.y - wp.y as f64;
                                    rx >= r.x && rx <= r.x + r.w && ry >= r.y && ry <= r.y + r.h
                                }
                                _ => true, // no rect yet → stay interactive
                            },
                            None => true,
                        };
                        // An active pointer drag holds an explicit interaction
                        // lease so an async window move cannot make this overlay
                        // click-through before Pointer Capture receives its end.
                        let ignore = should_ignore_cursor_events(active_drags.contains(label), inside);
                        if apply_ignore_state(&mut last_ignore, label, ignore, |ignore| {
                            win.set_ignore_cursor_events(ignore)
                        }) {
                            if flip_logs < 60 {
                                flip_logs += 1;
                                let cur_str = cur.as_ref().map_or("err".to_string(), |c| format!("({:.0},{:.0})", c.x, c.y));
                                dlog(&format!(
                                    "hit flip: label={label} ignore={ignore} cur={cur_str} win=({},{})",
                                    wp.x, wp.y
                                ));
                            }
                        }
                    }

                    // Prune closed windows and persist every instance position,
                    // both throttled to roughly once per second.
                    tick = tick.wrapping_add(1);
                    if tick % 17 == 0 {
                        let active: std::collections::HashSet<&String> =
                            wins.iter().map(|(label, _)| label).collect();
                        if let Some(state) = handle.try_state::<Mutex<HitRectMap>>() {
                            if let Ok(mut rects) = state.lock() {
                                rects.retain(|label, _| active.contains(label));
                            }
                        }
                        last_ignore.retain(|label, _| active.contains(label));
                        if let Some(state) = handle.try_state::<Mutex<ActivePetDragSet>>() {
                            if let Ok(mut active_drags) = state.lock() {
                                active_drags.retain(|label| active.contains(label));
                            }
                        }

                        let mut changed = false;
                        for (label, window) in &visible_wins {
                            let Some(id) = label.strip_prefix(PET_WINDOW_PREFIX) else { continue };
                            let Ok(position) = window.outer_position() else { continue };
                            let next = (position.x, position.y);
                            if last_saved.get(id) != Some(&next) {
                                last_saved.insert(id.to_owned(), next);
                                changed = true;
                            }
                        }
                        if changed {
                            if let Some(state) = handle.try_state::<Mutex<PetPositionMap>>() {
                                if let Ok(mut positions) = state.lock() {
                                    *positions = last_saved.clone();
                                    write_pet_positions(&positions);
                                }
                            }
                        }
                    }
                }
            });

            // Tray menu , the pet window is frameless, so this is how you reach
            // Settings or quit the app. Labels start in the saved language; the
            // Settings switcher re-labels them live via the `set_lang` command.
            let (p_lbl, s_lbl, q_lbl) = tray_labels(&read_lang());
            let pets_visible = read_desktop_pets_visible();
            let show_pet_i = tauri::menu::CheckMenuItem::with_id(
                app, "show_pet", p_lbl, true, pets_visible, None::<&str>)?;
            let settings_i = MenuItem::with_id(app, "settings", s_lbl, true, None::<&str>)?;
            let quit_i = MenuItem::with_id(app, "quit", q_lbl, true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show_pet_i, &settings_i, &quit_i])?;
            let mut tray = TrayIconBuilder::new()
                .tooltip("DesktopPet")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_tray_icon_event(|tray, event| {
                    // Left-click on the tray icon opens Settings; the pet's
                    // right-click popover covers the quick controls.
                    if let tauri::tray::TrayIconEvent::Click {
                        button: tauri::tray::MouseButton::Left,
                        button_state: tauri::tray::MouseButtonState::Up,
                        ..
                    } = event
                    {
                        open_settings_impl(tray.app_handle().clone());
                    }
                })
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show_pet" => set_desktop_pets_visible(app.clone(), !read_desktop_pets_visible()),
                    "settings" => open_settings_impl(app.clone()),
                    "quit" => app.exit(0),
                    _ => {}
                });
            if let Some(icon) = app.default_window_icon() {
                tray = tray.icon(icon.clone());
            }
            let tray = tray.build(app)?;
            app.manage(Mutex::new(TrayItems {
                show_pet: show_pet_i.clone(),
                settings: settings_i.clone(),
                quit: quit_i.clone(),
                tray,
            }));
            dlog("setup complete, tray + loop running");
            // First run: open Settings so the user knows to pick a pet
            // (otherwise the pet just sits there silently).
            let marker = dirs::config_dir().map(|d| d.join("DesktopPet").join(".onboarded"));
            if let Some(m) = marker {
                if !m.exists() {
                    // Call the sync impl directly: open_settings is an async fn
                    // (Tauri command), so calling it without .await would create
                    // a Future that's never polled and do nothing.
                    open_settings_impl(app.handle().clone());
                    if let Some(parent) = m.parent() {
                        let _ = std::fs::create_dir_all(parent);
                    }
                    let _ = std::fs::write(&m, "1");
                }
            }

            // Floating ball: a stable click target that doesn't run away with
            // the pet. Hidden by config (applies on next launch); spawned in a
            // worker thread because window creation must not run on the event
            // loop on Windows (same reason as open_settings_impl).
            if read_ball_visible() {
                let app2 = app.handle().clone();
                std::thread::spawn(move || spawn_floating_ball_impl(app2));
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running DesktopPet");
}

#[cfg(test)]
mod tests {
    use super::{apply_ignore_state, should_ignore_cursor_events};
    use std::collections::HashMap;

    #[test]
    fn keeps_an_active_drag_interactive_outside_the_last_hit_rect() {
        assert!(should_ignore_cursor_events(false, false));
        assert!(!should_ignore_cursor_events(true, false));
        assert!(!should_ignore_cursor_events(true, true));
    }

    #[test]
    fn retries_a_failed_ignore_cursor_transition() {
        let mut last_ignore = HashMap::from([("pet-a".to_owned(), true)]);

        assert!(!apply_ignore_state(
            &mut last_ignore,
            "pet-a",
            false,
            |_| Err::<(), ()>(()),
        ));
        assert_eq!(last_ignore.get("pet-a"), Some(&true));

        assert!(apply_ignore_state(
            &mut last_ignore,
            "pet-a",
            false,
            |_| Ok::<(), ()>(()),
        ));
        assert_eq!(last_ignore.get("pet-a"), Some(&false));
    }
}
