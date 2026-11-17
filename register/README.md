# Shared register

Every card the ID Maker issues is written to one Google Sheet, and every
number typed is checked against it. That sheet, not the laptop, is the
record of who holds which ID.

**It is already set up and built into the app — there is nothing to do.**
These notes are for rebuilding it, moving it, or replacing the address.
Set-up takes about ten minutes and needs no server.

## 1. Deploy the script in your Sheet

1. Open the register spreadsheet in Google Sheets.
2. *Extensions → Apps Script*. Delete the sample code, paste the whole of
   `Code.gs` from this folder, and save.
3. Rename the project from *Untitled project* to `APG ID Register` — that
   name is what Google shows on the permission screen and in the account's
   list of connected apps, so leaving it blank makes it unidentifiable
   later.
4. *Deploy → New deployment* → type **Web app**.
   - Description: `ID register`
   - Execute as: **Me**
   - Who has access: **Anyone**. The dropdown offers *Only myself*
     (the default), *Anyone with Google account* and *Anyone*, and only
     the last one works. The app calls the script without being signed
     in to Google, so either of the other two answers with a login page
     instead of the register, and every check fails. (The app says so
     when that happens.) Opening the address in your own browser tab is
     not a test of this: you are signed in there, so it answers you
     whatever the setting. See the warning below for what "Anyone" means.
5. Google asks for permission and warns that *this app hasn't been verified*.
   That is expected: the "app" is the script you just pasted into your own
   spreadsheet, and the developer it names is you. Click **Advanced**, then
   **Go to APG ID Register (unsafe)**, then **Allow**. It is asking for
   Sheets access because it reads and writes the `Register` tab.
6. Copy the **Web app URL**. It looks like
   `https://script.google.com/macros/s/AKfy…/exec` and must end in `/exec`.

Check it: open that URL in a browser. You should see
`{"ok":true,"service":"apg-id-register","rows":0}` and a `Register` tab
appears in the Sheet.

## 2. Tell the app about it

The address is never written into the source. The release build puts it
into the app from a repository secret:

1. On GitHub, open the repository's **Settings → Secrets and variables →
   Actions → New repository secret**.
2. Name it `REGISTER_URL` and paste the web-app address as the value.

Every release built after that has the register built in, so nobody has
to set anything up and it cannot be switched off by accident. A release
without the secret stops with a message saying so, rather than shipping
an app with no shared register. Builds on a developer's computer, and the
checks run on pull requests, have no built-in register: they keep their
own list.

> **Treat that address as a password.** The deployment is open to "Anyone",
> so whoever holds the address can read every issued ID and add rows.
> Anyone holding the released `.exe` can still dig it out, because the app
> has to call it, so hand the app only to the people who issue cards.
>
> If it leaks: *Deploy → Manage deployments*, archive the deployment,
> create a new one, and put the new address in the `REGISTER_URL` secret.
> The old one stops working the moment it is archived. Bump the version to
> release a build with the new one; every computer on the old build needs
> the new address saved once (below) or the new version installed.

One computer can be pointed somewhere else without rebuilding: **Issued
IDs → Shared register address → Test and save**. That overrides the
built-in address on that computer only; clearing the box goes back to it.

## What changes in the app

- Typing a number now also asks the Sheet. If it is already there, the
  field turns red with who has it and when, and a **Replacement card** tick
  box appears for genuine re-prints.
- Every downloaded card — single or batch — is written to the Sheet, and
  still kept locally.
- If the Sheet cannot be reached (offline, wrong address, deployment
  archived), the app says so under the number field and falls back to the
  local list. It never blocks issuing a card.

## Importing what was issued before

Paste older IDs straight into the `Register` tab, one per row. The full
column order is:

```
id, year, seq, name, title, issuedAt, replacement, recordedBy,
last, first, middle, suffix, role1, role2
```

Only **`id`** has to be filled in — that is the column the duplicate check
matches on (`APG-2026-122`). `name` and `issuedAt` make the warning message
useful ("already issued to … on …"), and `issuedAt` can be a date cell. The
rest can be left blank for historical rows.

## Changing the script later

After editing `Code.gs`, use *Deploy → Manage deployments → ✎ → Version:
New version → Deploy*. The URL stays the same, so the computers keep
working.
