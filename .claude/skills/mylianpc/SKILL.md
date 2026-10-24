---
name: mylianpc
description: Use when working in the MyLianPC repository — building or testing, cutting a release, adding a lighting effect, LCD theme, device adapter or fan curve, changing the IPC contract, or updating the docs. Covers the non-obvious rules this project needs (SDK path, hardware safety, versioning, feature touchpoints).
---

# MyLianPC development

MyLianPC is one WPF app plus one Windows service that owns all RGB, cooler LCD, fan curves and monitoring for a
specific PC build (GIGABYTE B850 + Lian Li HydroShift II + Corsair Vengeance RGB DDR5). The README explains the
product; this file explains how to change it without breaking the hardware story.

## Non-negotiables

- **There is no simulation layer.** Every control maps to a real device write. Do not run the service, the installed
  build, or probe write commands (`mb-color`, `ram-color`, `ring-color`, `lcd-demo`, `cooler-*`) to "test" unless the
  user asks. Use unit tests instead. Probe read-only commands (`hid`, `fusion2`, `lcd`, `sensors`, `ram-detect`,
  `ram-dump`, `mem-info`, `drives`) are safe.
- **The service is the only process that touches hardware.** The app talks to it over a named pipe
  (`MyLianPC.Core.Ipc`). Never add hardware access to `MyLianPC.App`, and never let two code paths command the same
  device.
- **Advertise, then offer.** An adapter reports the exact zones, effects, fans and capabilities it supports
  (`ZoneInfo`, `ZoneCapabilities`). Never add a UI control for something the adapter does not advertise — the UI is
  driven entirely by those records.
- **Validate on the service side.** Pipe requests are re-validated by the server; write operations require an
  elevated administrator. Do not move that trust into the client.
- **Keep the safety invariants** (`Core/Cooling/FanCurve.cs`, `Service/HardwareManager.cs`): curves reach 100% by
  90 °C, never drop below their minimum, the pump never goes below 1600 RPM, a missing sensor means 100%, the
  setpoint is repeated every 5 s (firmware falls back after ~10 s), and stopping the service restores safe defaults
  and BIOS fan control.
- **SMBus:** never write SPD addresses 0x50–0x57 (refused in code); all traffic goes through the shared mutex
  (`SmbusGate`).
- **Vendor software** (L-Connect 3, GCC, Corsair Device Control) must not run concurrently. Preserve the takeover and
  restore path (`VendorManager`, `install.ps1` / `uninstall.ps1`).
- **Do not hand-edit `artifacts/`** — the publish script generates it and git ignores it. Cleaning out old builds is
  expected after every publish (see *Release / version bump* below); never treat anything in there as source.
  `bin/` and `obj/` are also gitignored.

## Toolchain gotcha

On this machine `dotnet` on PATH resolves to `C:\Program Files\dotnet\dotnet.exe`, which has **no SDK**. The real SDK
is at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (10.0.402). Use it explicitly:

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet test tests\MyLianPC.Core.Tests -c Release
& $dotnet test tests\MyLianPC.Core.Tests --filter "FullyQualifiedName~EffectFrameTests"
```

Baseline at 0.11.2-beta: **203 tests, all passing.** `build\publish.ps1` already implements this fallback.

## Where things live

| Change | Start here |
|---|---|
| Effect enum, zone models, validation | `src/MyLianPC.Core/Lighting/LightingModels.cs` |
| Software-generated effects (ring, previews, per-LED) | `src/MyLianPC.Core/Lighting/EffectFrames.cs` |
| Motherboard ARGB (hardware effects + per-LED streaming) | `src/MyLianPC.Hardware/Adapters/Fusion2Adapter.cs` |
| Cooler ring, fans, LCD | `src/MyLianPC.Hardware/Adapters/HydroShiftAdapter.cs` |
| Corsair RAM | `src/MyLianPC.Hardware/Adapters/CorsairDramAdapter.cs` |
| Device registration, apply order, fallbacks | `src/MyLianPC.Service/HardwareManager.cs` |
| Fan curves and presets | `src/MyLianPC.Core/Cooling/{FanCurve,CoolingPresets}.cs` |
| LCD themes (480×480 SkiaSharp) | `src/MyLianPC.Lcd/LcdRenderer.cs` |
| Profiles, defaults, persistence | `src/MyLianPC.Core/Profiles/`, `Core/Settings/` |
| IPC contract / client / server | `Core/Ipc/Protocol.cs`, `ServiceClient.cs`, `Service/PipeServer.cs` |
| UI | `src/MyLianPC.App/Views/*.xaml(.cs)`, `ViewModels/`, `Controls/` |
| Hardware probe (dev CLI) | `tools/MyLianPC.Probe/Program.cs` |

## Adding a lighting effect

The full touchpoint set, as done for RainbowWave (commit d0df5dc):

1. `LightingEffect` enum in `Core/Lighting/LightingModels.cs`.
2. `ZoneLighting.Validate` — if the effect ignores colours, add it to the "no colours required" set.
3. `EffectFrames.Generate` — implement the frames (≤ `MaxFrames` = 48, seamless loop). Devices without a hardware
   effect get it automatically: the HydroShift ring, the UI preview (`Controls/LedPreview.cs`), Fusion2 per-LED mode,
   Corsair gradient.
4. `App/Views/LightingView.xaml.cs` — update `usesColors` (colour picker enabled?), `multi` (add-another-colour?),
   and speed enablement if the effect is not time-based.
5. Adapters — add it to each advertised array and encode it:
   - **Fusion2:** `HeaderEffects` / `SingleEffects` and the `Apply` switch. Addressable headers can stream frames with
     `SetDirect`; single-LED zones need a hardware approximation (see the existing Rainbow/Wave fallbacks).
   - **HydroShift:** `RingEffects` only; frames come from `EffectFrames`.
   - **Corsair:** `Effects` and the `Write` switch, mapped to a `CorsairDramMode`. Note the existing deliberate
     swap: app `Rainbow` maps to `CorsairDramMode.RainbowWave` and app `RainbowWave` maps to `CorsairDramMode.Rainbow`.
     Do not "fix" that without hardware evidence.
6. `HardwareManager.Fit` — add a fallback chain so zones lacking the effect get a sensible substitute and the UI note
   stays truthful.
7. Test — add an `[InlineData]` to `FeatureTests.EffectFrameTests.Frame_counts_fit_the_ring_upload_limit`.
8. Docs — README lighting row; `docs/COMPATIBILITY.md` if the per-device story changes.

## Adding or changing a device adapter

1. Implement the adapter under `src/MyLianPC.Hardware/Adapters` (transport code belongs in `Gigabyte/`, `LianLi/`,
   `Corsair/`, `Usb/`, `Smbus/`, `Sensors/`).
2. Expose `Zones`, `Status` (`DeviceStatus`) and `Apply`; keep `ZoneInfo` capabilities truthful.
3. Wire it into `HardwareManager`: construct it in the ctor, add it to `Zones`, add its `Apply` branch in
   `ApplyLighting`, handle reconnect in `WatchTick`, and add any LCD/fan pieces.
4. There is no runtime plugin loader — registration is compiled in. The Devices, Lighting and Cooling pages pick the
   device up from `DeviceStatus` / `ZoneInfo`.

## Adding an LCD theme

1. Implement a render method and a `case "<theme-id>"` in `LcdRenderer.Render`. Theme ids are kebab-case strings
   (`minimal-temps`, `hud-toxic`, …); the default is `minimal-temps`.
2. Render headless at 480×480 and tolerate missing metrics (the service runs it in session 0).
3. Verify with the probe's `lcd-preview <theme-id> <file.png>` (never `lcd-demo`, which drives the panel):
   `& $dotnet run --project tools\MyLianPC.Probe -- lcd-preview minimal-temps out.png`.

## IPC and persistence changes

- Add request/response records in `Core/Ipc/Protocol.cs`, the client call in `ServiceClient.cs`, and the handler in
  `Service/PipeServer.cs`. Keep authorization and validation server-side.
- Persistence is JSON: shared profiles in `%ProgramData%\MyLianPC\profiles.json`, per-user UI prefs in
  `%LOCALAPPDATA%\MyLianPC`. Use additive, nullable/defaulted properties only; add a migration in
  `Core/Settings/LegacyData.cs` if an old shape must still load. Existing profiles must never fail to load.
- Defaults live in `Core/Profiles/DefaultProfiles.cs`.

## Release / version bump

The version is duplicated on purpose. Bump all of it in one commit:

1. `src/MyLianPC.App/MyLianPC.App.csproj` → `<Version>` (the publish script reads this one).
2. `src/MyLianPC.Service/MyLianPC.Service.csproj` → `<Version>`.
3. `installer/MyLianPC.iss` → `#define AppVersion` fallback.
4. `README.md` → badge, installer filename in Install step 1, and the version named under "Build from source".
5. Publish: `powershell -ExecutionPolicy Bypass -File build\publish.ps1` (runs tests unless `-SkipTests`; produces
   `artifacts\MyLianPC`, the zip, and the setup `.exe` when Inno Setup 6 is installed).
6. Push `main`, tag `v<version>` (for example `v0.11.2-beta`), and create the GitHub prerelease
   `MyLianPC <version> Beta` with the setup `.exe`, the zip and `SHA256SUMS.txt` attached.
7. Clean `artifacts` (below) so only the version just published remains.

### Cleaning `artifacts`

The user wants **only the latest version** kept. After every publish, before uploading the release:

- delete `MyLianPC-Setup-<older>.exe` and `MyLianPC-<older>-win-x64.zip` for every version but the current one;
- delete stale `*.log` files and one-off `*.png` screenshots left from debugging installs;
- keep `artifacts\MyLianPC` (the current publish folder) and rewrite `SHA256SUMS.txt` from the current setup `.exe`
  and zip (`sha256sum --text <files>`, two spaces before the name);
- the current setup `.exe`, zip and `SHA256SUMS.txt` are exactly the three release assets.

Each old zip is roughly 100 MB and each old setup roughly 74 MB; before this rule the folder held nine releases and
1.5 GB. Never delete the publish folder or the current version's assets.

Commit style from history: an imperative subject naming the user-visible change, a body explaining **why** (hardware
behaviour, failure mode, trade-off), and the version bump mentioned in the commit.

## Verification bar

- `dotnet test` passes (currently 203).
- UI changes: render both appearances with
  `MyLianPC.exe --page <page> --screenshot <file.png> --theme dark|light`. Page names are exactly `Dashboard`,
  `Devices`, `Drivers`, `Lighting`, `Cooler LCD`, `Cooling`, `Profiles`, `Diagnostics`, `Settings`, `About`.
  Screenshots render at 100%
  zoom and at least 1480×1000. The app can render without the service (pages show disconnected state).
- Hardware behaviour cannot be verified without the machine — say so explicitly instead of claiming it works.
- Update the README feature table and any affected docs: `docs/COMPATIBILITY.md`, `docs/TROUBLESHOOTING.md`, and
  `THIRD-PARTY-NOTICES.md` for a new dependency or ported code (the project is GPL-2.0-or-later).
