# XProtect Fisheye Dewarp

A Milestone XProtect Smart Client plugin that dewarps 360° fisheye cameras **inside the normal camera tile**. Operators click a
**Dewarp** button on the tile, then drag and scroll to look around. There is no special view item to add, and no need to remove the camera
and re-add it through a plugin.

It was built to replace Axis Optimizer's dewarping for Axis M4328-P fisheye cameras, with a simpler workflow for operators.

> [!WARNING]
> **Prototype, provided as is.** This is early-stage software (version 0.x). It has been tested on one Smart Client
> installation with one camera model. It is provided **"AS IS", without warranty of any kind**, express or implied (see [LICENSE](LICENSE)).
> Test it in your own environment before relying on it. Do not use it as the only means of reviewing video for safety-critical or
> evidential purposes. Export evidence with XProtect's own tools. A dewarped export is a derived view of the recording; keep the
> original fisheye export as the evidence copy.

> [!NOTE]
> This project is not affiliated with, endorsed by, or supported by Milestone Systems or Axis Communications. "Milestone", "XProtect"
> and "Axis" are trademarks of their respective owners. Milestone and Axis support cannot help with issues caused by this plugin.
> Remove the plugin before opening a support case with them.

## Features

- **Dewarp in the tile.** A toggle on every camera tile's toolbar, in both Live and Playback. It works in saved views and when a camera
  is opened from the camera list.
- **Virtual PTZ.** Drag to look around, scroll to zoom in or out, double-click to reset the view.
- **Full-resolution sharpness.** The dewarped view is rendered from the camera's full-resolution frame, not the down-scaled tile image,
  so detail is comparable to Axis Optimizer.
- **Dewarped snapshot to clipboard.** One click copies the current dewarped view to the clipboard at up to the camera's native
  detail, ready to paste into a report or email.
- **Dewarped video export to MP4.** Export the tile's dewarped view for a time range, with an optional burned-in camera name and
  timestamp. It checks the user's export permission and writes an XProtect audit log entry.
- **No extra video streams for viewing.** Live and playback dewarping use the frames Smart Client already receives. Only an export
  opens its own playback connections to the recording server, while it runs.

## Requirements

| | |
|---|---|
| XProtect | Smart Client **2025 R2** (tested). Other versions from 2022 R3 onward may work, but are untested. |
| Windows | 64-bit Windows 10 or 11 with .NET Framework 4.8 (included with current Windows). |
| Graphics | A GPU that supports Pixel Shader 3.0 (any GPU from the last decade). Remote Desktop sessions use software rendering and will be slow. Test at the physical PC. |
| Export | Windows Media Foundation, included with Windows 10 and 11. **Windows N** editions need the free Media Feature Pack. **Windows Server** needs the Media Foundation feature (`Install-WindowsFeature Server-Media-Foundation`, then restart). Uses the GPU's H.264 encoder when present, otherwise Windows' software encoder. |
| Camera | Ceiling-mounted fisheye with a stereographic 182° lens. It was tuned for the **Axis M4328-P** and tested with an **Axis M3058**. |
| To build (optional) | [.NET SDK](https://dotnet.microsoft.com/download) 8 or later (the SDK only; Visual Studio is not needed). Not needed if you install a release. |

## Installation

The plugin is installed on each Smart Client workstation.

### Option A: install with the MSI (recommended)

1. On the workstation, open the [**Releases**](https://github.com/conticomp/xprotect-dewarp/releases) page and download
   `FisheyeDewarp-<version>.msi` from the latest release. *(Optional)* Check it against the `.sha256` file published with it.
2. **Close Smart Client.** If it is still open, the installer asks you to close it.
3. Double-click the MSI, accept the license and click **Install**. Windows asks for administrator approval.
   If SmartScreen shows "Windows protected your PC", click **More info**, then **Run anyway** (the installer is not code-signed yet).
4. Start Smart Client and check for the tile buttons as in step 7 of Option B.

The plugin appears in **Settings › Apps** (Add/Remove Programs) as **Fisheye Dewarp for XProtect Smart Client**. To **upgrade**, run the
newer MSI; it replaces the old version. To **uninstall**, remove it there.

**Silent install for IT** (GPO, Intune, SCCM, PDQ and similar), from an elevated prompt, with Smart Client closed:

```powershell
msiexec /i FisheyeDewarp-<version>.msi /qn /norestart     # install or upgrade
msiexec /x FisheyeDewarp-<version>.msi /qn /norestart     # uninstall
```

If Smart Client is still running during a silent install, Windows may replace the plugin only after the next restart.

### Option B: install with PowerShell

Use this if you can't run MSIs. Both options install to the same folder; use one method per PC so Add/Remove Programs stays accurate.

1. On the workstation, open the [**Releases**](https://github.com/conticomp/xprotect-dewarp/releases) page and download
   `FisheyeDewarp-<version>.zip` from the latest release.
2. *(Optional)* Check the download against the `.sha256` file published with it:
   ```powershell
   Get-FileHash .\FisheyeDewarp-<version>.zip -Algorithm SHA256
   ```
3. Right-click the zip, choose **Properties**, tick **Unblock** if it is shown, and click **OK**. Then choose **Extract All...**.
4. **Close Smart Client.** It locks plugin files while running.
5. Open **PowerShell as administrator** (right-click PowerShell, then **Run as administrator**), go to the extracted folder, and run:
   ```powershell
   cd "$env:USERPROFILE\Downloads\FisheyeDewarp-<version>"
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   The script copies the plugin to `C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp` and unblocks the files.
6. Start Smart Client and log in.
7. Check that the plugin loaded: open a fisheye camera and hover over the tile. A **Dewarp** button (a circle icon), a **Dewarped snapshot**
   button (a camera icon) and an **Export dewarped video** button (a screen with an arrow) should appear on the tile's toolbar. If they are missing, see [Troubleshooting](#troubleshooting).

To **upgrade**, repeat these steps with the new release. To **uninstall**, close Smart Client and run
`powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall` as administrator.

> [!NOTE]
> The DLL is not code-signed. Windows or your antivirus may warn about it. If your organisation requires signed plugins, build it
> yourself (Option C) and sign it with your own certificate.

### Option C: build from source

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later on a build PC. This can be any Windows PC; it does not need XProtect.
2. Clone or download this repository and build:
   ```powershell
   git clone https://github.com/conticomp/xprotect-dewarp.git
   cd xprotect-dewarp
   dotnet build src\FisheyeDewarp\FisheyeDewarp.csproj -c Release
   ```
3. Copy `FisheyeDewarp.dll`, `FisheyeDewarp.pdb` and `plugin.def` from `src\FisheyeDewarp\bin\Release\net48\`, together with
   `packaging\install.ps1`, into one folder. Then follow steps 4–7 of Option B.

### Developer shortcut

On a PC that has both the .NET SDK and Smart Client, `deploy.ps1` builds the plugin and copies it into the plugin folder in one step.
Close Smart Client first. The script needs write access to `C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp`: run it as administrator,
or give your user modify rights on that folder once.

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1              # build and install
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -StartClient # ...and start Smart Client
```

## Using it

| Action | How |
|---|---|
| Turn dewarping on or off | Click **Dewarp** on the camera tile's toolbar |
| Look around | Click and drag in the tile |
| Zoom in or out | Scroll the mouse wheel |
| Reset the view | Double-click the tile |
| Copy the dewarped view | Click **Dewarped snapshot**, then paste (Ctrl+V) into Word, Outlook, Paint, etc. |
| Export the dewarped view as video | Click **Export dewarped video** (see below) |

While dewarping is on, Smart Client's own digital zoom is turned off for that tile, so the two don't fight over the mouse. It comes back
when you turn dewarping off.

### Exporting dewarped video

1. Turn on **Dewarp** on the tile and aim the view at what you want to export.
2. Click **Export dewarped video**. A window opens next to Smart Client. It does not block Smart Client, so you can keep using the
   timeline.
3. Set **Start** and **End**. They default to 30 seconds either side of the frame on screen. To use the time shown in the tile, scrub
   the timeline to it and press **Displayed time** next to Start or End.
4. Choose the width (**1920** or **1280** pixels; the height follows the tile's shape), whether to burn in the camera name and time, and
   the folder. The default folder is `Videos\Dewarp exports`.
5. Press **Export**. The view is taken from the tile at that moment, and the video shows exactly what the tile shows. A progress bar
   appears; **Cancel** stops the export and removes the unfinished file. When it finishes, **Show file** opens the folder.

Files are named `<camera> <start time> dewarped.mp4`, with `(2)`, `(3)`... added rather than overwriting an existing file. The video
keeps the recording's real frame times, so it plays at the camera's recorded frame rate.

If the user does not have XProtect's **Export** permission on the camera, the window does not open, and the attempt is recorded in the
audit log. Each completed export is also recorded (camera, time range, file, user).

## Known limitations

- **Live refresh rate.** In Live, the sharp view refreshes only about once or twice a second. Smart Client takes roughly 0.4–0.9 s to hand
  over each full-resolution frame. Dragging and zooming stay smooth, because the plugin keeps dewarping the frame it already has. Paused
  and stepped playback are unaffected.
- **Ceiling mount only.** Wall-mount geometry exists in the code but has no setting yet.
- **The lens is hard-coded** to a stereographic 182° lens with the image circle filling the frame (Axis M4328-P). Other fisheye cameras may look
  slightly distorted.
- **Black bars at the sides.** The dewarped view is drawn inside the square area where Smart Client shows the fisheye image. In the view's
  Setup you can try turning off the camera tile's "keep aspect ratio" option, so the image fills the whole tile.
- **The view is not saved.** It resets when the view or Smart Client is reopened, and there are no presets yet.
- **One view per export.** An export uses a single fixed view for its whole time range. Following a moving subject means several exports,
  or exporting a wider view.
- **Export speed.** Fetching recorded frames is limited by round trips to the recording server, so the export splits the range into
  up to six parts that run in parallel. On a test PC, 60 s of 8 fps video took about 20 s. Expect roughly real time or faster; a long
  range at a high frame rate takes a while. Each running export opens up to six playback connections to the recording server.
- **Exports are limited to 4 hours** per export.
- **Snapshots are not audited.** They are copied to the clipboard without an XProtect audit log entry or export-permission check. If your
  organisation requires audited exports, account for this before deploying.
- **CPU use** grows with the number of dewarped tiles, since each one is re-rendered on the CPU. Test with your typical number of tiles.

## Troubleshooting

The plugin writes a log to:

```
%LOCALAPPDATA%\FisheyeDewarp\dewarp.log
```

| Problem | What to check |
|---|---|
| No Dewarp button | The files are in `C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp`, they are unblocked, and Smart Client was restarted. If the log file was never created, Smart Client did not load the plugin. |
| The button does nothing | Look in the log for `Shader compile failed` or `No tile found`. |
| The image is soft for a moment | This is expected right after turning Dewarp on or resizing the tile, until the first full-resolution frame arrives. |
| Export fails with "Media Foundation is not installed" | Windows N: install the Media Feature Pack. Windows Server: `Install-WindowsFeature Server-Media-Foundation`, then restart. |
| Export says there is no recorded video | Check the time range in Playback; the camera may not have recorded then. |
| Slow over Remote Desktop | Expected, because RDP uses software rendering. Evaluate at the physical workstation. |

## Uninstall

If you installed with the MSI, remove **Fisheye Dewarp for XProtect Smart Client** in **Settings › Apps**. If you used PowerShell,
close Smart Client and run `install.ps1 -Uninstall` as administrator (see above), or delete
`C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp`. To remove the log as well, delete
`%LOCALAPPDATA%\FisheyeDewarp`.

## Releases and builds

GitHub Actions builds the plugin on every push and pull request, packages it as a zip and as an MSI, and tests the MSI (install,
upgrade, refused downgrade, uninstall). Both are available under the run's **Artifacts** for testing.
Pushing a version tag publishes a release:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Tags with a suffix (for example `v0.2.0-beta.1`) are published as pre-releases. The plugin's version, shown in Smart Client's plugin list,
comes from the tag. The MSI uses the tag without its suffix (`v0.2.0-beta.1` → 0.2.0), because MSI versions must be plain numbers.

## How it works

- A background plugin attaches to every camera tile Smart Client creates (`ImageViewerAddOn`). A tile toolbar toggle enables dewarping
  for that tile.
- **Fast view:** a WPF pixel shader (HLSL compiled at runtime with `d3dcompiler_47.dll`) set as the tile's `VideoEffect` dewarps the image
  Smart Client is already drawing. It is instant, but limited to the tile's resolution.
- **Sharp view:** the plugin grabs the full-resolution decoded frame in the background, dewarps it on the CPU with the same lens math, and
  shows it on top of the fast view. While the operator steers, it re-dewarps the frame already in hand (about 15–30 ms per render).
- **Export:** the range is split into up to six parts, each on its own thread with its own `BitmapVideoSource`, which fetches the recorded
  frames at full resolution. A lookup table built once per export dewarps each frame. The camera name and time are drawn with GDI+,
  and Windows Media Foundation encodes H.264 into a temporary MP4. The parts are then joined into one MP4 without re-encoding, keeping
  the recording's timestamps. All parts use the same encoder, with no B-frames, so they join cleanly: the GPU encoder when available,
  otherwise all parts in software.
- **Lens model:** stereographic projection, `r = 2f·tan(θ/2)`. The virtual camera is `Rz(pan) · Rx(tilt)` for ceiling mounts.

## License

[MIT](LICENSE). Provided as is, without warranty of any kind.
