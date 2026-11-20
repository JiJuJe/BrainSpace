# BrainSpace

BrainSpace is a free, offline mind map and whiteboard for Windows 11. It opens with a small editable example workspace. Double-click a Brain to open its board. Open **Projects** to see child Brains, or **Weekly review** to try the board tools.

## Run

On Windows 11 x64, download `BrainSpace-Windows-x64.zip` from [GitHub Releases](https://github.com/JiJuJe/BrainSpace/releases), extract it, and open `BrainSpace.exe` inside the `BrainSpace` folder. The portable ZIP includes its .NET runtime.

The map supports smooth cursor-centered zoom, drag panning and nested boards. Names shrink as you zoom out and stay centered on their nodes. In unusually dense views, hover a marker to reveal its name. New children join their parent’s branch in the next free slot. Use **Place with siblings on map** in a Brain’s context menu to align an existing child with its siblings. Whiteboards support freehand ink, shapes, inline notes, images and links. `Ctrl+K` searches Brains, notes and saved links. `Ctrl+Z` and `Ctrl+Y` undo and redo edits. Find the rest under **… → Keyboard shortcuts**.

At the outermost map zoom, BrainSpace opens the workspace globe. You can also open it with the globe button or `Shift+F`. Scroll or use the zoom buttons to zoom the globe. Drag or use the arrow keys to rotate it at any magnification. A quick drag lets it coast briefly, and grabbing the globe stops it. Right-click a Brain for its actions, or right-click the background to fit or reset the globe. Double-click a visible Brain to open it. Use the map button to return to the editable map with the original layout.

PNG and JPEG use built-in Windows codecs. WebP uses Microsoft's [WebP Image Extensions](https://learn.microsoft.com/windows/win32/wic/native-wic-codecs). Imported image bytes are copied into BrainSpace's own data directory and decoded as needed. For an isolated workspace, launch `BrainSpace.exe --data "C:\path\to\folder"`.

## Build and package

For development with the .NET 10 SDK installed:

```powershell
dotnet build -c Release
dotnet run -c Release
```

To produce the portable distribution, use PowerShell 7 and the .NET 10 SDK. The script restores the Windows runtime packs, publishes the app, and runs the self-test.

```powershell
.\installer\Publish-Portable.ps1
```

The build produces `dist\BrainSpace-Windows-x64.zip`. For a per-user installer, publish first and compile `installer\BrainSpace.iss` in Inno Setup 6.3 or later. Workspace data is outside the installation folder and persists through uninstall.

## Local data

BrainSpace stores changes in `%LOCALAPPDATA%\BrainSpace\brainspace.db` and images in the adjacent `images` folder. SQLite writes are transactional, and BrainSpace waits for pending changes when the app closes. Use **… → Export backup** to create a portable `.brainspace` archive with its referenced images. **Import as copy** preserves existing Brains. **Restore backup** asks before replacing the workspace and creates a recovery archive first.

## Verification

```powershell
$output = Join-Path $PWD 'dist\portable-verification'
$test = Start-Process .\dist\portable\BrainSpace\BrainSpace.exe -ArgumentList '--self-test', "`"$output`"" -Wait -PassThru
Get-Content (Join-Path $output 'results.txt')
$test.ExitCode
```

The self-test uses isolated data and exercises SQLite restart behavior, text and ink, image persistence, archives, hierarchy edits, undo, search, zoom, window state and rendering a map with over 4,000 Brains.
