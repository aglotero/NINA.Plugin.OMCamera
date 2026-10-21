# NINA.Plugin.OMCamera — OM System / Olympus Native Camera Driver for N.I.N.A.

A native 64-bit camera driver plugin for **[N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://nighttime-imaging.eu/)** that enables direct USB tethered astrophotography with **OM System** and **Olympus OM-D** mirrorless cameras.

---

## Features

- **Native USB Tethered Capture (`OlyCameraLib.dll`):** Connects directly to OM System / Olympus bodies in PC Tether mode without virtual webcam workarounds or external folder watchers.
- **Lossless 12-Bit `.ORF` RAW Decoding & 16-Bit ADU Scaling:**
  - Automatically patches new OM System (`OM Digital Solutions` / `OM-1MarkII`) TIFF headers in memory so N.I.N.A.'s bundled FreeImage / LibRaw pipeline losslessly decompresses the Bayer `.ORF` stream.
  - Scales 12-bit linear sensor ADU values (`0..4095`) into the standard 16-bit range (`<< 4`, `0..65520`) for proper histogram stretching and calibration in N.I.N.A.
- **Timed & Bulb Exposures:**
  - Automatically programs the camera's discrete shutter speed (`1/8000s` to `60s`) when in Timed (`M`/`S`) mode.
  - Supports arbitrary long exposures when the camera is set to **Bulb** mode.
- **Bidirectional Camera Synchronization:**
  - **ISO (`Gain`):** Dynamically queries the camera's exact supported ISO values (`80` to `102400` in 1/3-stop increments on OM-1 Mark II) and syncs changes in both directions between N.I.N.A. and the physical camera dial.
  - **Shutter Speed (`Exposure Time`):** Converts requested exposure durations to the closest supported camera fraction (`numerator / denominator`).
  - **Lens Aperture (`Readout Mode` & Imaging Screen Dropdown):** Queries the attached Micro Four Thirds lens's supported f-stops (`f/1.2` – `f/22`), exposes an **Aperture** selector directly inside N.I.N.A.'s **Imaging** panel (below **Gain**) as well as the Camera **Readout Mode** dropdown, and stays synchronized with the camera dial.
- **LiveView Streaming:** Supports real-time LiveView framing and focusing directly inside N.I.N.A.

---

## Supported Cameras

Any OM System or Olympus camera that supports USB PC Tethering via **OM Workspace**, including:
- **OM System:** OM-1, OM-1 Mark II, OM-5
- **Olympus OM-D:** E-M1X, E-M1 Mark III, E-M1 Mark II, E-M1, E-M5 Mark III, E-M5 Mark II

---

## Prerequisites

1. **N.I.N.A. 3.0 or newer** (64-bit Windows).
2. **OM Workspace (64-bit)** installed on your PC (downloadable from [OM Digital Solutions](https://explore.omsystem.com/)).
   - This plugin dynamically loads `OlyCameraLib.dll` from your local OM Workspace installation (`C:\Program Files (x86)\OMDS\OM Workspace\OlyCameraLib.dll` or via the `OM_WORKSPACE_PATH` environment variable). No proprietary SDK binaries are distributed in this repository.
   - Make sure **OM Workspace** and **OM Capture** are **closed** before connecting in N.I.N.A., as Windows only allows one application to hold an exclusive PTP tethering session with the camera at a time.

---

## Camera Setup

1. Set the camera's physical mode dial to **M (Manual)** (or **B / Bulb** for exposures longer than 60 seconds).
2. Connect the camera to the PC via USB and power it on.
3. On the camera screen, select **PC (Tether)** when prompted for the USB mode.

---

## Installation

### From Release / Manual Build
1. Build the project in `Release` configuration (or download `NINA.Plugin.OMCamera.dll`).
2. Copy `NINA.Plugin.OMCamera.dll` to:
   ```text
   %LOCALAPPDATA%\NINA\Plugins\3.0.0\OM System Camera\NINA.Plugin.OMCamera.dll
   ```
3. Start **N.I.N.A.**, go to **Equipment -> Camera**, select **OM System / Olympus Camera (Native REA)** from the dropdown, and click **Connect**.

---

## Building from Source

### Requirements
- .NET 8.0 SDK (`net8.0-windows`)

### Build Commands
```powershell
dotnet build NINA.Plugin.OMCamera.sln -c Release
```
- If N.I.N.A. is installed at `C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy`, the project builds offline against your local N.I.N.A. assemblies.
- Otherwise, MSBuild automatically falls back to the `NINA.Plugin` NuGet package.

---

## License

Released under the [MIT License](LICENSE).
