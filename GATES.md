# Gates: rebuild the ID Maker as a native Windows app

OWNS: windows/**, .github/workflows/windows.yml, docs/STATE-MACHINES.md, AGENTS.md, GATES.md, README.md

Scope: The web ID Maker rebuilt as a native C# / WPF Windows program that prints the same card, signs full-screen with pen pressure from the tablet driver, removes photo backgrounds on the computer, and installs with the XP-Pen driver.

- [x] G1: Every rule about the card passes its tests, held to values captured from the web app (QR module for module, sheet plans, names, the example photo's placement), and the real background-removal model finds the person in an ordinary photo.
  CHECK: dotnet test windows/IdMaker.Tests
  EXPECT: /Passed!\s+- Failed:\s+0, Passed:\s+1\d\d/
  EVIDENCE: 104 passed, 0 failed, on Linux and on windows-latest (run 43996986781, step "Core tests").

- [x] G2: The whole solution, including the WPF program and its Windows-only tests, compiles with warnings treated as errors.
  CHECK: dotnet build windows/IdMaker.sln
  EXPECT: Build succeeded
  EVIDENCE: Build succeeded locally (compiled for Windows from Linux) and on windows-latest.

- [ ] G3: On Windows, the natively drawn example card matches the card the web app printed, zone by zone, with the same fonts, line breaks and sizes.
  CHECK: dotnet test windows/IdMaker.App.Tests --filter "FullyQualifiedName~RenderTests"
  EXPECT: /Passed!\s+- Failed:\s+0/
  CWD: .
  EVIDENCE: pending

- [ ] G4: On Windows, the real window takes a card from an empty form to a saved PDF, refuses to move on with the reason, issues nothing when the save fails, and every tool opens.
  CHECK: dotnet test windows/IdMaker.App.Tests --filter "FullyQualifiedName~WindowTests"
  EXPECT: /Passed!\s+- Failed:\s+0/
  EVIDENCE: pending

- [ ] G5: The package installs for the signed-in user without an administrator, carries XP-Pen's driver with the pinned checksum, and the installed program starts and shows its window.
  CHECK: pwsh -File windows/installer/package.ps1; pwsh -File windows/installer/selftest.ps1
  EXPECT: Installed, started and removed.
  EVIDENCE: pending

- [ ] G6: Signing with the XP-Pen Deco Fun draws a line that thickens with pressure, with "Windows Ink" both ticked and unticked in the XP-Pen settings, and the pen cannot leave the signing screen.
  EVIDENCE: pending

- [ ] G7: The installer, run on an office computer with no tablet driver, installs XP-Pen's driver after one Windows permission prompt and the tablet works afterwards.
  EVIDENCE: pending

- [ ] G8: A card printed at 100% from the native app measures the same as one printed from the web app.
  EVIDENCE: pending

- [ ] G9: Real staff photos, taken against an ordinary wall, come out of the background remover clean enough to print.
  EVIDENCE: pending

ABANDON: G6 needs the tablet in someone's hand; the machine this was built on is not Windows and has no tablet. The pressure path was written from the WinTab specification and the WPF stylus API, and compiles; it has not been drawn with.
ABANDON: G7 needs an office computer and an administrator to answer the prompt. The driver installer was downloaded and confirmed to be Inno Setup, whose silent switches are the ones used; the install itself was not run.
ABANDON: G8 needs a printer and a ruler. The PDF's page size and pixel dimensions are tested (638 × 1011 at 54 × 85.6 mm).
ABANDON: G9 needs real photographs of real staff, which must not leave the company. The model was measured on the repository's sample portrait over test backgrounds only.
