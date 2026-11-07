# Lightspeed

A Godot 4 (C#) simulation where physics runs in the present but what you *see* is delayed by the finite speed of
light. Fly around a small world and watch things arrive late, bend, and show up more than once, while you turn the
speed of light up and down from a few cm/s to the real thing.

- Goals, controls, architecture and status: [plan.md](plan.md)
- Building from source for Windows and Linux: [BUILDING.md](BUILDING.md)

## Play a release

Builds are attached to the [latest GitHub release](https://github.com/LucasVinicius314/lightspeed/releases/latest).
Paste one of these into a terminal, in an empty folder: it downloads the release, unpacks it and starts the game.
It uses only what is already installed (PowerShell on Windows; `curl` and `tar` on Linux).

**Windows (PowerShell)**

```powershell
$ProgressPreference='SilentlyContinue'; Invoke-WebRequest https://github.com/LucasVinicius314/lightspeed/releases/latest/download/Lightspeed-windows.zip -OutFile Lightspeed-windows.zip; Expand-Archive Lightspeed-windows.zip Lightspeed -Force; .\Lightspeed\Lightspeed.exe
```

**Linux (bash)**

```bash
curl -fL https://github.com/LucasVinicius314/lightspeed/releases/latest/download/Lightspeed-linux.tar.gz -o Lightspeed-linux.tar.gz && mkdir -p Lightspeed && tar -xzf Lightspeed-linux.tar.gz -C Lightspeed && chmod +x Lightspeed/Lightspeed.x86_64 && ./Lightspeed/Lightspeed.x86_64
```

## Update

Inside the game folder, double-click **Update.bat** (Windows) or run `bash update.sh` (Linux). It looks up the
latest release, and if it is newer than the `version.txt` in the folder it downloads it and replaces the game files.
Close the game first. Your settings and saves live outside the folder and are kept. Releases from `v0.0.2` on include
the updater; for older ones, run the download command above again.

Press **Tab** in the game for the settings menu, scroll the mouse wheel to change the speed of light, and **Esc** to
free the mouse.

Notes:

- `releases/latest/download/<file>` always points at the newest release's file with that name, so the commands never
  change. For a specific version use `releases/download/<tag>/<file>` (for example `.../releases/download/v0.1.0/...`).
  Drafts are skipped by `latest`.
- The repository (and so the release) must be public for these to work without logging in.
- The exports are self-contained (no Godot or .NET needed). Linux needs a desktop with a Vulkan-capable GPU.
