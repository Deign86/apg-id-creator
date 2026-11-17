# The Windows app

Alpha Premier ID Maker as a native Windows program: C# on .NET 8, drawn
with WPF. No browser inside it and no web page: the card, the form, the
PDF and the signing screen are all Windows' own.

It replaces the web app at the top of this repository (`src/`,
`Alpha Premier ID Maker.html`) and the Tauri window around it (`app/`).
Those are still here until this one has been used on a real card run;
see [What is still to do](#what-is-still-to-do).

The people who use the app want the **[README at the top](../README.md)**.
This page is for whoever changes it.

---

## What is where

```
windows/
  IdMaker.sln
  IdMaker.Core/        every rule about the card, with no window attached
    Config.cs            EVERY measurement: card size, text boxes, photo
                         placement, QR, signature box, number format,
                         register address, the AI photo prompt
    Session.cs           the form: what is typed, what is wrong with it,
                         which step may be open, what is waiting to print
    TextFit.cs           shrinks and wraps the name and title to fit
    Photo.cs             the photo pipeline: classify the background, key
                         out green or blue, find the head, place it
    Signature.cs         strokes (with pen pressure) and scans, as ink
    Geometry.cs          the card design in use: the map, and own artwork
    Sheet.cs             how many cards fit on a sheet, and where
    Pdf.cs               writes the PDF by hand, any number of pages
    Qr.cs, QrTables.cs   the QR code
    Register.cs          issued IDs: this computer's list, the shared sheet
    Batch.cs             many cards from a CSV
    Store.cs             small named values kept between runs; job titles
  IdMaker.Vision/      background removal (the only outside dependency)
    BackgroundRemover.cs runs the portrait-matting model
    Models/              the model, fetched by the build; see its README
  IdMaker.App/         the program
    MainWindow.xaml      the window's layout
    MainWindow.xaml.cs   the form, the preview, photo and signature steps
    MainWindow.Print.cs  the last step and the four tools
    CardRenderer.cs      draws front, back and sheets at any resolution
    SignaturePad.cs      the pad, and the full-screen signing window
    PenTablet.cs         pen pressure straight from the tablet driver
    MapEditor.cs         the draggable boxes over the card
    Theme.xaml           colours, type and control styles
    Fonts/               the three card fonts (see its README)
  IdMaker.Tests/       tests that run anywhere (Linux, macOS, Windows)
  IdMaker.App.Tests/   tests that need Windows: the renderer, the window
  installer/           Install.ps1, the pinned driver, the packaging script
```

The rule that shapes it: **`IdMaker.Core` decides, `IdMaker.App` shows.**
Anything that can be wrong about a card — a name that does not fit, a
number already issued, a step opened too early — is decided in Core and
tested there without a window. The window redraws itself from the
`Session` every time something changes (`Render()`), so no two parts of
the screen can disagree about where things stand.

Core has no dependencies at all and no image codec; the app hands it
pixels. Vision depends on ONNX Runtime and nothing else.

---

## Building, testing, packaging

You need the **.NET 8 SDK**. Nothing else.

```bash
dotnet build windows/IdMaker.sln
```

```bash
dotnet test windows/IdMaker.Tests
```

```bash
dotnet test windows/IdMaker.App.Tests
```

The first builds everywhere — on Linux or a Mac it compiles the Windows
program without being able to run it. The second runs everywhere. The
third needs Windows.

The first build downloads the background-removal model (25 MB) into
`IdMaker.Vision/Models/` and refuses it unless its checksum is the one in
the project file.

To run the app while working on it:

```bash
dotnet run --project windows/IdMaker.App
```

To make the package HR is given (on Windows, in PowerShell):

```powershell
./windows/installer/package.ps1
```

That writes `windows/dist/AlphaPremierIdMaker-setup.zip`: the app with
.NET built in, `Install.cmd`, and XP-Pen's driver. CI builds the same
zip on every push and keeps it as an artifact named
`AlphaPremierIdMaker-setup`.

### What the tests hold the app to

`IdMaker.Tests/Fixtures/web-reference.json` holds values captured from
the web app (version 1.20.0) with its example card loaded. The native app
is held to them:

| | Held to |
| --- | --- |
| QR code | the same modules, one for one, as the library the web app used |
| Sheets | the same plan for every paper and layout |
| Names, titles, file names | the same answers from the same inputs |
| Example photo | placed within half a millimetre of where the web app put it |
| Card fonts (Windows) | the same widths and heights as Chromium measured |
| Line breaks (Windows) | the same lines at the same point size |
| The example card (Windows) | `test/golden-front.png`, zone by zone |

The last is not pixel-for-pixel and cannot be: the golden card was drawn
by Chromium and this one by Windows, which smooth edges and resample
photos differently. Each zone — photo, name, title, number, signature,
QR — has a limit on how far it may differ, tight enough that a line of
text one pixel out of place fails. CI prints the numbers and keeps the
rendered card (`front-native.png`) and a picture of the differences
(`front-difference.png`) in the `render-report` artifact, for a person
to look at.

The window tests drive the real window: an empty form refuses to move
on and says why; a card goes from typing to a saved PDF; a print that
cannot be saved issues nothing; every tool opens; the map can be moved,
kept, discarded and reset.

[`docs/STATE-MACHINES.md`](../docs/STATE-MACHINES.md) lists every part of
the app that has states, and the faults that mapping them found.

---

## The pen tablet

Signing opens `SignWindow`: the pad alone, filling the screen, with the
pointer kept inside that screen until signing is done. A small tablet
maps onto the whole display, so a pad tucked into a corner of the form
uses a corner of the tablet and the pen runs off the edge. Filling the
screen gives the hand the whole tablet.

Pressure reaches the pad one of two ways, and the pad does not care
which:

- **Windows Ink.** When *Windows Ink* is ticked in the XP-Pen settings,
  the pen arrives as a pen, pressure included
  (`SignaturePad.OnStylusDown` and friends). This also covers a finger
  on a touch screen, which has no pressure.
- **WinTab.** When it is not ticked, the pen arrives as a mouse, which
  has no pressure. The driver still knows it, and `PenTablet.cs` asks it
  directly through `wintab32.dll` — the standard interface every tablet
  driver installs, the one drawing programs use.

With pressure, the line is thicker where the pen pressed harder. Without
it — a mouse, a finger, no driver — the line's width follows speed
instead, as it did in the web app. Nothing is required: with no tablet,
signing still works.

**The driver.** `installer/Driver.psd1` names one exact file: XP-Pen's
Deco Fun driver 4.0.21, its address on XP-Pen's server and its SHA-256.
`package.ps1` puts that file in the package; `Install.ps1` runs it only
if the computer has no tablet driver and only if the checksum matches.
It is XP-Pen's own installer (built with Inno Setup, run with Inno
Setup's switches for no questions). Installing a driver needs an
administrator, so Windows asks once; the app itself never does.

To move to a newer driver, change `Driver.psd1` and try it on one
computer first.

---

## The background remover

An ordinary photo is cut out on the computer by MODNet, a portrait-matting
model, run through ONNX Runtime on the processor (no graphics card
needed, about a third of a second). `IdMaker.Vision/Models/README.md`
says what it is, where it comes from and what it is good and bad at.

It replaces the step where HR was told to paste the photo into Gemini or
ChatGPT: an employee's face no longer has to leave the computer to make
their ID. A green-background photo or a cut-out PNG still works exactly
as before, and never touches the model.

If the model file is missing or will not load, the app says so on the
photo step and carries on accepting green-background photos and cut-outs.

---

## Where things are kept

Per user, in `%LOCALAPPDATA%\Alpha Premier\ID Maker\`:

| File | What |
| --- | --- |
| `register.json` | cards issued on this computer |
| `register-outbox.json` | issued cards not yet written to the shared sheet |
| `register-url.json` | a shared-register address that overrides the built-in one |
| `titles.json` | job titles typed under "Other…" |
| `template.json`, `template-*.png` | a moved map, and own artwork |
| `rotate-back.json`, `duplex-flip.json`, `print-layout.json` | the printer settings |
| `last-error.txt` | the last unexpected error, to send to whoever maintains the app |

**No photograph is ever kept**, and none leaves the computer.

Moving from the web app: its list of issued IDs lived in the browser and
cannot be read from here. Save it there (*Issued IDs → Download register
(CSV)*) and bring it in here with *Issued IDs → Bring in a saved list*.
The shared sheet is the real record and needs nothing done.

---

## Changing something

Most changes are numbers in `IdMaker.Core/Config.cs`.

| Want to… | Change |
| --- | --- |
| Move or resize the name, title or number | `NameText`, `TitleText`, `EmployeeNoText` in `Config.cs` |
| Add or remove the job titles everyone sees | `CardConfig.Titles` |
| Change where the photo sits or how big the head is | `CardConfig.Photo` |
| Change the QR link | `CardConfig.Qr` |
| Different number format | `EmployeePrefix`, `EmployeeDigits`, `EmployeeLabel` |
| Paper sizes, margins, crop marks | `Papers`, `SheetMarginMm`, `SheetGapMm`, `CropMark*` |
| New card artwork, built in | replace `assets/front-template.png` and `assets/back.png` at the top of the repository (638 × 1004 or 638 × 1011), re-trace `FrontOutline` and `PhotoArea` if the band or the photo window moved |
| Restyle the window after a rebrand | the colours at the top of `IdMaker.App/Theme.xaml`. None of them touch the printed card |
| Point at a different register | `CardConfig.RegisterUrl`. Treat it as a password; see `register/README.md` |
| The version | `CardConfig.AppVersion`, and `Version` in `IdMaker.App.csproj` and `app.manifest` |

After any change that could move something on the card, run all three
commands above; the Windows tests are the ones that notice.

---

## What is still to do

These need a person, a Windows computer and the tablet. None could be
done from the machine this was written on, which is not Windows.

1. **Sign with the Deco Fun.** Check the line thickens with pressure,
   with *Windows Ink* both ticked and unticked in the XP-Pen settings.
2. **Run the installer on an office computer** and let it install the
   driver. The silent switches are Inno Setup's standard ones and the
   installer was confirmed to be Inno Setup, but the driver's install has
   not been run end to end.
3. **Print one card at 100%** and measure it against an existing card.
4. **Try real staff photos** through the background remover.
5. Then retire the web app: remove `src/`, `app/`, `build.mjs`,
   `serve.mjs`, `test/*.mjs`, `Alpha Premier ID Maker.html` and the web
   half of `DEVELOPING.md`.

Not carried over from the web app, on purpose:

- **Typing coming back after a reload.** A program does not reload.
- **Reading a colour-coded map picture.** Nothing in the web app used it
  any more either; the map is dragged on the card.
