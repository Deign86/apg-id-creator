#!/usr/bin/env node
/* ------------------------------------------------------------------
   Put the built app where Tauri expects it
   ------------------------------------------------------------------
   The ID Maker builds to ONE self-contained HTML file at the top of
   this repo. Tauri wants a folder with an index.html in it, so this
   copies that file into app/web/index.html.

   tauri.conf.json runs it before every dev run and every build, so the
   window can never be showing a stale copy of the app. Nothing is
   generated here and nothing is edited: it is a copy.

   Paths come from this file's own location, not from the working
   directory, because Tauri does not promise which directory it runs
   beforeBuildCommand from.
   ------------------------------------------------------------------ */
import { copyFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const built = join(here, '..', 'Alpha Premier ID Maker.html');
const webDir = join(here, 'web');

if (!existsSync(built)) {
  console.error(`\nCannot find "${built}".\nRun "node build.mjs" in the folder above this one first.\n`);
  process.exit(1);
}

mkdirSync(webDir, { recursive: true });
copyFileSync(built, join(webDir, 'index.html'));
console.log(`web/index.html  <-  Alpha Premier ID Maker.html (${(statSync(built).size / 1024 / 1024).toFixed(2)} MB)`);
