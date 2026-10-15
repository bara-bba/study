---
tags:
  - study
---

# study

A tray-resident **read-only** monitor for Rockwell ControlLogix / CompactLogix tags, styled like
Picky (PowerToys dark Fluent theme, accent colour, system tray). Pick a PLC, type a tag, choose a
trigger — when it fires, an alarm sound loops until you acknowledge it, and everything is logged
to a daily CSV you can browse in the built-in log viewer.

**study never writes to a PLC.** The reader interface has no write method, and a unit test
(`ReadOnlyGuardTests`) fails if any libplctag write/set call appears anywhere in `src/`.

## Quick start

1. **PLCs…** → *Import from RSLinx* (reads RSLinx Classic's AB_ETH driver IP tables from the
   registry) and/or *Import…* a study JSON tree or a CSV (`name,ip,slot,notes,group`; header
   optional; `,` or `;`). Imports merge by IP and never delete; names, slots and folders you set
   are kept. *Test* pings and lists the controller tags; *Detect slot* tries slots 0–16.
   Click **Save list**.
2. **＋ Add watch** → PLC, tag (or *Browse…* the controller/program tags), data type, trigger,
   polling time. *Test read* reads the tag once without saving.
3. When a trigger fires: the sound loops, the card turns red, a banner appears, the tray icon
   turns red and a Windows notification pops. **Acknowledge** stops it — even if the condition is
   still true.

The seeded list (`config/trn3-plcs.csv`, 75 TRN3 CompactLogix, all slot 0) was built from the
RSLinx node tables + names from `plc-remote-run/trn3-plc-run-list.xlsx`.

## PLC tree (`plcs.json`)

The PLC list is a folder tree, sorted A→Z with natural numbers (PH2 before PH10): at each level
sub-folders first, then PLCs. A folder is each PLC's *Folder* path (`TRN3-ELTEN/Stacker`, `/`
nests), so it exists while it holds PLCs. Select a folder to rename or move it (with all its
sub-folders); ⊞ / ⊟ expand or collapse all; ⧉ opens the config folder.

`plcs.json` can be edited by hand (restart study afterwards). Comments and trailing commas are
allowed, the IP is the identity (a repeated IP keeps the first), and the old flat-array format
still loads:

```json
{
  "version": 2,
  "groups": [
    { "name": "TRN3-ELTEN", "groups": [
        { "name": "Stacker", "groups": [], "plcs": [
            { "name": "Stacker_1", "ip": "10.137.17.21", "slot": 0, "source": "RSLinx", "notes": "" } ] } ],
      "plcs": [] }
  ],
  "plcs": []
}
```

## Tags

| Form | Example |
|---|---|
| Controller tag | `Estop_Active` |
| Program tag | `Program:MainProgram.Fault` |
| UDT member | `Motor1.Fault` |
| Bit of an integer (data type BOOL) | `StatusWord.5`, `Local:1:I.Data.3` |

Data types: BOOL, SINT, INT, DINT, LINT, REAL. Strings, UDTs as a whole and BOOL-array elements
are not supported.

## Triggers

| Trigger | Fires when | After Acknowledge |
|---|---|---|
| Rising edge | 0 → non-zero. First read only sets the baseline. | idle until the next edge |
| Falling edge | non-zero → 0. First read only sets the baseline. | idle until the next edge |
| Is TRUE | value becomes TRUE (also at start) | silent while still TRUE; re-arms when it goes FALSE |
| Is FALSE | value becomes FALSE (also at start) | silent while still FALSE; re-arms when it goes TRUE |
| Expression | the expression becomes true | silent while still true; re-arms when false |

For level / expression triggers, *Stop the alarm automatically when the condition clears* is
optional; otherwise the sound keeps going until Acknowledge.

**Expressions** — `x` = current value, `prev` = previous value (BOOL = 1/0; `prev = x` on the
first read, so edge-style expressions never fire spuriously at start).
Operators `+ - * / %`, `== != < <= > >=`, `&& || !` (or `and or not`), parentheses,
`true`/`false`. Functions `abs(a)`, `min(a,b)`, `max(a,b)`, `bit(a,n)`. Division by zero gives 0.

```
x > 100
x >= 10 && x <= 20
prev < 100 && x >= 100      # crossing upward
abs(x - prev) > 5           # sudden jump
bit(x, 3)                   # bit 3 of a DINT
```

## Connection behaviour

Ping (1.5 s) → CIP connect on path `1,<slot>` → read every *polling time*. On ping failure or a
read error the watch shows **Unreachable / Error**, logs it once, waits 5 s and retries; edges are
re-baselined after a reconnect so a reconnect can't fake a transition. Settings options: *Skip the
ping check* (ICMP blocked but EtherNet/IP open) and *Also sound the alarm when a connected PLC is
lost*.

## Settings

Alarm sound (built-in Beep / Chime / Klaxon / Pulse / Siren MP3s, or your own .mp3/.wav), volume,
auto-stop after N seconds (0 = until acknowledged), default polling time, log folder, accent
colour, Windows notifications, Start with Windows (per-user Run key, no elevation).

## Files

| What | Where |
|---|---|
| Settings, PLC list, watches | `%APPDATA%\study\settings.json`, `plcs.json`, `watches.json` |
| Logs | `Documents\study\logs\study-YYYY-MM-DD.csv` (configurable) |

Log columns: `timestamp,event,watch,plc,ip,tag,value,detail`. Events: STARTED, STOPPED,
CONNECTED, UNREACHABLE, READ_ERROR, TRIGGER, ACK, CLEARED, AUTO_STOP. The log viewer can open any
folder, combine several days (Ctrl+click), filter by text and event, and refreshes live.

## Build / run / test

Requires the .NET 8 SDK (on this machine: `C:\Users\lorettin\.dotnet`).

```powershell
$env:PATH = "C:\Users\lorettin\.dotnet;$env:PATH"
dotnet build
dotnet test                       # 77 tests: expressions, triggers, CSV/RSLinx import, logs, runner, read-only guard
.\src\study\bin\Debug\net8.0-windows10.0.19041.0\study.exe
```

`study.exe --snapshot <folder>` renders every window to PNG with demo data and exits (no
monitoring, no logging, nothing saved) — used for visual checks.

## Layout

```
src/study.core/     no-UI logic, unit-tested
  Expressions/      condition language (tokenizer + parser, no code execution)
  Triggers/         TriggerEngine: edge / level / expression state machine
  Plc/              libplctag reader, tag browser (@tags), slot detector — read-only
  Monitoring/       WatchRunner: ping → connect → poll loop
  Config/, Logging/ JSON store, CSV/RSLinx import, daily CSV logger/reader
src/study/          WPF app (theme, tray, windows) — Theme/Accent/Dwm/Tray copied from Picky
tests/study.tests/  xUnit
```

Library: [libplctag](https://github.com/libplctag/libplctag.NET) 1.5.2 (EtherNet/IP, ControlLogix).
