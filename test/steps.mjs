#!/usr/bin/env node
/* ------------------------------------------------------------------
   Step-flow test
   ------------------------------------------------------------------
   Six steps, each one needed before the next. The rule is easy to state
   and was easy to get wrong in three different places at once, because
   the Next button, the step chips and the draft that comes back after a
   reload each decided it for themselves.

   The one that mattered: whether a name fits the card cannot be worked
   out before the fonts load, so an unfittable name counted as finished,
   and a restored draft opened step 4 over a broken step 1 without ever
   mentioning the name.

   So this walks the ways forward and checks none of them goes past
   something unfinished, and that being refused says which step and why.

     npm run test:steps
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

const page = await browser.newPage({ viewport: { width: 1258, height: 900 } });
page.on('pageerror', (e) => errors.push(e.message));
await page.goto(app);
await page.waitForSelector('#card-box:not([hidden])', { timeout: 20000 });

const look = () => page.evaluate(() => ({
  step: state.step,
  id: STEPS[state.step].id,
  // optional on purpose: against an older build this has to report a
  // failed check, not a stack trace
  blocked: (document.getElementById('blocked')?.textContent || '').trim(),
  locked: [...document.querySelectorAll('.step-chip')].map((b) => b.getAttribute('aria-disabled') === 'true'),
  done: [...document.querySelectorAll('.step-chip')].map((b) => b.classList.contains('is-done')),
  focus: document.activeElement?.id || '',
}));

/* ---- 1. nothing opens past the first unfinished step -------------- */

let s = await look();
check(s.step === 0 && s.locked.slice(1).every(Boolean), 'on a blank card only step 1 is open');

await page.click('#btn-next');
await page.waitForTimeout(250);
s = await look();
check(s.step === 0, 'Next on a blank step 1 does not move');
check(/Can.t continue/.test(s.blocked) && /last name/i.test(s.blocked), `and says why: "${s.blocked}"`);
check(s.focus === 'f-last', `and puts the cursor on the field that is missing (${s.focus || 'nothing'})`);

// a locked chip is reachable and explains itself rather than doing nothing
await page.evaluate(() => document.querySelectorAll('.step-chip')[4].click());
await page.waitForTimeout(250);
s = await look();
check(s.step === 0, 'clicking a locked chip does not jump to it');
const label = await page.evaluate(() => document.querySelectorAll('.step-chip')[4].getAttribute('aria-label'));
check(/locked until step 1/i.test(label || ''), `and the chip says what it is waiting for: "${label}"`);

/* ---- 2. a step that comes undone pulls you back ------------------- */

await page.fill('#f-last', 'Reyes');
await page.fill('#f-first', 'Ana');
await page.click('#btn-next');
await page.waitForTimeout(250);
check((await look()).step === 1, 'a finished step lets you through');

await page.evaluate(() => { state.last = ''; render(); });
await page.click('#btn-next');
await page.waitForTimeout(300);
s = await look();
check(s.step === 0, 'breaking step 1 from step 2 pulls you back to step 1');
check(/step 1/i.test(s.blocked) && /Name/.test(s.blocked), `and names the step that broke: "${s.blocked}"`);

await page.fill('#f-last', 'Reyes');
await page.waitForTimeout(250);
check((await look()).blocked === '', 'fixing it takes the refusal back down');

/* ---- 3. a restored draft cannot open a step over a broken one ----- */

await page.evaluate(() => {
  state.last = 'Villanueva';
  state.first = 'Maximiliano Bartolome Encarnacion Buenaventura';   // cannot fit the card
  state.role1 = { pick: 'IT Associate', custom: '' };
  state.seq = '777';
  saveDraft();
});
await page.reload();
await page.waitForSelector('#card-box:not([hidden])', { timeout: 20000 });
await page.waitForFunction(() => !!state.templates, null, { timeout: 20000 });
await page.waitForTimeout(400);
s = await look();
check(s.step === 0, `a restored draft opens the first unfinished step, not a later one (opened ${s.id})`);
const nameFits = await page.evaluate(() => !problems().nameFit);
check(!nameFits && !s.done[0], 'the name is known not to fit, and step 1 does not claim to be finished');
check(s.locked.slice(1).every(Boolean), 'and every step after it is locked');

await page.evaluate(() => { state.first = 'Ana'; render(); });
await page.waitForTimeout(250);
check(!(await look()).locked[1], 'shortening the name unlocks the next step');

/* ---- 4. the cursor lands on what the step is about ---------------- */

await page.evaluate(() => { state.role1 = { pick: 'IT Associate', custom: '' }; render(); goStep(2); });
await page.waitForTimeout(250);
check((await look()).focus === 'f-seq', 'the number step lands on the number, not on the year');

// a real, complete card, so the later steps are genuinely open
await page.evaluate(() => loadExample());
await page.waitForFunction(() => state.photo && state.signature, null, { timeout: 20000 });
await page.waitForTimeout(400);
await page.evaluate(() => goStep(4));
await page.waitForTimeout(250);
s = await look();
check(s.id === 'sign', 'a finished card opens any step');
check(s.focus !== 'btn-clear-sig', `the signature step does not land on Clear (landed on ${s.focus || 'nothing'})`);

/* ---- done --------------------------------------------------------- */

await browser.close();
if (errors.length) { console.error('\npage errors:'); errors.forEach((e) => console.error('  ' + e)); }
if (fail.length || errors.length) {
  console.error(`\nFAIL — ${fail.length} check(s) failed${errors.length ? `, ${errors.length} page error(s)` : ''}`);
  process.exit(1);
}
console.log('\nPASS — no way forward opens a step over an unfinished one');
