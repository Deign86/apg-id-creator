# The Tauri window

This is the desktop window around the ID Maker. It draws nothing itself:
the card, the photo pipeline and the PDF all come from the one HTML file
built at the top of this repo, exactly as they do in a browser.

It replaced `desktop/shell/`, a WinForms + WebView2 window written in C#
(removed 4 Nov 2026; see git history).
On Windows **Tauri renders in WebView2 too**, so the engine under the card
has not changed — only the window around it. The printed card is governed
by the same `npm test` as before.

This is step one of moving to Tauri + React + TypeScript. The app inside
the window is still the plain-JavaScript one; porting it to React is a
separate piece of work, and nothing here has to change when that happens.

---

## What you need on the laptop, once

The C# shell needed the .NET SDK. Tauri needs Rust instead, and Rust on
Windows needs Microsoft's C++ linker. Budget about **4 GB** and half an
hour. Both are free and neither needs administrator rights beyond the
normal installer prompt.

1. **Visual Studio Build Tools** — <https://visualstudio.microsoft.com/visual-cpp-build-tools/>
   In the installer tick **Desktop development with C++**. Nothing else.
   (If Visual Studio itself is already installed with that workload, skip
   this.)
2. **Rust** — <https://rustup.rs>. Take the default option (1), which
   installs the `stable-x86_64-pc-windows-msvc` toolchain.
3. Close and reopen the terminal, then check both:
   ```
   rustc --version
   cargo --version
   ```
4. **WebView2 Runtime** is already on this laptop (the old app used it),
   and the installer this produces fetches it on any machine that lacks
   it. Nothing to do.

Node is already installed — it is what builds the HTML file.

---

## Building it

From the folder above this one, build the app itself first, then the
window around it:

```
node build.mjs
cd app
npm install
npm run build
```

`npm install` is only needed the first time, and the first `npm run build`
is slow — Rust compiles Tauri from source, which takes several minutes.
Later builds take seconds.

The installer comes out at:

```
app\src-tauri\target\release\bundle\nsis\Alpha Premier ID Maker_1.17.0_x64-setup.exe
```

That one file is what HR runs. It installs for the current user only, so
it never asks for an administrator password, and it puts the app in the
Start menu.

To run it without building an installer:

```
npm run dev
```

---

## What the window does

Everything the C# shell did, for the same reasons:

| | |
| --- | --- |
| **A real origin** | The app is served from `http://tauri.localhost`, not from a `file://` path. A `file://` page has no real origin, which makes browser storage fragile and cross-site requests — the shared register among them — behave unlike anywhere the app was tested. |
| **Its own browser data** | The issued-ID list, the register address, the printer settings and any custom card design belong to this app, not to whoever is signed in to Edge. |
| **Downloads land somewhere visible** | The PDFs, the batch ZIP and the register CSV are saved and then **shown in Explorer**. Saving them silently would leave the operator with no idea where the print went. |
| **Links open in a real browser** | A link to somewhere else opens in Edge or Chrome. An application window that can be navigated away from is no longer an application window. |
| **DevTools stay on** | Deliberately, same as before: it is how a problem on this laptop gets diagnosed, and nobody finds it by accident. It needs the `devtools` Cargo feature, which is already set in `Cargo.toml`. |

---

## One thing to do before switching over

**The issued-ID list on this laptop will look empty in the new app.**

Browser storage belongs to an origin, and the origin changes from
`https://idmaker.alphapremier.local` (the old shell's made-up host name)
to `http://tauri.localhost` (Tauri's). There is no way to carry it
across: one origin cannot read another's storage, which is the whole
point of the rule.

What this does and does not cost:

- **The shared register sheet is untouched.** It is the real record, and
  the duplicate check reads it, so issuing cards carries on working
  normally from the first minute.
- **The local list is a convenience copy.** Download it from the old app
  first — *Issued IDs → Download register (CSV)* — if you want to keep it.
- **Printer setting and any custom card design reset.** Both are a few
  clicks to set again.

---

## Files

| Path | What it is |
| --- | --- |
| `src-tauri/src/main.rs` | The whole window: 79 lines. |
| `src-tauri/tauri.conf.json` | Window size, icon, installer settings. Keep `version` in step with `APP_VERSION` in `src/config.js`. |
| `src-tauri/Cargo.toml` | Rust dependencies. Also carries a version to keep in step. |
| `sync-web.mjs` | Copies the built HTML into `web/index.html`. Runs automatically before every dev run and build, so the window can never show a stale copy. |
| `web/` | That copy. Generated, not committed. |
