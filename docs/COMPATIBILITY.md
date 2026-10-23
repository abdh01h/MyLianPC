# Phase 1 — Hardware compatibility assessment

Assessed 2026-10-22 on `DESKTOP-SVB8KBC`, Windows 11 Pro 10.0.26200. Everything marked **Verified** was observed on this
machine. Everything marked **Researched** comes from vendor documentation or community source code and has **not** yet
been exercised on this hardware.

## 1. Verified inventory

| Component | What the machine reports | Interface |
|---|---|---|
| Motherboard | GIGABYTE B850 AORUS ELITE WIFI7, BIOS F12d (2026-09-23) | — |
| Motherboard RGB controller | USB `048D:5711`, firmware string `IT5711-GIGABYTE V1.0.29.6`, chip id `0x5711` | HID. `MI_01&COL02`: vendor page `FF89:00CC`, 64-byte feature reports. `MI_00`: **HID LampArray `0059:0001`** (Windows Dynamic Lighting) |
| Super I/O (fans, board temps) | ITE **IT8696E**, 2 fan headers reporting RPM (≈2033 and ≈1790 RPM) | LPC port I/O → needs kernel driver |
| CPU cooler | Lian Li **HydroShift II LCD-C** (`HydroShiftIILCDCircle`), USB `1CBE:A021`, serial `3A2847F9F4C6C503`, firmware `lianlih2_0003_0020`, integrated wireless receiver fw 22 | **WinUSB** (not HID), interface GUID `{88BAE032-5A81-49F0-BC3D-A4FF138216D6}` |
| RAM | 2 × Corsair `CMH32GX5M2E6000Z36` (Vengeance RGB DDR5), on SMBus at Corsair DCS slots `[0:1]` and `[0:3]` | SMBus (AMD `1022:790B`) → needs kernel driver |
| Case | Lian Li Vector V100R (per spec; no USB device — see §2) | Lighting on a motherboard 5 V ARGB header; fans on motherboard PWM headers |
| CPU / GPU | Ryzen 7 9700X; ASUS PRIME RTX 5060 Ti 16 GB; Radeon iGPU | — |

Vendor software currently installed and running:

| Software | Process / service | Account / session | Role |
|---|---|---|---|
| L-Connect 3 v2.1.28 | `LConnectService`, `LConnectServiceWatcher` (auto start) | LocalSystem, session 0 | Owns the cooler (LCD, pump, fans, ring). Uses Lian Li's closed `lcd207.dll` SDK v1.4.0.125 and streams H.264 to the panel. Local HTTP endpoint on `localhost:11021` (undocumented) |
| GIGABYTE Control Center 26.03.26.01 | `GCC.exe`, `GBT_DL_LIB.exe` | **Per user**, session 10 | Motherboard ARGB and, via Corsair DCS, RAM RGB |
| Corsair Device Control Service 1.9.9 | `CorsairDeviceControlService` (manual, started on demand) + kernel driver `CorsairLLAccess…` | LocalSystem | SMBus access to the DIMMs on behalf of GCC (named-pipe clients) |
| Also present | `ENE_DRAM_RGB_AIO`, `gdrv3`, `HWiNFO_215` driver, Patriot Viper M2 RGB, MSI Afterburner | — | Other SMBus/port-I/O users → potential bus contention |

## 2. Compatibility matrix

Legend: ✅ verified on this PC · 🔬 researched, implementation ready or planned, **not yet run on this PC** · ⚠️ possible with caveats · ❌ not possible / not offered.

| # | Question | Motherboard ARGB (IT5711) | Case V100R lighting | Corsair Vengeance RGB DDR5 | HydroShift II LCD-C | Case/board fans (IT8696E) |
|---|---|---|---|---|---|---|
| 1 | Detected programmatically | ✅ HID enumeration + identification read (standard user, no driver) | ⚠️ Only as LEDs on an ARGB header; LED count must be configured (V100R strip is 26 LEDs + fan LEDs, total unknown) | ✅ via SPD/WMI; 🔬 RGB controller detection over SMBus | ✅ USB enumeration, model + firmware from L-Connect log | ✅ chip identified (HWiNFO); 🔬 via LibreHardwareMonitor once driver installed |
| 2 | RGB control | 🔬 Fusion2 protocol (OpenRGB lists this exact board, layout `it5711_9_device`: ARGB_V2_1/2/3 + accent zones) **or** 🔬 Windows Dynamic Lighting (LampArray) | 🔬 through the motherboard header | 🔬 SMBus protocol (OpenRGB `CorsairDRAMController`) | 🔬 24-LED pump ring via `0xFC` command | — |
| 3 | Brightness / zones | 🔬 per header, per-LED direct mode | 🔬 per LED | ✅ every module is found on its own SMBus address (2 installed, 4 supported); 10 or 12 LEDs per DIMM depending on the module, the driver writes 12 (surplus entries are ignored), per-LED + effects | 🔬 per LED (24) | — |
| 4 | LCD independent of vendor SW | — | — | — | 🔬 **Yes**: JPEG/PNG push over WinUSB (MIT-licensed, verified by its authors on this USB id). Code + tests done; **blocked only on stopping L-Connect** | — |
| 5 | Fan monitor / control | — | ⚠️ V100R fans are 4-pin PWM on board headers | — | ✅ coolant °C, pump RPM, 3 fan RPM read; ✅ pump + 3 fan duty write **verified on this PC**: pump 20→100 % moves 1835→2480 RPM, fans 20→100 % moves 420→2150 RPM. ⚠️ The firmware hands control back to its own curve after ~10 s, so the setpoint is re-sent every 5 s | ✅ RPM visible (HWiNFO); 🔬 PWM write supported by LibreHardwareMonitor for IT8696E |
| 6 | Sensor readings | — | — | ✅ DIMM temps exist (HWiNFO) | 🔬 coolant temperature | CPU temp/power/clock: ❌ without driver (verified 0 as standard user); 🔬 with PawnIO. GPU temp/clock/fan: ✅ verified now (NVAPI, no driver) |
| 7 | Vendor SW must stay installed/running | No (after replacement) | No | No for SMBus route; **CDCS + GCC must not run concurrently** | **L-Connect service must be stopped** (single-owner WinUSB) | No |
| 8 | Works from a Windows service | ✅ HID works from any session (Fusion2 route). LampArray route ❌ (WinRT API is tied to a user session) | ✅ | 🔬 yes (PawnIO, LocalSystem) | 🔬 yes (WinUSB from LocalSystem is how L-Connect already does it) | 🔬 yes |
| 9 | Survives Windows user switching | 🔬 yes if one service owns it **and** GCC + Dynamic Lighting stop overriding it; effect can also be saved to controller flash (`0x5E`) | same as board | 🔬 yes while the service stays connected; DIMMs fall back to their built-in effect when nobody drives them (see §3) | ✅ by design: L-Connect already runs it from session 0 | 🔬 yes (service) |
| 10 | Restrictions | GPL licence of the reference code (§5); GCC's flash writes | LED count unknown | SMBus contention risk; DDR5 SPD-corruption reports in OpenRGB #4934 → only RGB controller addresses, never SPD `0x50–0x57` | Firmware hangs if control writes land while an H.264 buffer is full (we push still JPEGs only); ≤ 3.6 s pump/fan write cadence | Board has no named LHM profile → generic "Fan #N" labels |

## 3. Why lighting changes when you switch Windows accounts (evidence)

1. **RAM — confirmed from logs.** GCC is the only client of Corsair Device Control Service, and GCC runs per user. Each time
   the GCC instance disconnects (logoff, account switch, or closing GCC) CDCS logs
   `Enabling DRAM HW lightings due to session end` and stops, so the DIMMs revert to their built-in effect. Software-set RAM
   lighting only lasts while a client stays connected. 95 CDCS logs in `C:\ProgramData\Corsair\Logs` show this cycle.
2. **Motherboard / case — strong suspect, not yet confirmed.** The ARGB controller is also a Windows **Dynamic Lighting**
   (LampArray) device. Dynamic Lighting is a *per-user* setting. In account `abdullah` it is disabled for this device
   (`AmbientLightingEnabled=0`), but a second account keeps its own value; the Windows default is enabled, which makes Windows
   paint its own effect on login. The other account's hive is loaded but needs admin to read. In addition, the second
   account's own GCC instance applies its own stored profile when it starts.
3. **Cooler — not affected**, because L-Connect already runs as a LocalSystem service.

**Fix:** a single LocalSystem service owns all devices and keeps driving them regardless of who is logged in. GCC's
per-user autostart and Dynamic Lighting (in *both* accounts) are switched off so nothing else repaints the LEDs.

## 4. Limitations found

* **L-Connect and this app cannot both run.** WinUSB allows one opener. Replacing L-Connect means disabling its two services
  (not uninstalling — they can be re-enabled at any time). Built-in L-Connect themes, its H.264 videos and the
  wireless-fan pairing UI are then unavailable; our LCD themes are rendered by us.
* **Kernel driver needed for CPU temperatures, board fan control and RAM RGB.** Microsoft blocks the old WinRing0 driver.
  The current solution is the signed **PawnIO** driver (used by LibreHardwareMonitor 0.9.5+, FanControl and OpenRGB 1.0).
  It needs a one-time admin install.
* **SMBus is shared.** Corsair DCS, GCC, the ENE DRAM driver, HWiNFO and Patriot's SSD tool can all touch it. Our service
  takes the standard `Global\Access_SMBUS.HTP.Method` mutex, but programs that ignore it must not run at the same time.
* **GIF/video on the LCD** works by pushing successive JPEG frames (no H.264 streaming in v1, to avoid the firmware hang).
  Expect roughly 10–20 fps for animations, which is fine for GIFs.
* **The HydroShift's radiator fans** may be the wireless TL/CL type. If so, wired fan-duty writes are ignored by the
  firmware and the fans stay under the pump head's own control; RPM monitoring still works. Needs testing.
* **No BIOS settings are touched.** Note: the DIMMs currently run at 4800 MT/s (`ConfiguredClockSpeed`), not their rated
  6000 — EXPO appears to be off in BIOS. Not something this app changes.

## 5. Implementation routes and licensing

| Device | Recommended route | Alternative |
|---|---|---|
| HydroShift II LCD / pump / ring | Own C# WinUSB driver, ported from **MIT** projects (done, 14 unit tests) | Keep L-Connect (no unified control) |
| Motherboard + case ARGB | Own C# HID driver for the Fusion2 `0xCC` protocol | Windows Dynamic Lighting API (official, but only works inside a user session, so it can't solve the account-switch problem) |
| Corsair RAM | Own SMBus driver over PawnIO | iCUE SDK (requires full iCUE running — another vendor app) |
| Fans / sensors | LibreHardwareMonitorLib (MPL-2.0) over PawnIO | — |

The only mature reference code for the Fusion2 and Corsair DRAM protocols is **OpenRGB (GPL-2.0-or-later)**. Porting it
makes this application a derivative work, so the application would be licensed **GPL-2.0-or-later**. That is harmless for
a personal project (the MIT, MPL and Apache code it uses are all compatible), but it is your decision. Option B is to require
OpenRGB 1.0 to run alongside as a separate server and drive it over its SDK port — no licence impact, but one more
background program.

## 6. Architecture

```
┌──────────────── session 0 (LocalSystem) ─────────────────┐      ┌── each user session ──┐
│ MyLianPC.Service (Windows service, single instance)        │      │ MyLianPC.App (WPF)      │
│  ├ DeviceManager → adapters (one owner per device)        │ pipe │  dashboard, RGB, LCD   │
│  │   HydroShift2 (WinUSB) · Fusion2 (HID) · CorsairDram   │◄────►│  editor, fan curves,   │
│  │   (SMBus/PawnIO) · SuperIO fans (LHM/PawnIO)           │ ACL: │  profiles, settings,   │
│  ├ SensorHub (LHM, 1 s default, cached snapshot)          │ Auth │  diagnostics           │
│  ├ CoolingController (validated curves, failsafe 100%)   │ Users│  theme: Dark/Light/Sys │
│  ├ LcdCompositor (SkiaSharp → JPEG, 1 fps metrics)        │      │  prefs: %LOCALAPPDATA% │
│  ├ ProfileStore  %ProgramData%\MyLianPC\profiles.json       │      └────────────────────────┘
│  └ Diagnostics/log  %ProgramData%\MyLianPC\logs             │
└───────────────────────────────────────────────────────────┘
```

* The service is the only process that touches hardware. It starts at boot, so lighting/LCD/cooling are applied before
  anyone logs in and are unaffected by logoff or switching accounts.
* Named pipe `\\.\pipe\MyLianPC` with an ACL granting *Authenticated Users* read (status, sensors) and *Administrators* plus
  an opt-in local group write (apply profile, fan curves). Requests are validated server-side.
* On service stop or crash: fans/pump are handed back to hardware/BIOS control (Super I/O restored to automatic; pump
  head keeps its last safe duty), lighting stays as last saved in controller memory.
* Shared hardware profiles (`%ProgramData%`) are separate from per-user UI preferences (`%LOCALAPPDATA%`). Theme changes
  only touch the latter — covered by a unit test.

Module layout (in this repo):

| Project | Spec modules |
|---|---|
| `MyLianPC.Core` | profiles, validation, fan-curve logic, configuration persistence, preferences |
| `MyLianPC.Hardware` | discovery, device communication, adapters (HydroShift2 done; Fusion2, CorsairDram, SuperIO next) |
| `MyLianPC.Lcd` | LCD themes/rendering (16 themes) |
| `MyLianPC.Service` *(next)* | Windows service, IPC, session handling, cooling loop, diagnostics/logging |
| `MyLianPC.App` *(next)* | WPF UI, themes |
| `tools/MyLianPC.Probe` | hardware probe / proof-of-concept CLI |

## 7. Implementation status

| Phase | Status |
|---|---|
| 1 Compatibility assessment | Done (this document) |
| 2 Hardware proof of concept | Done: RAM, motherboard/case and pump-ring colors and LCD frames shown on the real hardware (see §8) |
| 3 Core application (discovery, RGB, profiles, monitoring, LCD, persistence) | Done |
| 4 Cooling management (curves, presets, failsafe, motherboard opt-in) | Done; pump/fan writes not yet exercised on hardware |
| 5 Windows service and multi-user (shared profiles, per-account active profile, pipe security, session re-assert, recovery) | Done; a real account switch has not been exercised with the installed service |
| 6 Interface and appearance (all pages, Dark/Light/System) | Done |
| 7 Packaging (setup .exe, zip + scripts, docs) | Done |

## 8. Verified vs. not yet verified (2026-10-22)

**Verified on this PC**
* Device identities (USB ids, firmware strings, Super I/O chip, DIMM part numbers, RAM RGB controllers at SMBus 0x19 and
  0x1B, Corsair VID 0x1B1C PID 0x0901, firmware 1.1.9, protocol 4).
* **RAM lighting:** a static color stored on both modules (module acknowledged the checksum; turned red as confirmed by
  photo), and later through the service as part of a profile. One module (0x19) had been left in a stalled firmware
  mode by a vendor tool and silently ignored all writes while reporting protocol 1; after the driver switched it back
  to normal mode (register `0x23 = 1`) it reported protocol 4 and showed the colour immediately (photo).
* **Motherboard and case lighting:** a static color on all three ARGB headers, chipset accent and LED_C (turned red, photo),
  then a profile applied through the service and saved to the controller's flash.
* **Pump ring:** static red uploaded (visible on the cooler, photo). The cooler's reply was not read, see below.
* **LCD:** live JPEG frames from this app shown on the panel (photo). L-Connect's leftover overlay was still on top;
  the driver now clears both layers at startup.
* PawnIO driver installed; CPU temperature/power/clock, IT8696E fan RPM and DIMM temperatures read through it.
* Service ↔ app IPC from a standard (non-elevated) account; profile activation with a per-device result report; honest
  partial-failure reporting while the cooler was unavailable.
* 100 unit tests (curves, colors, effects, profiles, persistence, LCD rendering/validation, cooler packet encoding,
  TinyUZ, CRCs, IPC serialization).
* App UI rendered in Dark and Light themes.

**Found on real hardware and fixed in the code**
* The cooler answers every command; unread replies fill its buffer until it stops accepting writes. The first driver
  used short timed reads and lost replies. It now keeps a read permanently pending on a dedicated thread.
* Stopping L-Connect while it streams H.264 leaves the controller wedged until a power cycle. The takeover therefore
  disables L-Connect permanently instead of stopping it per session. L-Connect's service process can also hang
  in "stopping"; the takeover closes it.
* A Vengeance RGB DDR5 module can end up in a stalled firmware mode (seen on this PC after iCUE was stopped mid-write):
  it still answers on SMBus, acknowledges every checksum, but ignores every effect and colour — its LEDs stay dark or
  frozen on the last frame — and its device info reports protocol version 1 instead of 4. Writing 1 to the mode
  register (`0x23`) puts it back into normal mode. The RAM driver now does this while detecting modules, every two
  minutes, and before an apply, and rewrites the module's stored lighting when a module recovers.
* The RGB Fusion 2 controller's HID handle can go stale: it keeps answering info queries but silently ignores every
  effect write, so the case fans and strips stop following colour changes while the cooler and RAM keep working.
  A reset on the old handle is not enough — only re-opening the HID device restores it (verified on this board). Every
  profile apply now re-opens the controller, and the headers' effect-engine register (`0x32`) is written once per
  apply rather than once per zone, because the per-zone write made the strip step visibly while a colour changed.
* The two modules of this kit do not have the same number of LEDs: one bar is fully covered by a 10-LED buffer, the
  other keeps its two end LEDs on their previous colour until a 12-LED buffer is written (no readback tells them
  apart, and both report the same PID and device info). The driver now always writes 12 entries; a 10-LED module
  ignores the surplus ones.

**Not yet verified (needs the installed build after one power cycle)**
* Reading cooler telemetry (coolant temperature, pump/fan RPM) and the LCD layer-clearing init with the new reader.
* Pump and radiator-fan speed writes (0xFB). The curves are validated and rate-limited, but no write has been observed
  on the hardware yet.
* Animated ring effects (multi-frame uploads, ≤48 frames).
* GIF playback on the panel; LCD brightness range (0-100 assumed); rotation.
* Motherboard fan-header control through IT8696E (opt-in, off by default).
* Lighting staying put across a real Windows account switch with the installed service; persistence after a full
  power-off (Fusion2 flash save, RAM module storage).
* The setup program on a clean system.

## 9. Known limitations

* Profiles are stored once for the whole PC (`%ProgramData%\MyLianPC\profiles.json`). Every Windows account sees the
  same list and can edit it; what is remembered per account is only *which* profile that account activated, and the
  service switches to it when the account signs in.
* The radiator fans' LEDs are driven by the pump head over a channel the community protocol does not cover in
  general. The app uploads them together with the pump ring (Settings → Service → "Light the radiator fans with the
  pump ring"); with that option off their LEDs are switched off.
* The IT5711's built-in "wave" effect is a whole-device sequence that overrides per-zone settings and it has no
  per-LED rainbow at all, so on the ARGB headers Rainbow, Rainbow Wave and Wave are drawn per LED in direct mode (the service
  streams the frames, like the pump ring); the single onboard LEDs use the controller's colour-cycle engine.
* L-Connect 3 and MyLianPC cannot run at the same time.
* RAM effect changes are written to module memory (flash). The app avoids rewriting identical settings.
* Per-LED effects on motherboard headers (gradient, rainbow, rainbow wave, wave) use direct mode, which is not stored in flash; the
  service re-applies them at every start.
