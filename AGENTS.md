# For agents working in this repository

Alpha Premier ID Maker makes employee ID cards. The app is the native
Windows program in `windows/` (C#, .NET 8, WPF). Start with
`windows/README.md`; the people who use the app read `README.md`.

It replaced a web app (plain HTML and TypeScript in a Tauri window),
removed on 17 Nov 2026 and still in git history. `test/golden-front.png`
is the card that web app printed, and
`windows/IdMaker.Tests/Fixtures/web-reference.json` the numbers it
produced; the native app is held to both.

## Rules

- **Native only.** No web frontend, no browser shell, no WebView. A web
  skill's advice is translated into WPF and XAML, not followed literally.
- **Prefer what is already there**: the .NET standard library and
  Windows' own features, before a package. The one dependency is ONNX
  Runtime, for background removal. Adding another needs a reason written
  down where it is added.
- **`IdMaker.Core` decides, `IdMaker.App` shows.** A rule about the card
  or the form goes in Core with a test that runs without a window. The
  window draws the `Session` and passes on what the operator does.
- **The printed card must not move by accident.** Anything that can
  change what is drawn is checked by `IdMaker.App.Tests` against
  `test/golden-front.png`. If it moves on purpose, say so in the commit.
- **Photographs are personal data.** Never store one, never send one
  anywhere, never write one to a log or a test report.
- **The register address is a credential.** It is never in the source:
  the release build takes it from the `REGISTER_URL` secret. Tests never
  call it; `Make.LocalRegister` and `FakeHttp` stand in.
- **Every cast carries a `// SAFETY:` comment** saying why it cannot
  fail. A deliberate shortcut carries `// shortcut: <its limit>, <when to
  revisit>`.
- **An exception is caught by type, where something can be done about
  it**, and the operator is told what to do next in plain words. Nothing
  is swallowed.
- Never commit a password, a key or an `.env` file. Never force-push
  `main`.

## Checks

```bash
dotnet build windows/IdMaker.sln
```

```bash
dotnet test windows/IdMaker.Tests
```

`IdMaker.App.Tests` needs Windows and runs in CI
(`.github/workflows/windows.yml`), which also packages the installer and
proves it installs and starts. Done means these pass locally, the change
is pushed, CI is watched, and anything it finds is fixed.

`GATES.md` lists what the native rebuild had to prove and what is still
waiting on a person with the hardware.

## How the Notion skills apply here

| Skill | Here |
| --- | --- |
| unlazy | `GATES.md`: gates written as commands with expected output, re-run before calling anything done |
| state-machine-bug-hunt | `docs/STATE-MACHINES.md`, kept current when a state or transition changes |
| untangle-codebase | the Core / App split; one place per decision (`FurthestStep`, `RenderTools`) |
| ponytail-review, ponytail-audit | findings as *what it is, the problem, the fix, what happens if skipped*; `shortcut:` comments |
| security-audit | findings only where a lower-trust input crosses a boundary: files opened or dropped, the register's answers, the installer's download |
| full-output-enforcement | no placeholders, no "rest follows the same pattern" |
| better-accessibility, fixing-accessibility | real controls, a visible focus ring, labels tied to inputs, errors as words beside the field, status announced, 40-pixel targets, Next never disabled |
| better-layout, better-typography, better-colors, better-ui, baseline-ui | `Theme.xaml`: two tiers of colour, four type sizes, spacing in steps, no animation, no gradients |
| better-writing | verb-first buttons that name the consequence, sentence case, errors that say how to recover |
| anti-slop | TypeScript-only as written; its principles are the `// SAFETY:` rule and parsing at the boundary (`Register.ParseEntry`) |
| i-have-adhd | how replies to Deign are shaped, not the app |
| grill-me, interface-review, break, variant | run by name, with a person answering |
| create-verification-skill | needs a real launch on Windows to count; not written yet |

The web-marketing and image-generation skills (brandkit, gpt-taste,
imagegen-*, image-to-code, stitch-design-taste, high-end-visual-design,
industrial-brutalist-ui, design-taste-frontend, fixing-metadata,
fixing-motion-performance, e2e, typesafe-ai) do not apply to a native
desktop tool.
