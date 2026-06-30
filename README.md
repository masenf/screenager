# screenager

A lightweight Windows utility that limits **active** daily screen time and **locks** the
screen (it never logs out — open programs keep running) when the limit is reached. It pauses
while the screen is locked or the PC is asleep, shows a small always-on-top countdown, warns
before locking, and can email you a daily report of sites/videos/app usage.

Built for a single family PC: C# / .NET 10, published as a **self-contained single-file
`win-x64` executable** — no installer, no preinstalled .NET runtime needed.

---

## How it works

- **Active-time accounting.** Every second it credits real elapsed time to the day's counter
  *only* while the session is unlocked, the machine is awake, and there has been keyboard/mouse
  input within `idle_threshold_seconds`. Locking, sleeping, or going idle pauses the countdown.
  Per-tick credit is capped, so missed sleep/lock events or clock changes can't over-count.
- **The logical day** rolls over at `reset_hour` (e.g. 4am) so late-night use counts against the
  right day. State is stored in SQLite at `%LOCALAPPDATA%\screenager\screenager.db`.
- **Enforcement.** When the budget is exhausted (or during the bedtime window) it shows a large
  topmost warning, then calls `LockWorkStation()`. If the monitored user unlocks again while
  their time is spent, it briefly shows "screen time is up" and re-locks.
- **Parent override.** Press the hotkey (default `Ctrl+Alt+Shift+S`), enter your PIN, and grant
  extra minutes for today (or revoke previously-granted time). While any granted time is still
  active, the **bedtime cutoff is suspended** too. The dialog shows how much extra has been
  granted today, and locking is paused for a configurable grace period (`grace_seconds`,
  default 30s) while the dialog is open so you're never rushed.
- **Movable countdown.** Drag the countdown window anywhere; its position is remembered.
- **Daily report.** At `send_hour` it emails (via the Mailgun HTTP API) a summary: time used,
  most-used windows, videos watched, and sites visited (read from Chrome/Edge/Firefox history).

---

## Getting the executable

You don't need a build toolchain — GitHub Actions builds it for you:

1. Push to the branch (CI runs on every branch) or trigger the **build** workflow manually.
2. Open the workflow run → download the **`screenager-win-x64`** artifact.
3. It contains `screenager.exe` and a sample `screenager.cfg`. Put both in a folder on the PC,
   e.g. `C:\Program Files\Screenager\`.

To build locally instead (needs the .NET 10 SDK):

```powershell
dotnet publish src/Screenager/Screenager.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

---

## Setup

1. Edit **`screenager.cfg`** (next to the exe). See the comments in the file; key settings:
   - `[limits] daily_minutes`, `reset_hour`, `idle_threshold_seconds`, `warn_seconds`,
     `bedtime_start` / `bedtime_end`.
   - `[override] pin`, `hotkey`.
   - `[report]` and `[mailgun]` — your Mailgun `domain`, `api_key`, `from`, `to`. (Use a Mailgun
     account; the API key is used as the HTTP Basic-auth password.)
2. **Make Windows trust it** (see next section).
3. Test it: lower `daily_minutes` to `1`, run `screenager.exe`, and watch it count down and lock.
4. Install it to start hidden at logon (run **as the monitored user's account**, approving the UAC prompt):
   ```
   screenager.exe --install
   ```
   This registers a hidden logon scheduled task **and** adds a Defender exclusion for the folder.
   Remove with `screenager.exe --uninstall`.

**Verify email config** without waiting for `send_hour`:
- `screenager.exe --test-email` — sends a quick fixed test message and exits (fastest config check).
- `screenager.exe --report-now` — builds and sends the *full* daily report (with browser history) now.

---

## Making Windows trust it (avoid the malware flag)

An unsigned app that locks the screen and reads browser history will look suspicious to Windows.
Since you have admin on the machine, two quick steps clear it:

1. **SmartScreen** flags files downloaded from the internet (the CI artifact). After downloading,
   unblock it once:
   ```powershell
   Unblock-File .\screenager.exe
   ```
   (or right-click → Properties → check **Unblock**).
2. **Microsoft Defender** may quarantine it for behavior. `--install` adds a folder exclusion
   automatically; to do it manually from an elevated PowerShell:
   ```powershell
   Add-MpPreference -ExclusionPath "C:\Program Files\Screenager"
   ```

**Optional, cleaner:** create a self-signed code-signing certificate, trust it on the machine, and
sign the exe so Windows treats it as a known publisher:

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigning -Subject "CN=Screenager" -CertStoreLocation Cert:\CurrentUser\My
# Trust it (run elevated): import $cert into LocalMachine\Root and LocalMachine\TrustedPublisher
Set-AuthenticodeSignature -FilePath .\screenager.exe -Certificate $cert
```

A real CA-issued certificate is only needed if you distribute the exe beyond this PC.

---

## Limitations & notes

- **Exclusive-fullscreen games** (legacy DirectX) may not let the visual warning draw on top — the
  **lock still fires**, and an audible alert plays. Most modern borderless-fullscreen games are fine.
- **Tamper resistance is modest.** This is a user-mode app; a determined admin user can kill it,
  delete the task, or change the clock. Keep the monitored user on a **Standard (non-admin)**
  account so they can't read the config (which contains the Mailgun key) or remove the task.
  Backward clock changes are ignored and per-tick credit is capped, but this deters casual evasion,
  not a determined user.
- **Gamepad-only play** can read as idle (no keyboard/mouse), pausing the countdown.
- **Privacy.** This is monitoring software. The config stores the Mailgun API key in plaintext —
  restrict the file's permissions and keep it off a shared account.

---

## Project layout

```
src/Screenager/
  Program.cs            entry point + CLI verbs (run / --install / --uninstall / --test-email / --report-now)
  AppController.cs      wires tracker + UI + enforcement + session/power events
  Config.cs             INI-style config parser
  Native/               P/Invoke surface
  Tracking/             logical clock, message window, activity & focus trackers
  Enforcement/          lock primitive + parent override
  Ui/                   countdown, warning, time-up, override dialog
  Data/                 SQLite persistence
  Reporting/            browser history, report builder, Mailgun mailer, scheduler
.github/workflows/build.yml   CI that publishes the standalone exe
screenager.cfg          sample configuration
```
