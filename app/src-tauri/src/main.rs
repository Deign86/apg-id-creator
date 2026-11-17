// No console window behind the app in a release build.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! A window around the ID Maker.
//!
//! It draws nothing itself. The card, the photo pipeline and the PDF all
//! come from the one HTML file, exactly as they do in a browser, so the
//! golden-render test still governs what is printed.
//!
//! This replaces a WinForms + WebView2 shell that did the same job in
//! C#. On Windows, Tauri renders in WebView2 too, so the engine under
//! the card has not changed — only the window around it.

use tauri::webview::{DownloadEvent, WebviewWindowBuilder};
use tauri::{RunEvent, Url, WebviewUrl, WebviewWindow, WindowEvent};
use tauri_plugin_opener::OpenerExt;

/// Is this somewhere the app window is allowed to go?
///
/// Everything the app itself serves, plus the blob: and data: URLs its
/// own downloads and previews use. Anything else is the web, and the web
/// belongs in a real browser: an application window that can be
/// navigated away from is no longer an application window.
fn is_ours(url: &Url) -> bool {
    match url.scheme() {
        // what Tauri serves the app from, and what the app makes itself
        "tauri" | "blob" | "data" | "about" => true,
        // on Windows the app is served from http://tauri.localhost
        "http" | "https" => url
            .host_str()
            .is_some_and(|h| h == "tauri.localhost" || h.ends_with(".localhost")),
        _ => false,
    }
}

/// Hold the cursor inside the signature pad while somebody signs.
///
/// A pen tablet whose driver acts as a mouse moves the ordinary cursor,
/// and an absolute pen drifts off the pad as easily as it lands on it;
/// once off, the next tap clicks whatever is there. `x, y, w, h` are the
/// pad's box in CSS pixels, from the page's `getBoundingClientRect`.
///
/// The cursor belongs to every program on the computer, so it is let go
/// by `release_cursor`, and also whenever this window loses focus or
/// closes (below). Ctrl+Alt+Del frees it too.
#[tauri::command]
fn hold_cursor(window: WebviewWindow, x: f64, y: f64, w: f64, h: f64) -> Result<(), String> {
    #[cfg(windows)]
    {
        use windows::Win32::Foundation::RECT;
        use windows::Win32::UI::WindowsAndMessaging::ClipCursor;

        let origin = window.inner_position().map_err(|e| e.to_string())?;
        let scale = window.scale_factor().map_err(|e| e.to_string())?;
        // one pixel in from each edge, so the cursor stays over the pad
        let rect = RECT {
            left: origin.x + (x * scale).ceil() as i32 + 1,
            top: origin.y + (y * scale).ceil() as i32 + 1,
            right: origin.x + ((x + w) * scale).floor() as i32 - 1,
            bottom: origin.y + ((y + h) * scale).floor() as i32 - 1,
        };
        if rect.right <= rect.left || rect.bottom <= rect.top {
            return Err("the signature pad is not on screen".into());
        }
        unsafe { ClipCursor(Some(&rect)) }.map_err(|e| e.to_string())?;
    }
    #[cfg(not(windows))]
    let _ = (window, x, y, w, h);
    Ok(())
}

/// Let the cursor go anywhere again. Harmless when it is not held.
#[tauri::command]
fn release_cursor() {
    #[cfg(windows)]
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::ClipCursor(None);
    }
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![hold_cursor, release_cursor])
        // never keep the cursor once this window is not the one in use
        .on_window_event(|_window, event| {
            if matches!(event, WindowEvent::Focused(false) | WindowEvent::CloseRequested { .. } | WindowEvent::Destroyed) {
                release_cursor();
            }
        })
        .setup(|app| {
            let for_links = app.handle().clone();
            let for_saves = app.handle().clone();

            WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into()))
                .title("Alpha Premier ID Maker")
                .inner_size(1280.0, 900.0)
                .min_inner_size(960.0, 700.0)
                .center()
                .on_navigation(move |url| {
                    if is_ours(url) {
                        return true;
                    }
                    if url.scheme() == "https" {
                        let _ = for_links.opener().open_url(url.as_str(), None::<&str>);
                    }
                    false
                })
                .on_download(move |_webview, event| {
                    // The PDFs, the batch ZIP and the register CSV all
                    // arrive this way. Saving them silently would leave
                    // the operator with no idea where the print went, so
                    // show the finished file in Explorer and let them
                    // decide what to do with it.
                    if let DownloadEvent::Finished {
                        path: Some(path),
                        success: true,
                        ..
                    } = event
                    {
                        let _ = for_saves.opener().reveal_item_in_dir(path);
                    }
                    true
                })
                .build()?;

            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("the ID Maker window could not be created")
        .run(|_app, event| {
            if matches!(event, RunEvent::Exit | RunEvent::ExitRequested { .. }) {
                release_cursor();
            }
        });
}
