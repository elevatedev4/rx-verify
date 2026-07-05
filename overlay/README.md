# Rx Verify — Windows overlay (P0b)

An always-on-top Windows desktop app that reads prescription fields out
of **PioneerRx** via UI Automation (UIA), compares them against the
existing [rx-verify matching engine](../README.md) (128 tests), and
shows a pharmacist a green ✓ / yellow ? / red ✗ verdict per field, in a
**fixed review order** (Patient → Prescriber → Rx) that never changes
based on severity.

**This has not been run against a live PioneerRx window.** It was built
on macOS, where UI Automation, WPF, and PioneerRx itself don't exist to
test against. Every UIA selector/label/geometry value in `Uia/FieldMap.cs`
and `Uia/PioneerRxWindow.cs` was inferred from 22 Accessibility Insights
screenshots (see `../../manager-app/docs/spec-rx-verify-phase0-2026-07-04.md`,
section "W-Q3 RESOLVED"), not from a running instrumented session. Treat
this as a well-documented first draft to validate and adjust against
your real workstation — the "Dump UIA Tree" debug mode (below) is built
specifically for that.

## Why .NET/WPF + FlaUI (not Python)

| | .NET WPF + FlaUI | Python (uiautomation/pywinauto + Tkinter/PyQt) |
|---|---|---|
| UIA robustness | FlaUI wraps the native UIA3 COM API directly; strongly typed; actively maintained | uiautomation/pywinauto wrap the same COM API through Python interop — workable, but an extra translation layer, less mature tooling for raw/full-tree walks |
| Always-on-top overlay | WPF `Topmost` + native window management, first-class | Tkinter/PyQt overlays work but fight harder with Windows' always-on-top/DPI/multi-monitor quirks |
| Long-running desktop reliability | Compiled, strongly typed — a whole category of runtime surprises (typos, wrong-type field access) get caught at build time | Dynamically typed; more surface for a shift-long session to hit an unexpected `AttributeError` |
| Speed to prototype | Slower to stand up than a Python script | Faster |

For a tool that will sit open, always-on-top, for an entire pharmacist's
shift, reliability wins over prototyping speed. **Recommendation: .NET
8 + WPF + FlaUI**, which is what this directory contains.

## Why the engine is a subprocess, not a port

The matching engine (`../src/`) is TypeScript with 128 passing tests
encoding real pharmacy nuance — nickname crosswalks, sig abbreviation
expansion, NDC/RxNorm comparison, quantity/days-supply reconciliation,
date re-windowing. Porting all of that to C# would mean re-deriving and
re-testing the same rules in a second language for **zero behavior
change** — pure risk, no reward, for a v0.

Instead: `../src/cli.ts` is a small new CLI entrypoint added to the
rx-verify repo. It reads `{ "source": ScriptData, "entered": EnteredData
}` as JSON on stdin, calls the real `verify()`, and writes the
`VerifyResult` JSON to stdout. The overlay (`Engine/EngineClient.cs`)
spawns `node dist/cli.js` as a local subprocess and talks to it over
stdin/stdout pipes — no engine code was duplicated or reimplemented.
The engine's own 128 tests (plus 3 new ones for the CLI wrapper, 131
total) still pass unchanged; see `../README.md` and run `npm test` in
the `rx-verify` root to confirm.

## Prerequisites (on the Windows workstation)

1. **.NET 8 SDK** (or newer) — https://dotnet.microsoft.com/download
2. **Node.js 20+** — https://nodejs.org (needed to run the engine
   subprocess; this is the only thing the overlay needs beyond .NET)
3. **Visual Studio 2022** (Community edition is fine) or the `dotnet`
   CLI — either works for building
4. This repo checked out somewhere on the workstation (e.g.
   `C:\Users\will\claude\rx-verify`)

## Build steps

```powershell
# 1. Build the engine (from the rx-verify repo root, not this overlay folder)
cd rx-verify
npm install
npm run build          # emits dist/cli.js — this is what the overlay calls
npm test                # optional, confirms all 131 tests still pass

# 2. Build the overlay
cd overlay\RxVerifyOverlay
dotnet restore
dotnet build
```

Or open `overlay/RxVerifyOverlay/RxVerifyOverlay.sln` in Visual Studio
and build from there (F6 / Ctrl+Shift+B).

## Run steps

```powershell
cd overlay\RxVerifyOverlay
dotnet run
```

On first launch:

1. The window opens always-on-top in the top-left corner (drag it
   anywhere; drag the resize grip in the bottom-right corner to resize).
2. Expand **"Engine settings"** at the bottom and set:
   - **rx-verify dist/cli.js path** — Browse to
     `...\rx-verify\dist\cli.js` (the file built in step 1 above)
   - **Node executable** — leave as `node` if it's on your PATH (check
     with `node --version` in a terminal); otherwise give the full path
     to `node.exe`
   - Click **Save settings**
3. Open a **Pre-Check Rx**, **Edit Rx**, or **New Rx** screen in
   PioneerRx.
4. Click **Refresh** in the overlay (or check **"Auto (5s)"** to poll
   automatically). The verdict rows should populate in the fixed order:
   Patient → Patient DOB → Patient Address → Prescriber → Date Written
   → Drug → Sig → Quantity → Days Supply → Refills.

If the status line says "Waiting for a PioneerRx window..." — make sure
the window title actually starts with "Pre-Check Rx", "Edit Rx", or
"New Rx" (see `Uia/FieldMap.TargetWindowTitlePrefixes`); adjust that
list if your PioneerRx version/locale uses different screen names.

## If fields read wrong (the expected first-run experience)

This is the part most likely to need adjustment, because it was built
without a live PioneerRx window to validate against. Use the **"Dump
UIA Tree..."** button:

1. With a Pre-Check/Edit/New Rx window open, click **Dump UIA Tree...**
2. Save the resulting `.txt` file (you'll be prompted for a location —
   **this file can contain real patient data**, since it's a literal
   readout of every element on screen; handle/delete it per your usual
   workstation policy, same as any other document with PHI on it)
3. Open the dump — it lists every UIA element in the window: control
   type, Name, AutomationId, whether it's keyboard-focusable, and its
   on-screen bounding rectangle, indented by tree depth
4. Compare it against the labels/positions in `Uia/FieldMap.cs` (each
   constant has a comment explaining what screenshot it came from and
   why) and `Uia/PioneerRxWindow.cs` (the fractional panel-bounds
   estimates used to disambiguate repeated labels like "Address:" and
   "Phone:", which appear once for the patient and once for the
   prescriber)
5. Adjust the string constants / fractional bounds to match what you
   see in the dump. Nothing else in the app needs to change — all the
   reading logic in `Uia/FieldReader.cs` and `Uia/UiaTreeWalker.cs` is
   driven entirely by `FieldMap.cs`'s constants and
   `PioneerRxWindow.cs`'s bounds.

## What's implemented

- **Field reading** (`Uia/FieldReader.cs`): builds both engine inputs —
  `ReadEntered()` from the LEFT data-entry panel (what the tech typed:
  Patient, Written By/prescriber, Written date, Item/drug, Quantity,
  Refills, Directions/sig, plus read-only DOB/address/phone/license
  text), and `ReadSource()` from the CENTER e-script panel (parsed
  Prescriber/Patient/Medication/Quantity/Directions boxes) — with no
  OCR, per the phase-0 finding that both panels are UIA-readable for
  electronic scripts.
- **Full-tree walk** (`Uia/UiaTreeWalker.cs`): walks the whole control
  tree, not just focusable/tab-order elements, so the Not-Focusable
  read-only text nodes (DOB, addresses, phone, license numbers) are
  reachable — their value is the element's `Name`.
- **Engine integration** (`Engine/EngineClient.cs` + `../src/cli.ts`):
  subprocess call to the real, tested engine; JSON in, JSON out.
- **Verdict overlay** (`MainWindow.xaml` / `.xaml.cs`,
  `ViewModels/OverlayViewModel.cs`): always-on-top, movable, resizable
  window; green/yellow/red pill counts at the top; rows in the FIXED
  field order (`Models/EngineModels.cs` → `FieldOrder.Fields`, mirroring
  the engine's own `FIELD_ORDER` — **never re-sorted by severity**, a
  hard requirement from the pharmacist owner); each row shows the
  source value, entered value, and the engine's plain-English
  explanation, not just a color.
- **Debug tree-dump mode**: see above.
- **Defensive reads**: every UIA call is wrapped so a missing/blank
  field, a stale element, or a mid-redraw exception becomes `null` /
  "not provided" (which the engine already renders as yellow, never a
  false mismatch) instead of crashing the overlay.
- **Structured-vs-scanned detection**: `FieldReader.IsStructuredSourceAvailable()`
  detects when the center e-script panel is actually a blank raster
  image (faxed/scanned script) and surfaces a manual-review message
  instead of ten spurious "not provided" yellows.

## Local-only, by construction

- The overlay reads the local screen via UIA (an in-process Windows
  API, no network involved) and spawns a **local** `node` subprocess
  talking over stdin/stdout pipes.
- Search the codebase for anything network-shaped: no `HttpClient`, no
  `WebClient`, no `Socket`, no `fetch`, no URL constants anywhere in
  `Engine/`, `Uia/`, `ViewModels/`, or the WPF code-behind. `cli.ts` in
  the engine repo has zero runtime dependencies and does no I/O beyond
  stdin/stdout (see its file header).
- The only thing ever written to disk is (a) `%AppData%\RxVerifyOverlay\settings.json`
  (two file paths, no patient data), and (b) the UIA tree dump, and only
  when the pharmacist explicitly clicks "Dump UIA Tree..." and picks a
  save location — never automatically, never silently.
- Nothing here requires or uses Claude Code, or any other AI service,
  on the workstation itself — this is a standalone compiled Windows app.

## Deferred (not in this v0)

- **OCR for faxed/scanned scripts.** The center e-script panel is a
  raster image for those (not UIA-readable); v0 detects this and
  surfaces "manual review required" rather than guessing. Per the
  phase-0 spec this is a small % of Will's volume — revisit if it
  becomes worth building against Windows.Graphics.Capture + the Windows
  OCR API.
- **Real RxNorm data.** The engine's drug comparator still uses
  `FixtureProvider` (~20 synthetic concepts) — swapping in real RxNorm
  data needs a free NLM UTS account (an owner task, documented in
  `../README.md` "Swapping in real RxNorm data"). No overlay code
  changes when that happens — only `../src/cli.ts`'s provider
  construction.
- **Installer / code signing.** v0 runs via `dotnet run` or a
  `dotnet build` output folder. Packaging as a signed, installable
  `.exe` (and bundling Node so end users don't need it on PATH — e.g.
  via `pkg`/`nexe` for the CLI, or a self-contained .NET publish for the
  overlay itself) is a P2+ productization step per the phase-0 spec.
- **Window-position persistence.** The overlay always opens at a fixed
  top-left position; it doesn't remember where you last dragged it.
  Small addition (a couple settings fields) whenever it's worth the
  code.
- **Address component parsing.** `FieldReader.ParseAddress()`
  deliberately keeps the whole address string as one field rather than
  splitting street/city/state/zip — the screenshots show inconsistent
  spacing/formatting (e.g. no space before the zip in one sample) that
  would need several more live examples to parse reliably rather than
  guess.
- **Prescriber license/NPI position.** `FieldMap.cs` documents an
  unresolved ambiguity: which of the two numbers under "Licenses:" in
  the center panel is the NPI vs. a state license, and where exactly the
  (unlabeled, in the screenshots) license number sits in the LEFT
  panel. `FieldReader.FindPrescriberLicenseNumber()` currently returns
  `null` rather than guess — needs one live tree-dump to resolve.
- **Audit logging of overrides.** The phase-0 spec flags this as a
  likely compliance need (does a pharmacist's override of a red/yellow
  verdict need to be recorded?) — a product conversation before this
  goes into real pilot use, not an engineering default.

## File map

```
overlay/
├── README.md                          — this file
└── RxVerifyOverlay/
    ├── RxVerifyOverlay.sln
    ├── RxVerifyOverlay.csproj          — net8.0-windows, WPF, FlaUI.Core + FlaUI.UIA3
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml / .xaml.cs      — the overlay UI + its code-behind (settings, tree-dump save dialog, auto-refresh timer)
    ├── Models/
    │   ├── EngineModels.cs             — C# mirror of ../../src/types.ts (ScriptData/EnteredData/FieldVerdict/VerifyResult/FIELD_ORDER)
    │   └── OverlaySettings.cs          — persisted engine-path settings (%AppData%\RxVerifyOverlay\settings.json)
    ├── Engine/
    │   └── EngineClient.cs             — subprocess call to `node dist/cli.js`
    ├── Uia/
    │   ├── FieldMap.cs                 — ALL the UIA labels/selectors inferred from screenshots (start here to fix a misread field)
    │   ├── PioneerRxWindow.cs          — window attach + panel-bounds geometry
    │   ├── UiaTreeWalker.cs            — full-tree walk, label→value pairing, debug tree dump
    │   └── FieldReader.cs              — combines the above into PrescriptionRecord for both panels
    └── ViewModels/
        └── OverlayViewModel.cs         — orchestrates read → verify → bind; owns the fixed-order verdict list
```
