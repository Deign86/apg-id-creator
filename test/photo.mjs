#!/usr/bin/env node
/* ------------------------------------------------------------------
   Photo-sizing test
   ------------------------------------------------------------------
   The card measures the head and scales the photo so it is headWidth
   millimetres wide. Get the measurement wrong and the error is not
   small: a real photo measured its head at a quarter of its true width,
   so the photo was blown up four times to compensate, and the face
   filled the whole card. Nothing caught it, because every other test
   uses the one example photo that happens to measure correctly.

   The silhouettes here are drawn, not photographed — an employee's
   photo has no business living in a repository, and the thing under
   test is geometry. The one that matters is the tuft: a head is not
   reliably convex, and the old reader ended the head at the first dip
   in the outline, which a spike of hair provides in the first few rows.

     npm run test:photo
   ------------------------------------------------------------------ */
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const app = pathToFileURL(join(here, '..', 'Alpha Premier ID Maker.html')).href;

const pw = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const chromium = pw.chromium ?? pw.default.chromium;
const browser = await chromium.launch();
const errors = [];
const fail = [];
const check = (ok, what) => { console.log(`${ok ? '  ok  ' : ' FAIL '} ${what}`); if (!ok) fail.push(what); };

const page = await browser.newPage({ viewport: { width: 900, height: 1100 } });
page.on('pageerror', (e) => errors.push(e.message));
await page.goto(app);
await page.waitForSelector('#card-box:not([hidden])', { timeout: 20000 });

await page.evaluate(() => {
  /** A person on transparent: head, neck, shoulders, optional hair tuft. */
  window.__person = ({ w = 900, h = 1200, headW = 300, tuft = 0, bodyTo = 1.0 } = {}) => {
    const c = document.createElement('canvas');
    c.width = w; c.height = Math.round(h * bodyTo);
    const g = c.getContext('2d');
    const cx = w / 2, headTop = h * 0.10, headH = headW * 1.3;
    g.fillStyle = '#6b4f3a';
    if (tuft) {                                   // a spike of hair above the head
      g.beginPath();
      g.moveTo(cx - tuft / 2, headTop + 10);
      g.lineTo(cx, headTop - headH * 0.18);
      g.lineTo(cx + tuft / 2, headTop + 10);
      g.closePath(); g.fill();
    }
    g.beginPath(); g.ellipse(cx, headTop + headH / 2, headW / 2, headH / 2, 0, 0, Math.PI * 2); g.fill();
    const neckTop = headTop + headH * 0.88, shoulderY = neckTop + headH * 0.30;
    g.fillRect(cx - headW * 0.20, neckTop, headW * 0.40, shoulderY - neckTop + 2);
    g.fillStyle = '#243a6b';
    g.beginPath();
    g.moveTo(cx - headW * 0.22, shoulderY);
    g.lineTo(cx + headW * 0.22, shoulderY);
    g.lineTo(cx + headW * 1.35, shoulderY + headH * 0.42);
    g.lineTo(cx + headW * 1.35, c.height);
    g.lineTo(cx - headW * 1.35, c.height);
    g.lineTo(cx - headW * 1.35, shoulderY + headH * 0.42);
    g.closePath(); g.fill();
    return new Promise((r) => c.toBlob(r, 'image/png'));
  };
  window.__measure = async (blob) => {
    const res = await processPhotoFile(new File([blob], 'p.png', { type: 'image/png' }));
    if (!res.check?.ok) return { rejected: res.check?.problem || res.error };
    const pl = res.check.placement;
    const srcW = res.image.naturalWidth || res.image.width;
    return {
      headMm: +(res.check.head.width * (pl.w / srcW)).toFixed(2),
      photoWmm: +pl.w.toFixed(1),
      shortBy: pl.shortBy ?? 0,
      warnings: res.check.warnings,
    };
  };
});

const want = await page.evaluate(() => CONFIG.photo.headWidth);
// defaulted so this still reports a real failure, rather than NaN, when
// run against a build from before the cap existed
const most = await page.evaluate(() => CONFIG.photo.headWidth * (CONFIG.photo.maxEnlarge ?? 1.15));
const cardW = await page.evaluate(() => CONFIG.card.widthMm);

/* ---- 1. a plain head is measured, and sized, correctly ----------- */

let m = await page.evaluate(async () => __measure(await __person({})));
check(m.headMm >= want * 0.95 && m.headMm <= most + 0.1,
  `a plain chest-up silhouette gives a ${m.headMm} mm head (wanted ${want}, never over ${most.toFixed(2)})`);
check(m.photoWmm < cardW * 1.6, `and a photo ${m.photoWmm} mm wide, not several cards across`);

/* ---- 2. a tuft of hair must not be mistaken for the head --------- */

for (const tuft of [60, 110]) {
  m = await page.evaluate(async (t) => __measure(await __person({ tuft: t })), tuft);
  check(!m.rejected && m.headMm >= want * 0.95 && m.headMm <= most + 0.1,
    `a ${tuft} px hair tuft still gives a ${m.headMm ?? '—'} mm head${m.rejected ? ` (rejected: ${m.rejected})` : ''}`);
}

/* ---- 3. the photo is never scaled to absurdity ------------------- */

m = await page.evaluate(async () => __measure(await __person({ bodyTo: 0.55 })));
check(!m.rejected, `a closely cropped photo is still usable${m.rejected ? ` (rejected: ${m.rejected})` : ''}`);
if (!m.rejected) {
  check(m.headMm <= most + 0.1, `and its head is ${m.headMm} mm, not enlarged without limit (cap ${most.toFixed(2)})`);
  check(m.shortBy > 0 && m.warnings.some((t) => /stops short|cropped/i.test(t)),
    'and it says the photo is cropped too close to fill the card');
}

/* ---- 4. the card's own example is unchanged ---------------------- */

const ex = await page.evaluate(async () => {
  const res = await processPhotoFile(new File([await (await fetch(ASSETS.example)).blob()], 'e.png', { type: 'image/png' }));
  const pl = res.check.placement;
  const srcW = res.image.naturalWidth || res.image.width;
  return { headMm: +(res.check.head.width * (pl.w / srcW)).toFixed(2), w: +pl.w.toFixed(2) };
});
check(Math.abs(ex.headMm - want) < 0.05, `the built-in example still measures exactly ${ex.headMm} mm`);
check(Math.abs(ex.w - 37.85) < 0.05, `and still places at ${ex.w} mm wide, as the golden card expects`);

/* ---- done --------------------------------------------------------- */

await browser.close();
if (errors.length) { console.error('\npage errors:'); errors.forEach((e) => console.error('  ' + e)); }
if (fail.length || errors.length) {
  console.error(`\nFAIL — ${fail.length} check(s) failed${errors.length ? `, ${errors.length} page error(s)` : ''}`);
  process.exit(1);
}
console.log('\nPASS — the head is measured and sized the same whatever the hair is doing');
