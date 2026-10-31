# Lucy for Windows

One self-contained `Lucy.exe` that **installs** Lucy and then becomes its **launcher**, the Windows counterpart of `Lucy.py` and the Control Center TUI in [lucy_ws](https://github.com/Lucy-Robotics/lucy_ws). It replaces the old `Lucy-windows-installer` and `Lucy-windows-launcher` projects.

## Usage

Download `Lucy.exe` from the releases page and run it. No admin account is needed. Only the MSVC Build Tools installation (through winget) asks for elevation.

### First run: installer
1. Choose the workspace folder. The default is `C:\Lucy`. `install.py` rejects paths with spaces, and folders under Program Files need admin rights, so the folder must avoid both.
2. Lucy checks for, and installs if missing:
   - **Git**, installed for the current user. Lucy uses `winget Git.Git --scope user`; without winget, or if that fails, it downloads the official portable MinGit from the git-for-windows release (SHA-256 checked) into `%LOCALAPPDATA%\Programs\MinGit` and adds it to the user PATH.
   - **Python 3.9+** (`winget Python.Python.3.12`, user scope; the Microsoft Store alias is ignored)
   - **pixi ≥ 0.78** (official `install.ps1`)
   - **MSVC Build Tools** (VS 2022 BuildTools, C++ workload)
3. It clones `lucy_ws` and runs its `install.py`, streaming the output. `install.py` fetches the repositories, creates the pixi environment (ROS 2 Jazzy), builds with colcon and installs the control panel.
   - With **Review repositories & launcher config** ticked, the install pauses after the clone and before `install.py` runs. An editor opens on `config/repos.json.local` (forks, branches, extra repos) and `config/launcher_config.json.local`. Both are prefilled with the current override or a copy of the tracked file and validated as you type.
   - Content identical to the tracked file writes no override, and an existing `.local` matching the defaults is removed. Skip keeps the files as they are on disk.
4. It copies itself to `%LOCALAPPDATA%\Programs\Lucy\Lucy.exe`, then adds a Start Menu shortcut and an entry in *Apps & features*.

If an install is interrupted, the next start offers to **resume** it. Cancelling stops every process the installer started.

### Afterwards: launcher
This is the same Control Center as the Linux TUI, driven by the workspace's own files:
- `config/launcher_config.json` (or `.json.local`): packages, dependencies, conflicts, readiness stages
- `.lucy_launcher_state.json` / `.lucy_launcher_modifiers.json`: the selection is shared with the TUI
- `.env`: `DEV=` (Developer mode) plus variables passed to every process

To use it:
- Toggle packages, then press **APPLY** (or Ctrl+Enter).
- Each process gets a log tab. Interactive tools (Lucy CLI, Console, rqt) open in their own window.
- **Update** runs `git pull --ff-only` on lucy_ws, then `install.py update`. **Rebuild** runs `pixi run build` and then `pixi run panel-install`.
- In production mode (Developer mode off), Core and the Control Panel start automatically.

### Closing
Closing the window by any route (✕, Alt+F4, Exit, logoff) stops everything Lucy started:
1. Lucy sends Ctrl+C first.
2. If processes are still running after a grace period, it terminates the process tree.
3. It then sweeps for leftover stack processes in the workspace.

Every child runs inside a Windows Job Object with *kill on close*, so even if Lucy.exe crashes or is killed, Windows ends the whole tree.

## Windows differences from Linux
- **Gazebo (`gazebo`, `headless`) is unavailable.** `scripts/pixi_lucy_launch.py` does not support it on Windows.
- Core runs through `pixi run python scripts/pixi_lucy_launch.py`. The robot modifier is passed as `LUCY_ROBOT_PACKAGE`.
- POSIX readiness checks are translated:

  | Check | On Windows |
  |---|---|
  | `port_open.sh N` | TCP connect to N |
  | `pgrep -f/-x` | matched against the package's process tree |
  | `controllers_active.sh`, `/tmp/...` files | considered passed |
- A package may define `command_win` / `readiness_check_win` in the config to override the translation.

## Files
| What | Where |
|---|---|
| Logs | `%LOCALAPPDATA%\Lucy\logs` |
| Settings (workspace path) | `%LOCALAPPDATA%\Lucy\settings.json` |
| Installed app | `%LOCALAPPDATA%\Programs\Lucy\Lucy.exe` |

## Uninstall
You can uninstall from:
- the **Uninstall** button in the launcher header,
- *Settings → Apps → Lucy*,
- or by running `Lucy.exe --uninstall`.

Lucy first stops everything it runs. It then removes the app, its Start Menu shortcut, the *Apps & features* entry and its settings.

**Remove all** also deletes the workspace. That option is only offered when the folder really is a lucy_ws checkout. The folders are deleted once Lucy.exe has exited.

## Development
```
dotnet build
dotnet test Lucy_windows.Tests
# opt-in: drive the real stack of a built workspace
set LUCY_E2E=C:\Lucy && dotnet test Lucy_windows.Tests --filter EndToEnd
# opt-in: render the screens to PNG
set LUCY_RENDER=%TEMP%\lucy-render && dotnet test Lucy_windows.Tests --filter Render
# release build
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

### Releasing
Push a tag such as `v1.2.0`. The workflow `.github/workflows/release.yml` then:
1. runs the tests,
2. publishes the self-contained single-file `Lucy.exe`, with its version set from the tag,
3. creates (or updates) the GitHub release and uploads `Lucy.exe` to it.

It can also be started by hand from the Actions tab. That produces a build artifact without making a release.

Flags: `--install` forces the installer, and `--uninstall` uninstalls.
