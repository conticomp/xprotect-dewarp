# XProtect Fisheye Dewarp

A Milestone XProtect Smart Client plugin that dewarps 360° fisheye cameras **inside the normal camera tile**. Operators click a
**Dewarp** button on the tile, then drag and scroll to look around. There is no special view item to add, and no need to remove the camera
and re-add it through a plugin.

It was built to replace Axis Optimizer's dewarping for Axis M4328-P fisheye cameras, with a simpler workflow for operators.

> [!WARNING]
> **Prototype, provided as is.** This is early-stage software (version 0.x). It has been tested on one Smart Client
> installation with one camera model. It is provided **"AS IS", without warranty of any kind**, express or implied (see [LICENSE](LICENSE)).
> Test it in your own environment before relying on it. Do not use it as the only means of reviewing video for safety-critical or
> evidential purposes. Export evidence with XProtect's own tools.

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
- **No extra video streams.** The plugin uses the frames Smart Client already receives. It does not open its own connections to
  the recording server.

## Requirements

| | |
|---|---|
| XProtect | Smart Client **2025 R2** (tested). Other versions from 2022 R3 onward may work, but are untested. |
| Windows | 64-bit Windows 10 or 11 with .NET Framework 4.8 (included with current Windows). |
| Graphics | A GPU that supports Pixel Shader 3.0 (any GPU from the last decade). Remote Desktop sessions use software rendering and will be slow. Test at the physical PC. |
| Camera | Ceiling-mounted fisheye with a stereographic 182° lens. It was tuned for the **Axis M4328-P** and tested with an **Axis M3058**. |
| To build (optional) | [.NET SDK](https://dotnet.microsoft.com/download) 8 or later (the SDK only; Visual Studio is not needed). Not needed if you install a release. |

## Installation

The plugin is installed on each Smart Client workstation.

### Option A: install a release (recommended)

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
7. Check that the plugin loaded: open a fisheye camera and hover over the tile. A **Dewarp** button (a circle icon) and a **Dewarped snapshot**
   button (a camera icon) should appear on the tile's toolbar. If they are missing, see [Troubleshooting](#troubleshooting).

To **upgrade**, repeat these steps with the new release. To **uninstall**, close Smart Client and run
`powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall` as administrator.

> [!NOTE]
> The DLL is not code-signed. Windows or your antivirus may warn about it. If your organisation requires signed plugins, build it
> yourself (Option B) and sign it with your own certificate.

### Option B: build from source

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later on a build PC. This can be any Windows PC; it does not need XProtect.
2. Clone or download this repository and build:
   ```powershell
   git clone https://github.com/conticomp/xprotect-dewarp.git
   cd xprotect-dewarp
   dotnet build src\FisheyeDewarp\FisheyeDewarp.csproj -c Release
   ```
3. Copy `FisheyeDewarp.dll`, `FisheyeDewarp.pdb` and `plugin.def` from `src\FisheyeDewarp\bin\Release\net48\`, together with
   `packaging\install.ps1`, into one folder. Then follow steps 4–7 of Option A.

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

While dewarping is on, Smart Client's own digital zoom is turned off for that tile, so the two don't fight over the mouse. It comes back
when you turn dewarping off.

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
- **No dewarped export yet.** Use the snapshot button for stills. Export the original fisheye video with XProtect's normal export.
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
| Slow over Remote Desktop | Expected, because RDP uses software rendering. Evaluate at the physical workstation. |

## Uninstall

Close Smart Client and run `install.ps1 -Uninstall` as administrator (see above), or delete
`C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp`. To remove the log as well, delete
`%LOCALAPPDATA%\FisheyeDewarp`.

## Releases and builds

GitHub Actions builds the plugin on every push and pull request; the zip is available under the run's **Artifacts** for testing.
Pushing a version tag publishes a release:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Tags with a suffix (for example `v0.2.0-beta.1`) are published as pre-releases. The plugin's version, shown in Smart Client's plugin list,
comes from the tag.

## How it works

- A background plugin attaches to every camera tile Smart Client creates (`ImageViewerAddOn`). A tile toolbar toggle enables dewarping
  for that tile.
- **Fast view:** a WPF pixel shader (HLSL compiled at runtime with `d3dcompiler_47.dll`) set as the tile's `VideoEffect` dewarps the image
  Smart Client is already drawing. It is instant, but limited to the tile's resolution.
- **Sharp view:** the plugin grabs the full-resolution decoded frame in the background, dewarps it on the CPU with the same lens math, and
  shows it on top of the fast view. While the operator steers, it re-dewarps the frame already in hand (about 15–30 ms per render).
- **Lens model:** stereographic projection, `r = 2f·tan(θ/2)`. The virtual camera is `Rz(pan) · Rx(tilt)` for ceiling mounts.

## License

[MIT](LICENSE). Provided as is, without warranty of any kind.
