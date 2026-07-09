<img width="25%" height="25%" align="right" alt="image" src="https://github.com/user-attachments/assets/0f30244d-6fde-4310-9d4f-cb63bac07082" />

  
# ADB-MCP — AI-Controlled Android over ADB

A **.NET Framework 4.8.1 / C#** [Model Context Protocol](https://modelcontextprotocol.io) server that gives
an LLM full agency over an Android device through ADB: it can **see** the screen (structured UI hierarchy +
sampled frames), **act** on it (tap, type, swipe, keys, launch apps, send SMS, place calls), and be **watched
live** via a scrcpy mirror — all exposed as typed MCP tools with a safety layer around the dangerous verbs.
Shipped alongside is a **standalone desktop mirror app** (WinForms) for driving the phone by hand.

Built to the *AI-Controlled Android via ADB-MCP* feasibility brief. The control plane is the mature,
off-the-shelf part; the novel pieces here are the **hybrid hierarchy-first perception**, the **screen-mirror
plane**, and the **authority model** (confirmation gates + shell allowlist) that keeps an agent-owned phone safe.

> **Status:** proven live on a Motorola moto g82 5G (Android 13) over wireless ADB. The MCP server has driven
> the full observe→decide→act loop end-to-end: reading the UI hierarchy, composing and sending Messenger
> messages (element-grounded taps + text injection), and searching Contacts to place a real phone call.

---

## What you get

| Plane | How it works here |
|-------|-------------------|
| **Control** | `adb` actuators wrapped as MCP tools: `tap`, `type_text`, `swipe`, `press_key`, `open_app`, `send_sms`, `shell`. When a stream is active, input routes over the scrcpy **control socket** instead of spawning `adb shell input` — the latency win. |
| **Perception** | **Hierarchy-first** (`get_state`: cheap, structured, exact tap centres) with **sampled frames** (`get_screenshot`) only when ambiguous. Frames come from the **live scrcpy H.264 stream** when running, else screencap. |
| **Mirror / Stream** | `start_stream` opens scrcpy's own **H.264 video + control sockets** (low-latency frames + input on one connection). `start_mirror` runs the scrcpy window for a human observer. |
| **Authority** | Irreversible actions (SMS) are **confirmation-gated**; the raw `shell` tool is **allowlist-guarded** ("loaded weapon" posture). |
| **Desktop app** | `AdbMcp.App` — a WinForms window that mirrors the screen (screencap loop), turns mouse clicks into taps / drags into swipes, injects keystrokes and text, and has one-tap Back/Home/Recents/Power/Wake plus a built-in wireless **Pair** row. |

### Tools (17)

`list_devices` · `get_state` · `get_screenshot` · `get_notifications` · `tap` · `type_text` · `swipe` ·
`press_key` · `open_app` · `send_sms` · `shell` · `start_mirror` · `stop_mirror` · `wait` ·
`start_stream` · `stop_stream` · `stream_status`

Run `adb-mcp-server --list-tools` to print the full JSON schemas.

---

## Architecture

```
MCP client (LLM)
      │  JSON-RPC 2.0 over stdio (newline-delimited)
      ▼
┌─────────────────────────── adb-mcp-server (net481) ───────────────────────────┐
│  McpServer  ──►  ToolRegistry  ──►  14 typed tools                             │
│                                        │                                       │
│     ┌──────────────┬────────────────┬──┴───────────┬────────────────┐         │
│     ▼              ▼                ▼               ▼                ▼         │
│  AdbClient   UiHierarchy      FrameProcessor   ShellPolicy     ScrcpyMirror    │
│  (binary-    (uiautomator     (downscale +     (allowlist)     (human window)  │
│   safe adb)   parse+resolve)   JPEG encode)                                     │
└───────────────────────────────────┬────────────────────────────────────────────┘
                                     ▼
                              adb  ──►  Android device / emulator
```

Source layout:

```
src/AdbMcp.Core/       class library — all logic
  Adb/                 AdbClient, UiHierarchy, DeviceQueries, Perception, InputEncoding, AndroidKeys
  Mcp/                 McpServer (JSON-RPC), ToolRegistry, Tooling (ITool/ToolResult/Schema), DefaultTools
  Tools/               PerceptionTools, ActuatorTools, SystemTools
  Imaging/             FrameProcessor, Nv12Converter, IVideoDecoder (System.Drawing)
  Scrcpy/              ScrcpyProtocol (control-msg + video-header codec), ScrcpyVideoStream (demux),
                       ScrcpyControlClient, ScrcpySession, ScrcpyFrameSource, StreamManager, ScrcpyMirror
  Mf/                  MediaFoundationH264Decoder + COM interop (Windows in-box H.264 decode)
  Safety/              ShellPolicy
  Config/              ServerConfig
src/AdbMcp.Server/     console exe — arg parsing, config, stdio wiring (adb-mcp-server.exe)
src/AdbMcp.App/        WinForms desktop mirror — Adb.cs (adb wrapper), MainForm.cs (live mirror + input + Pair)
tests/AdbMcp.Tests/    offline self-test harness (75 assertions, no device needed)
```

---

## Prerequisites

1. **Windows + .NET Framework 4.8.1** (targeting pack; Visual Studio 2022 or the .NET SDK builds it).
2. **A modern `adb`** from [Android SDK platform-tools](https://developer.android.com/tools/releases/platform-tools).
   > ⚠️ The `adb.exe` shipped in some Windows installs (`C:\Windows\adb.exe`) can be a **2011-vintage v1.0.26**
   > that hangs on `adb devices` and lacks `exec-out`. Install current platform-tools and point `--adb` at it.
3. **A device** with USB debugging enabled (USB, or wireless via `adb tcpip 5555` + `adb connect <ip>`).
4. *(Optional)* **[scrcpy](https://github.com/Genymobile/scrcpy)** for the mirror window (`start_mirror`) and the
   low-latency stream (`start_stream`). The stream needs a **`scrcpy-server` JAR matching `scrcpyServerVersion`**
   (default 2.4) — auto-detected next to `scrcpy.exe`, or set `scrcpyServerJar` in config.
5. *(Windows, automatic)* Media Foundation provides the in-box H.264 decoder for live stream frames — no install.
   Verify it with `adb-mcp-server --test-decoder`.

---

## Build

```powershell
# from the repo root
dotnet build -c Release                       # builds Core + Server + App + Tests
# server: src\AdbMcp.Server\bin\Release\net481\adb-mcp-server.exe
# app:    src\AdbMcp.App\bin\Release\net481\AdbMcp.App.exe
```

Or open `AdbMcp.sln` in Visual Studio 2022 and build.

### Run the tests

```powershell
dotnet run --project tests\AdbMcp.Tests -c Release
# -> Passed: 75   Failed: 0   (no device required)
```

---

## Run

```powershell
# Verify the toolchain and see connected devices (does not start the server):
adb-mcp-server --self-check --adb C:\platform-tools\adb.exe

# Start the MCP server (talks JSON-RPC on stdio; a client launches it for you):
adb-mcp-server --adb C:\platform-tools\adb.exe
```

Wire it into an MCP client (e.g. Claude Desktop / Claude Code) — see
[`mcp-config.example.json`](mcp-config.example.json):

```json
{
  "mcpServers": {
    "adb-android": {
      "command": "C:\\Users\\admin\\Desktop\\ADB-MCP\\src\\AdbMcp.Server\\bin\\Release\\net481\\adb-mcp-server.exe",
      "args": ["--adb", "C:\\Program Files (x86)\\Android\\android-sdk\\platform-tools\\adb.exe"],
      "env": { "ANDROID_ADB_SERVER_PORT": "5860" }
    }
  }
}
```

> The `env.ANDROID_ADB_SERVER_PORT` above is **not decorative on this machine** — see
> [Wireless setup & the adb server port](#wireless-setup--the-adb-server-port). Omitting the target `--device`
> lets the server auto-select the single connected phone, so it survives wireless-ADB port rotation.

### CLI options

| Flag | Meaning |
|------|---------|
| `--config <file>` | Load a JSON config (see [`config/adbmcp.config.sample.json`](config/adbmcp.config.sample.json)). |
| `--adb <path>` | Path to a modern adb executable. |
| `--scrcpy <path>` | Path to scrcpy. |
| `--device <serial>` | Target a specific device (default: auto-select the single connected one). |
| `--max-frame-dim <px>` / `--jpeg-quality <n>` | Sampled-frame size / quality. |
| `--auto-confirm` | **DANGER** — disable confirmation gates (test devices only). |
| `--allow-unlisted-shell` | Let non-allowlisted shell commands run with `confirm=true`. |
| `--self-check` / `--list-tools` / `--help` | Diagnostics; do not start the server. |

Config precedence: **defaults → config file → `ADB_MCP_*` env vars → CLI flags**.
`stdout` is reserved for the JSON-RPC stream; **all logs go to stderr**.

---

## Desktop mirror app (`AdbMcp.App`)

A standalone WinForms window for driving the phone by hand — handy for setup, debugging, and watching what the
agent does.

```powershell
dotnet build -c Release src\AdbMcp.App\AdbMcp.App.csproj
# launch (inherit the dedicated adb port; see below):
$env:ANDROID_ADB_SERVER_PORT = "5860"
.\src\AdbMcp.App\bin\Release\net481\AdbMcp.App.exe
```

- **Live mirror** via a `screencap` loop at a selectable frame rate (the "works today" perception path).
- **Click = tap**, **drag = swipe**, keyboard keys pass through (`Esc`→Back, `Enter`, `Backspace`→Del), plus a
  text box that injects a whole string.
- **Hardware buttons:** Back / Home / Recents / Power / Wake.
- **Pair row:** enter the pairing `host:port` + code and click **Pair** to do wireless pairing without a terminal.

---

## Wireless setup & the adb server port

### Pairing (one-time)

Android 11+ wireless debugging needs a **one-time pairing** before `adb connect` works. The pairing code
**expires in ~60 seconds** and its port rotates each time the dialog reopens, so do it in a single quick step —
either the app's **Pair** row, or one command with the pairing dialog open:

```powershell
& "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe" -P 5860 pair 192.168.0.212:<PAIR_PORT> <CODE>
```

Pairing persists across reboots. Afterwards you only ever `connect` (no code):

```powershell
.\connect-phone.ps1 <CONNECT_PORT>     # reads the port off the main Wireless debugging screen
```

### ⚠️ The adb server port (port-5037 conflict)

adb's default server port is **5037**. On this machine that port is held by the **traccar** GPS-tracking service
(a Java Windows service), which is *not* adb — so every adb client that connects to 5037 **hangs forever**. The
fix is non-destructive: run adb on its **own dedicated server port** and leave traccar alone.

- Everything here uses **port 5860**: the MCP config sets `ANDROID_ADB_SERVER_PORT=5860`, the desktop app bakes
  in `-P 5860`, and [`connect-phone.ps1`](connect-phone.ps1) targets it.
- If you move to a machine without this conflict, drop the env var / `-P` and the default 5037 works fine.

```powershell
# start a clean adb server on the dedicated port and confirm it responds instantly:
$env:ANDROID_ADB_SERVER_PORT = "5860"
& "C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe" -P 5860 devices
```

---

## The MVP proof: "send a message"

The end-to-end target from the brief — *open Messages, find a contact, compose, confirm, send* — maps to:

1. `get_state` → read the screen, find the compose field and controls by text/id.
2. `open_app` / `tap` → navigate to the conversation.
3. `tap` (the field) → `type_text` → compose.
4. `send_sms` **without** `confirm` → returns a preview, no action.
5. `send_sms` **with** `confirm:true` → composes via intent and presses send.

`send_sms` also does the compose-then-tap-send dance directly, which is the reliable path the brief
recommends over the OEM-fragile `service call isms`.

---

## Safety & authority model

An agent with `shell` + `input` + `send_sms` effectively **owns the phone**. This server bakes in the brief's
posture:

- **Confirmation gates.** `send_sms` refuses to act without `confirm:true` — it previews instead. Extend the
  same pattern to any new irreversible verb.
- **Shell allowlist.** `shell` runs a command only if it matches a configured regex allowlist (read-only
  inspection + standard input actuators by default). Everything else is refused unless the operator explicitly
  enables `allowUnlistedShellWithConfirm`. **Never expose raw shell to an untrusted prompt.**
- **Prompt injection is a live threat.** On-screen content (a notification, a web page) can try to steer the
  agent. Sandbox to a **test device** for any untrusted workload.
- **Bind locally, own the device.** USB-debugging is an attack surface; only point this at devices you control,
  and disclose automated authorship where messages reach third parties.

---

## Perception strategy (why it stays cheap)

- **`get_state` first.** The uiautomator hierarchy is structured, exact, and cheap — it gives every element's
  text, id, content-desc and a tap centre. Target by **text / resource-id**, not raw x,y, so actions survive
  resolution and layout changes.
- **`get_screenshot` only on ambiguity.** Frames are downscaled to a max edge and JPEG-encoded to control
  tokens. Sample them for canvas/game/photo content the hierarchy can't describe.

---

## Screen-mirror plane & low-latency streaming (the novel build)

This is the piece the brief called out as the real engineering — and it's built:

- **`start_stream`** pushes the scrcpy-server JAR, `adb forward`s a local port to the device's abstract socket,
  launches the server via `app_process`, and connects **two sockets**:
  - a **video socket** carrying H.264, demuxed here (device-meta handshake, codec meta, per-frame
    PTS/flags/length headers) and decoded to RGB via the Windows **Media Foundation** H.264 MFT
    ([`MediaFoundationH264Decoder`](src/AdbMcp.Core/Mf/MediaFoundationH264Decoder.cs)) plus the tested
    [`Nv12Converter`](src/AdbMcp.Core/Imaging/Nv12Converter.cs);
  - a **control socket** for input — while streaming, `tap`/`type_text`/`swipe`/`press_key` become a few dozen
    bytes down the held-open connection instead of an `adb shell input` spawn per action.
- **`get_screenshot`** samples the live decoded frame when streaming (`source: auto`), else screencap.
- Everything **degrades gracefully**: no stream, input via `adb shell` and frames via screencap; decoder
  unavailable, frames via screencap while the control channel still carries input.

**Verification status.** The wire-format codecs (control messages, video demux) and the NV12 color math are
unit-tested (75 assertions). The Media Foundation interop is validated on Windows via `--test-decoder`
(MFT creation and input/output type negotiation succeed). Full end-to-end streaming — server handshake through
decoded frames — is verified against a physical device, since it needs a live H.264 source and a matching
`scrcpy-server` JAR.

```powershell
adb-mcp-server --test-decoder          # probe the H.264 decoder interop (no device needed)
```

---

## Known limitations

- Requires a modern `adb`; the ancient bundled Windows build (`C:\Windows\adb.exe`, 2011 v1.0.26) will hang.
- **Port 5037 may be occupied** by another service (here: traccar) — run adb on a dedicated port via
  `ANDROID_ADB_SERVER_PORT` / `-P` or every adb call hangs. See [Wireless setup](#wireless-setup--the-adb-server-port).
- Wireless pairing codes **expire in ~60 s** and the pairing port rotates — pair in one quick step (app Pair row
  or a single command), then only `connect`.
- The scrcpy protocol is **version-specific** — the `scrcpy-server` JAR must match `scrcpyServerVersion`.
- `send_sms` presses the send control by locating it in the hierarchy — robust on common messaging apps, but
  heavily-customised OEM apps may need a `get_state` + explicit `tap`.
- End-to-end streaming is device-verification-pending (interop and codecs are tested; a live stream needs hardware).
- Wireless ADB can drop under sleep/roaming — reconnect with `adb connect <ip>` if a device disappears.

## License

Provided as-is for the Tester Present engagement. Scope to devices you own and control.
