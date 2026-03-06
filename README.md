# Wider Codex

Wider Codex is a small Windows launcher for the Codex desktop app. Its job is simple: start Codex and apply a wider chat layout so the conversation panel uses more horizontal space.

## What it does

When you launch `WiderCodex.exe`, it:

1. Locates the installed Codex desktop app.
2. Starts `Codex.exe` with a temporary remote debugging port.
3. Connects to the renderer through Chrome DevTools Protocol.
4. Injects a CSS override that increases the chat content width.

The injected style currently sets:

- `--thread-content-max-width: 96rem`
- `--thread-composer-max-width: calc(var(--thread-content-max-width) + 1rem)`

This makes the main chat area noticeably wider than the default Codex layout.

## Main functionality

The primary purpose of this app is to launch the Codex app with a wider chat panel.

It does not replace Codex, patch Codex on disk, or bundle a custom frontend. The width change is applied at runtime after Codex starts.

## Requirements

- Windows
- Codex desktop app installed
- .NET 10 SDK to build from source

## Running the app

Build and run from the repository root:

```powershell
dotnet build .\WiderCodex\WiderCodex.csproj
dotnet run --project .\WiderCodex\WiderCodex.csproj
```

You can also run the built executable directly.

## Command line usage

```text
WiderCodex.exe [--force-restart] [-- <additional Codex args>]
```

Options:

- `--force-restart` kills running Codex processes before launching.
- `--help` prints usage information.

Anything after `--` is forwarded to Codex.

## Installation discovery

Wider Codex tries to find Codex automatically by:

- checking `CODEX_GUI_PATH`
- checking the installed `OpenAI.Codex` AppX package
- inspecting running Codex processes

Supported environment overrides:

- `CODEX_GUI_PATH`: explicit path to `Codex.exe`
- `CODEX_HELPER_PATH`: explicit path to `resources\codex.exe`

## Notes and limitations

- The wider layout is only applied when Codex is launched through Wider Codex.
- If Codex is already running, injection can fail because the launcher may not reach the page target it started for. In that case, close Codex or use `--force-restart`.
- The app relies on Codex exposing a DevTools target and on the current DOM/CSS variables remaining compatible.
- The launcher also sets `BUILD_FLAVOR=env` for the spawned Codex process.

## Project structure

- `WiderCodex/Program.cs`: launcher logic, process startup, DevTools connection, and CSS injection
- `WiderCodex/WiderCodex.csproj`: project configuration
- `.github/workflows/build.yml`: Windows CI build

## License

MIT. See [LICENSE](LICENSE).
