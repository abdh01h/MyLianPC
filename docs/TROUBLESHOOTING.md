# Troubleshooting

Start with **Diagnostics → Export report**. It lists every device's state, errors, the last apply result, conflicting
programs, all sensors and the recent service log. The service log is also in `C:\ProgramData\MyLianPC\logs`.

## The cooler shows "Busy"

Another program owns the cooler's USB interface (it accepts exactly one owner). This is almost always L-Connect 3.
Use **Settings → Vendor software → Take over**, then **Devices → Rescan**. If L-Connect's service is stuck
"stopping", the takeover also closes its leftover process.

## The cooler shows "Unresponsive"

The HydroShift II controller stopped answering. This happens when commands reach it while it is still playing a video
stream (for example right after L-Connect was stopped mid-stream). It cannot be recovered over USB; a USB reset makes it
worse. Recover it like this:

1. Make sure L-Connect is disabled (Settings → Vendor software).
2. Shut Windows down, switch the PSU off (or unplug it) for 15 seconds, then start the PC.
3. The service connects at boot and the Devices page shows the cooler as Connected.

While it is unresponsive the pump and fans keep running at their last speed. Cooling continues, but the curves can't
change it.

## Lighting changes when I switch Windows accounts

* Check **Settings → Vendor software**. GIGABYTE Control Center's sign-in task should be disabled and the Corsair
  service stopped.
* Sign in to the *other* account once, open MyLianPC and click **Turn off Dynamic Lighting for the motherboard**.
  Windows keeps this setting per account.
* The service re-applies the active profile 4 s and 20 s after any sign-in, unlock or account switch. The log shows
  "Re-applying lighting after SessionLogon".

## RAM lighting does not change / "checksum mismatch"

Another program is using the SMBus at the same time (iCUE, OpenRGB, GIGABYTE Control Center with Corsair's service,
SignalRGB). Close or disable it, then click **Devices → Rescan** and apply again. If the Devices page says
*NeedsDriver*, install PawnIO (`winget install namazso.PawnIO`) and restart the service.

A module that answers with an abnormal firmware state (the Devices page shows a warning for it) is put back into normal
mode automatically and its stored lighting is rewritten; the service retries every 30 s and the log shows
"Corsair DRAM: module 0x… is in an abnormal firmware mode". If that warning is still there after a minute, power-cycle
the PC (PSU off for 15 s). A module that cannot be read at all is left alone and read again 30 s later, so a single
disturbed transfer never changes its lighting.

Every static colour write is now read back from the module. When a module acknowledges the colours but keeps the old ones
(the LEDs at the ends then stay on the previous colour, which looks like "red edges"), the service puts that module back
into normal mode straight away, writes the lighting again and logs "had fallen into an abnormal firmware mode; it was
recovered". If the read-back still does not match, the apply is reported as failed and retried every 30 s instead of
pretending the module took the setting.

## The case lighting shows only part of the strip, or a gradient looks wrong

Plain ARGB strips don't report their length. Set the LED count of the header on the Devices page. It matters only for
the per-LED Gradient effect; hardware effects (static, breathing, rainbow…) work without it.

## CPU temperature shows "—"

PawnIO is not installed, or the service isn't running. Both show on the Dashboard banner and in Diagnostics.

## The window says "Not connected to the MyLianPC service"

The hardware keeps its last settings. Start the service: Settings → Service → Restart service (one administrator
prompt), or `sc start MyLianPCService` from an elevated prompt. The service restarts itself after a crash (5 s, 10 s,
30 s).

## Restore defaults

* **Cooling page → Restore default fan behavior**: removes custom curves; motherboard headers go back to BIOS Smart Fan,
  and the cooler uses the Balanced curve.
* **Settings → Reset profiles to the examples**.
* **Settings → Vendor software → Restore**: re-enables L-Connect 3, Corsair Device Control Service and GCC autostart
  exactly as they were.

## Safe recovery procedure (if anything misbehaves)

1. Stop the service: `sc stop MyLianPCService` (elevated). The pump goes to 2500 RPM, the fans to 70%, and motherboard
   headers return to BIOS control.
2. Restore vendor software: `"C:\Program Files\MyLianPC\MyLianPC.Service.exe" --vendor-restore` (elevated).
3. Power-cycle the PC (PSU off for 15 s) so the cooler starts fresh under L-Connect.
4. To remove MyLianPC completely, uninstall it from Apps & features.
