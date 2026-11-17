# State machines in the Windows app

Every part of the native app (`windows/`) that can be in one of several
states, written down: the states, what must always be true, what moves
it from one to another, and the invalid states that were actually found
to be reachable. Paths are relative to `windows/`.

Each finding is marked **Confirmed** (traced through the code, with the
test that now fails without the fix) or **Suspected** (with what would
confirm it).

Machines:

1. [The form's steps](#1-the-forms-steps)
2. [The duplicate check](#2-the-duplicate-check)
3. [The shared register's index](#3-the-shared-registers-index)
4. [Writes to the shared register](#4-writes-to-the-shared-register)
5. [A photo being looked at](#5-a-photo-being-looked-at)
6. [Saving a print](#6-saving-a-print)
7. [The tool drawer and the map editor](#7-the-tool-drawer-and-the-map-editor)
8. [The card design kept on disk](#8-the-card-design-kept-on-disk)
9. [A stroke on the signature pad](#9-a-stroke-on-the-signature-pad)
10. [The pen tablet context](#10-the-pen-tablet-context)

Issues, ranked by what they could cost, are at the [end](#issues).

---

## 1. The form's steps

**Purpose.** Which of the six steps is open. **Code:** `IdMaker.Core/Session.cs`
(`FurthestStep`, `GoStep`, `NextStep`, `ChipPressed`, `AddAnother`,
`CancelAdding`, `RemoveFromQueue`, `ClearCard`).

**States.** `StepIndex` 0–5: Name, Job title, Number, Photo, Signature,
Check. On Check there are two sub-states the screen tells apart: a
finished card on the form (`Ready()`), or a blank form with cards in
`Queue` ("printing what is already queued").

**Invariants.**

- I1. `StepIndex ≤ FurthestStep()` — no step is open over an unfinished
  one — except after a forced move.
- I2. A forced move happens in exactly two places: `LoadExample` (the
  example is complete) and `CancelAdding` (the print it returns to is
  already made).
- I3. On Check there is always something to print: `JobCards()` is not
  empty.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| any | Next | a step before this one is unfinished | first unfinished step | refusal names that step | `NextStep` |
| n | Next | this step unfinished | n | its fields marked touched; refusal names the reason | `NextStep` |
| n < 5 | Next | this step finished | n + 1 | arriving at 5 fixes `ReviewEntry` | `NextStep` → `GoStep` |
| any | chip i | i ≤ furthest | i | | `ChipPressed` |
| any | chip i | i > furthest | furthest | refusal names the step in the way | `ChipPressed` |
| n > 0 | Back | | n − 1 | | `Back` |
| 5 | Add another ID | card finished | 0 | card moved to `Queue`; form cleared | `AddAnother` |
| < 5 | Stop adding | queue not empty | 5 (forced) | form cleared, queue kept | `CancelAdding` |
| 5 | Remove queued card | it was the last, and the form is blank | 0 | | `RemoveFromQueue` |
| 5 | Start a new ID | print saved | 0 | form and queue cleared | `NewId` |
| any | register answers late | the number is a duplicate | 2 | refusal about step 3 | `SettleDuplicate` |

**Invalid states and how they are prevented.**

- *A later step open over an unfinished earlier one.* Every unforced move
  is capped in `GoStep`; `NextStep` and `ChipPressed` send the form back
  when something behind it has come undone.
- *Check open with nothing to print* — see issue 4.

---

## 2. The duplicate check

**Purpose.** What is known about the number on the form. **Code:**
`IdMaker.Core/Session.cs` (`RefreshDuplicate`, `SettleWhenLoaded`,
`SettleDuplicate`, `Duplicate`).

**States.** `DupShared`: `Free`, `Taken` (with `SharedEntry`), `Pending`,
`Unreachable`. Alongside it `DupLocal` (this computer's list) and the
queue of the print in progress.

**Invariants.**

- I1. `Pending` is a problem on the Number step: not knowing is not the
  same as knowing the number is free.
- I2. `Unreachable` is not a problem: a sheet being down cannot stop HR
  working, and the local list still catches repeats from this computer.
- I3. An answer is applied only to the number it was asked about
  (`_dupToken`).
- I4. `Taken` blocks unless "Replacement card" is ticked; a number
  already in this print's queue blocks regardless.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| any | number or year typed | no shared register, or number invalid | Free | token advanced | `RefreshDuplicate` |
| any | number or year typed | index in memory | Free / Taken | answered at once, no request | `RefreshDuplicate` |
| any | number or year typed | index not read yet | Pending | one read started | `RefreshDuplicate` |
| Pending | read succeeds | token unchanged | Free / Taken | may send the form back to step 3 | `SettleWhenLoaded` |
| Pending | read fails | token unchanged | Unreachable | | `SettleWhenLoaded` |
| Pending | read finishes | token changed | (unchanged) | answer dropped | `SettleWhenLoaded` |

**Invalid transitions prevented.** Pressing Next twice quickly while
`Pending` cannot reach the Photo step (I1; test
`While_the_register_has_not_answered_the_number_is_not_cleared`). An
answer for a number since deleted is dropped (I3; test
`An_answer_for_a_number_that_has_since_changed_is_ignored`).

---

## 3. The shared register's index

**Purpose.** The whole shared register, read once and kept in memory.
**Code:** `IdMaker.Core/Register.cs` (`LoadIndex`, `LoadIndexCore`,
`SetUrl`, `FindSharedNow`, `NoteShared`).

**States.** Implicit, from two fields: `_index` (null or a dictionary)
and `_loading` (null or a task). *Unread* (null, null), *Reading*
(null, task), *Read* (index, null), *Refreshing* (index, task — only
after `LoadIndex(force: true)`).

**Invariants.**

- I1. Any number of callers while *Reading* share one request.
- I2. `_index` only ever holds rows from the address currently set.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| Unread | `LoadIndex` | shared | Reading | one GET `?action=list` | `LoadIndex` |
| Reading | `LoadIndex` | not forced | Reading | same task returned | `LoadIndex` |
| Reading | answer, rows read | address unchanged | Read | | `LoadIndexCore` |
| Reading | answer | address changed meanwhile | Unread | rows dropped | `LoadIndexCore` |
| Reading | failure | | Unread | caller told "not reached" | `LoadIndexCore` |
| any | `SetUrl` | | Unread | address version advanced | `SetUrl` |
| Read | card issued here | | Read | entry added, no re-read | `NoteShared` |

**Invalid state found.** `_index` holding the *old* register's rows after
the address changed — issue 2.

---

## 4. Writes to the shared register

**Purpose.** Getting every issued card into the shared sheet, including
those issued while it could not be reached. **Code:**
`IdMaker.Core/Register.cs` (`RecordShared`, `SendWaiting`, the
`register-outbox` store key).

**States per issued card.** *Local only* (in this computer's list),
*Waiting* (also in the outbox), *Shared* (written to the sheet).

**Invariants.**

- I1. A card that was issued and not written to the sheet is in the
  outbox. It is never simply dropped.
- I2. A card is in the outbox at most once.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| Local only | write attempted | succeeds | Shared | | `RecordShared` |
| Local only | write attempted | fails | Waiting | kept on disk; operator told | `RecordShared` |
| Waiting | app starts, register reached | write succeeds | Shared | removed from outbox | `SendWaiting` |
| Waiting | resend | fails | Waiting | the rest are left for next time | `SendWaiting` |

**Invalid state found.** *Issued, not shared, and forgotten* — issue 1.

---

## 5. A photo being looked at

**Purpose.** A chosen picture on its way through decoding, background
removal and placement, off the window's thread. The same shape applies
to a scanned signature being read. **Code:**
`IdMaker.App/MainWindow.xaml.cs` (`AcceptPhotoFile`,
`AcceptSignatureFile`), `IdMaker.Core/Session.cs` (`CardSerial`,
`AcceptPhoto`, `PhotoFailed`).

**States.** *Idle*, *Looking* (`_checkingPhoto`), then one of *Accepted*
(`Session.Photo` set), *Refused* (`PhotoError` set).

**Invariants.**

- I1. One picture at a time: a second is not started while *Looking*.
- I2. A result is applied only to the card it was started for.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| Idle | picture chosen or dropped | wrong type or too large | Refused | reason shown | `AcceptPhotoFile` |
| Idle | picture chosen or dropped | | Looking | button says so and is disabled | `AcceptPhotoFile` |
| Looking | picture chosen again | | Looking | ignored | `AcceptPhotoFile` |
| Looking | finished | card serial unchanged, usable | Accepted | manual adjustment reset | `AcceptPhoto` |
| Looking | finished | card serial unchanged, not usable | Refused | reason shown | `AcceptPhoto` / `PhotoFailed` |
| Looking | finished | card serial changed | Idle | result dropped | `AcceptPhotoFile` |

**Invalid transition found.** A result landing on a different card —
issue 3.

---

## 6. Saving a print

**Purpose.** Turning the print on the Check step into a PDF and issuing
its cards. **Code:** `IdMaker.App/MainWindow.Print.cs` (`SavePrint`,
`WritePdf`), `IdMaker.Core/Session.cs` (`MarkIssued`, `RecordShared`).

**States.** *Reviewing*, *Saving* (`_saving`), *Saved* (`_saved`),
*Failed* (back to Reviewing with an error shown).

**Invariants.**

- I1. Cards are issued (written to the register, queue emptied) only
  after the PDF is complete on disk.
- I2. A half-written PDF never has the final file's name: it is written
  as `name.pdf.part` and renamed.
- I3. One save at a time.
- I4. The example card is never issued.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| Reviewing | Save the PDF | a card to print | Saving | button says so and is disabled | `SavePrint` |
| Saving | Save pressed again | | Saving | ignored | `SavePrint` |
| Saving | file written and renamed | | Saved | cards issued locally; shared writes begin | `MarkIssued` |
| Saving | write fails | | Reviewing | `.part` removed; error says how to recover; nothing issued | `WritePdf` |
| Saved | Start a new ID | | (step 1) | | `NewId` |

Saving as pictures never issues a card: pictures are a proof to look at.

**Checked and holding.** A print that cannot be saved issues nothing
(test `A_print_that_cannot_be_saved_issues_nothing`). The web app could
not make this promise — a browser download gives no answer — so it
recorded the cards the moment the download was started.

---

## 7. The tool drawer and the map editor

**Purpose.** Which of the four tools is open, and whether the boxes on
the card are being dragged. **Code:** `IdMaker.App/MainWindow.Print.cs`
(`RenderTools`, `StartAdjusting`, `StopAdjusting`, the three design
buttons).

**States.** `_tool`: none, register, batch, printer, design. Editing:
`MapOverlay` visible or not.

**Invariants.**

- I1. Editing implies `_tool == design`. The "Keep this map" button lives
  in that drawer; without it there is no way to finish.
- I2. The design tool is only reachable on step 1 of a blank card with
  nothing queued (`Session.DesignAllowed`): a photo placed against one
  map is wrong for another.
- I3. While editing, the form is disabled.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| any tool | rail button | same tool | none | focus returns to the rail | `Rail_Click` |
| any | rail button | different tool | that tool | editing stopped if it was on | `RenderTools` |
| design | Show the map | design allowed | design + editing | snapshot taken for Discard | `StartAdjusting` |
| editing | Keep this map | | design | saved, or cleared if nothing moved | `StopAdjusting` |
| editing | Discard changes | | design | snapshot restored | `BtnTplCancel_Click` |
| editing | Close, Escape, another tool | | that state | changes kept, as with Keep | `RenderTools` |
| design | card started | | none | drawer closed | `RenderTools` |

**Checked and holding.** Closing the drawer by another route while
editing does not strand the boxes on the card with the form disabled
(test `Every_tool_opens_and_the_map_can_be_moved_kept_and_reset`). This
is the bug the web app's `renderTools` comment describes; the one place
that enforces it was carried over.

---

## 8. The card design kept on disk

**Purpose.** A moved map and replaced artwork surviving a restart.
**Code:** `IdMaker.Core/Geometry.cs` (`TemplateStore.Save`, `Restore`,
`Clear`), `IdMaker.App/MainWindow.xaml.cs` (`Boot`),
`IdMaker.App/MainWindow.Print.cs` (`UseArtwork`, `BtnTplReset_Click`).

**States.** Two things on disk: `template.json` (the record) and
`template-front.png` / `template-back.png` (the pictures). *Built-in*
(neither), *Map moved* (record, `artwork: false`), *Own artwork* (record
with `artwork: true`, and both pictures).

**Invariant.** A record saying `artwork: true` is only acted on when
both pictures open.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| any | Use this artwork | both card-shaped | Own artwork | pictures written first, then the record | `UseArtwork` |
| any | Keep this map | something moved | Map moved / Own artwork | record written | `StopAdjusting` |
| any | Go back to the built-in design | confirmed | Built-in | record removed, then pictures | `BtnTplReset_Click` |
| on start | record unreadable or incomplete | | Built-in | record removed | `TemplateStore.Restore` |
| on start | record says artwork, a picture will not open | | Built-in | record removed | `Boot` |

**Failure between two writes.** Pictures are written before the record,
and the record is removed before the pictures. A crash in between leaves
pictures with no record — harmless, nothing points at them — never a
record with no pictures that is trusted.

---

## 9. A stroke on the signature pad

**Purpose.** One line being drawn. **Code:** `IdMaker.App/SignaturePad.cs`.

**States.** *Idle* (`_current` null), *Pen stroke* (`_current` set,
`_stylusStroke` true), *Mouse stroke* (`_current` set, `_stylusStroke`
false).

**Invariants.**

- I1. One stroke at a time, from one kind of device.
- I2. Every stroke that begins also ends, and ends exactly once: losing
  the pointer mid-stroke finishes it rather than leaving it open.
- I3. Points stay inside the pad (they are clamped to it).

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| Idle | pen down | pad can be drawn on | Pen stroke | pen captured | `OnStylusDown` |
| Idle | mouse down | not a pen in disguise | Mouse stroke | mouse captured | `OnMouseLeftButtonDown` |
| Pen stroke | mouse events | | Pen stroke | ignored: Windows sends both for a pen | mouse handlers |
| stroke | move | moved more than jitter | stroke | point added, with pressure if there is any | `Extend` |
| stroke | up | | Idle | stroke kept; `InkChanged` | `End` |
| stroke | capture lost | | Idle | stroke kept; `InkChanged` | `OnLost…Capture` |
| stroke | button found released on a move | | Idle | stroke kept | `OnMouseMove` |
| any | Clear | | Idle | strokes dropped; `InkChanged` | `Clear` |

`End` does nothing when no stroke is open, which is what makes "exactly
once" hold when an up and a lost-capture both arrive.

---

## 10. The pen tablet context

**Purpose.** The connection to the tablet driver's WinTab interface while
the signing window is open. **Code:** `IdMaker.App/PenTablet.cs`.

**States.** *None* (no driver, no tablet, or it refused), *Open and
listening* (window active), *Open and paused* (window not active),
*Closed* (`_context` zero).

**Invariants.**

- I1. Nothing depends on it: in every state signing works, by speed when
  there is no pressure to be had.
- I2. A pressure value is only used while it is fresh (120 ms) and the
  pen has not left the tablet.
- I3. Once *Closed*, no call into the driver is made again.

| From | Event | Guard | To | Side effects | Code |
| --- | --- | --- | --- | --- | --- |
| None | signing window opens | driver present, tablet answers, reports pressure | Open and listening | window hooked for packets | `TryOpen` |
| listening | packet | for this context | listening | pressure and time kept | `Hook` |
| listening | pen leaves the tablet | | listening | pressure marked stale | `Hook` |
| listening | window loses focus | | paused | | `SetActive(false)` |
| paused | window regains focus | | listening | context brought to the top | `SetActive(true)` |
| open | window closes | | Closed | hook removed, context closed | `Dispose` |
| open | the driver faults | | Closed | as above | `Hook`, `SetActive` |

---

## Issues

Ranked by what each could cost. All were fixed with a test that fails
without the fix.

### 1. A card saved while the sheet was down was never written to it — Confirmed

**Machine 4.** `RecordShared` returned `false` on failure and nothing
kept the entry (this is how the web app behaved too: `recordShared` in
`src/register.ts` swallowed the failure). The card was issued, printed
and recorded on one computer, and no other computer would ever learn the
number was taken — the duplicate the whole register exists to prevent.

**Fix.** Failed writes go to an outbox on disk and are sent again the
next time the app starts with the register in reach; the operator is
told at the time. **Test:**
`A_card_saved_while_the_sheet_is_down_is_kept_and_sent_when_it_is_back`.

### 2. Rows from the old register kept after the address changed — Confirmed

**Machine 3.** `SetUrl` cleared `_index` and `_loading`, but a read
already in flight finished afterwards and stored its rows — the *old*
sheet's — as the index for the new address. Numbers would then be
checked against the wrong register. Reachable by pressing "Test and
save" in the first seconds after start, while the opening read is still
out.

**Fix.** Each read carries the address version it started under and
stores nothing if that has moved on. **Test:**
`Changing_the_address_while_a_read_is_in_flight_does_not_keep_the_old_register_s_rows`.

### 3. A photo still being looked at could land on a different card — Confirmed

**Machine 5.** With a card queued, choose a photo for the next one and
press "Stop adding and go back to the print" before it finishes (large
photos take a second or more with background removal). `CancelAdding`
cleared the form; the result then arrived and was applied to the blank
form, leaving a half-started card on the Check step that the queued
print no longer matched.

**Fix.** `Session.CardSerial` goes up whenever the form is cleared; a
result whose serial is stale is dropped. The same guard covers a scanned
signature. **Test:**
`Putting_the_card_aside_changes_its_serial_so_late_work_for_it_can_stand_down`
covers the serial; the window's use of it is two lines traced by hand
(`AcceptPhotoFile`, `AcceptSignatureFile`).

### 4. The Check step left open with nothing to print — Confirmed

**Machine 1, invariant I3.** After "Stop adding" the form is blank and
the Check step shows the queue. Removing the last queued card from there
left the Check step open with no cards: a Save button with nothing to
save. Present in the web app (`removeFromQueue` in `src/app.js`).

**Fix.** Removing the last card from a blank Check step returns to
step 1. **Test:**
`Cancelling_an_extra_id_returns_to_the_print_that_was_waiting`.

### 5. Cards issued before the file was known to be saved — Confirmed (in the web app; prevented here)

**Machine 6, invariant I1.** Not a bug in this code — a property the
port had to establish rather than inherit. See machine 6.

### Not confirmed

- **Suspected: a tablet driver that stops sending pointer events to a
  window once a WinTab context is open on it.** Some drivers treat the
  two as exclusive. If XP-Pen's does, a pen with "Windows Ink" ticked
  would draw with the pressure from WinTab (through the mouse path)
  rather than fail, because the pad falls back by design — but this has
  not been tried on the hardware. **What would confirm it:** signing
  with the Deco Fun, Windows Ink on, and seeing no pressure variation.
