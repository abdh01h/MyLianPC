# Third-party notices

MyLianPC is licensed under the **GNU General Public License v2.0 or later** (see `LICENSE`). It includes code ported
from the projects below; their copyright notices and license texts are kept here as their licenses require.

## OpenRGB
https://gitlab.com/CalcProgrammer1/OpenRGB (commit bbc77470, GPL-2.0-or-later, (c) Adam Honse and OpenRGB contributors).
Used for: the GIGABYTE RGB Fusion 2 USB protocol (`src/MyLianPC.Hardware/Gigabyte/Fusion2Controller.cs`), the Corsair DRAM
RGB protocol (`src/MyLianPC.Hardware/Corsair/CorsairDramController.cs`) and the PawnIO SMBus transfer layout
(`src/MyLianPC.Hardware/Smbus/PawnIoSmbus.cs`). License: GNU GPL v2 or later — the full text is in `LICENSE`.

## tinyuz
https://github.com/sisong/tinyuz (MIT, (c) housisong). The compressed-stream format used for the cooler's ring lighting;
the encoder/decoder in `src/MyLianPC.Hardware/LianLi/TinyUz.cs` follows the hydroshift2-rs port below.

## hydroshift2-rs
https://github.com/andyjusa/hydroshift2-rs (commit 5b5c95d6). Used for: HydroShift II LCD packet format (`src/MyLianPC.Hardware/LianLi/HydroShift2Protocol.cs`).

```
MIT License

Copyright (c) 2026 andyjusa

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

```

## lian-li-linux
https://github.com/sgtaziz/lian-li-linux (commit f55f3e54). Used for: HydroShift II telemetry layout, pump/fan write cadence and reply-draining rules (`HydroShift2Protocol.cs`, `HydroShift2Device.cs`).

```
MIT License

Copyright (c) 2026 sgtaziz

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## NuGet dependencies (not modified)
| Package | License |
|---|---|
| LibreHardwareMonitorLib | MPL-2.0 |
| HidSharp | Apache-2.0 |
| SkiaSharp | MIT |
| System.ServiceProcess.ServiceController | MIT |
| Microsoft.Extensions.Hosting / Hosting.WindowsServices | MIT |
| CommunityToolkit.Mvvm | MIT |
| .NET runtime and WPF (bundled in self-contained builds) | MIT |

## Runtime dependencies installed separately (not redistributed)
| Component | License | Needed for |
|---|---|---|
| PawnIO driver (namazso) | freeware installer; driver GPL-2.0, modules LGPL-2.1 | CPU temperatures, Super I/O fan control, SMBus (RAM RGB). Installed from winget/pawnio.eu, not redistributed. The signed SmbusPIIX4 module used at runtime ships inside LibreHardwareMonitorLib. |
