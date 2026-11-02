#!/usr/bin/env node
/* ------------------------------------------------------------------
   Duplicate-number test
   ------------------------------------------------------------------
   Issuing two cards on one number is the worst thing this app can do,
   and the check that prevents it used to be the slowest thing in the
   form: a round trip to Google on every keystroke, during which the
   number looked free. Press Next quickly enough and you were past the
   step before the register answered.

   The register is now read whole, once, so the answer is a Map lookup.
   This checks the four things it can say, and that only one of them
   lets you carry on:

     a number already in the register   -> refuses, and names who has it
     a number that is not               -> opens, with no network at all
     not known yet                      -> refuses, and says it is asking
     register unreachable               -> opens, with a warning

   The register is stubbed in the page: the real one is a live sheet for
   a real company and a test has no business writing to it, or reading
   it a thousand times.

     npm run test:duplicate
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

/** Replace the register's one network call, and say how it should behave. */
const stub = (mode) => page.evaluate((how) => {
  Register._index = null;
  Register._loading = null;
  window.__calls = 0;
  const taken = { id: employeeId(2026, 500), name: 'Dela Cruz, Juan', issuedAt: Date.UTC(2026, 2, 3) };
  Register._fetch = (url) => {
    window.__calls++;
    if (how === 'down') return Promise.reject(new Error('no'));
    const rows = String(url).includes('action=list') ? [taken] : null;
    return how === 'slow'
      ? new Promise((res) => setTimeout(() => res(rows), 4000))
      : Promise.resolve(rows);
  };
}, mode);

/** Fill steps 1-2 and open the number step. */
const toNumber = () => page.evaluate(() => {
  state.last = 'Reyes'; state.first = 'Ana';
  state.role1 = { pick: 'IT Associate', custom: '' };
  state.year = 2026; state.seq = ''; state.reissue = false;
  render(); goStep(2);
});

const type = async (seq) => {
  await page.fill('#f-seq', '');
  await page.fill('#f-seq', seq);
  await page.waitForTimeout(60);
};

const look = () => page.evaluate(() => ({
  seqProblem: problems().seq,
  numberDone: stepDone(2, problems()),
  step: state.step,
  shared: state.dupShared === undefined ? 'unreachable'
        : state.dupShared === 'pending' ? 'pending'
        : state.dupShared === null ? 'absent' : 'taken',
  calls: window.__calls,
  blocked: (document.getElementById('blocked')?.textContent || '').trim(),
  photoLocked: document.querySelectorAll('.step-chip')[3].getAttribute('aria-disabled') === 'true',
}));

/* ---- 1. a number already in the register ------------------------- */

await stub('ok');
await page.evaluate(() => Register.loadIndex());
await page.waitForFunction(() => !!Register._index, null, { timeout: 10000 });
await toNumber();
await type('500');
let s = await look();
check(s.shared === 'taken' && /already issued/i.test(s.seqProblem), `a taken number is refused: "${s.seqProblem.slice(0, 54)}…"`);
check(/Dela Cruz/.test(s.seqProblem), 'and the message names who already has it');
check(!s.numberDone && s.photoLocked, 'the number step is not finished, and the photo step stays locked');

const callsBefore = s.calls;
await page.click('#btn-next');
await page.waitForTimeout(250);
s = await look();
check(s.step === 2, 'Next will not leave the step while the number is taken');
check(/Can.t continue/.test(s.blocked), `and says so: "${s.blocked.slice(0, 48)}…"`);

/* ---- 2. a free number, answered without asking anybody ------------ */

await type('501');
s = await look();
check(s.shared === 'absent' && s.seqProblem === '', 'a free number clears immediately');
check(s.calls === callsBefore, `and asks the register nothing (${s.calls} call${s.calls === 1 ? '' : 's'} in total, all at startup)`);
await page.click('#btn-next');
await page.waitForTimeout(250);
check((await look()).step === 3, 'and the step opens');

/* ---- 3. not known yet is not the same as free -------------------- */

await stub('slow');
await toNumber();
await type('500');
s = await look();
check(s.shared === 'pending' && /Checking the shared register/i.test(s.seqProblem),
  'while the register has not answered, the number is not cleared');
check(!s.numberDone, 'so the step is not finished');
await page.click('#btn-next');
await page.waitForTimeout(250);
check((await look()).step === 2, 'and Next will not go past it');

// and when the slow answer finally lands, it is a duplicate after all
await page.waitForFunction(() => state.dupShared && typeof state.dupShared === 'object', null, { timeout: 15000 });
s = await look();
check(/already issued/i.test(s.seqProblem), 'the late answer still refuses the number');

/* ---- 4. a register that cannot be reached must not stop work ----- */

await stub('down');
await toNumber();
await type('502');
await page.waitForFunction(() => state.dupShared !== 'pending', null, { timeout: 15000 });
s = await look();
check(s.shared === 'unreachable' && s.seqProblem === '',
  'an unreachable register warns rather than blocking');
const help = await page.evaluate(() => document.getElementById('f-seq-msg').textContent);
check(/could not reach/i.test(help), `and says what happened: "${help}"`);
await page.click('#btn-next');
await page.waitForTimeout(250);
check((await look()).step === 3, 'and HR can carry on working');

/* ---- done --------------------------------------------------------- */

await browser.close();
if (errors.length) { console.error('\npage errors:'); errors.forEach((e) => console.error('  ' + e)); }
if (fail.length || errors.length) {
  console.error(`\nFAIL — ${fail.length} check(s) failed${errors.length ? `, ${errors.length} page error(s)` : ''}`);
  process.exit(1);
}
console.log('\nPASS — a number already issued cannot get past the number step');
