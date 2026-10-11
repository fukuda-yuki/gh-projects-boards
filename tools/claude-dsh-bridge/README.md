# Claude Code ⇄ DeepSeek Harness bridge (Windows)

Japanese version: [README.ja.md](README.ja.md)

A local MCP server that lets Claude Code stay the orchestrator while a DeepSeek
Harness agent — DeepSeek V4.1 Flash over OpenCode Go — does the implementation
work.

| Role | Who | Responsibility |
|---|---|---|
| Parent | Claude Code | requirements, design, task split, review of the result, rework requests, completion decision |
| Child | DSH agent run (`headless` profile) | code investigation, edits, test execution, failure analysis and fixes |
| Link | this MCP server | hands one request at a time to the child, returns state, report, end reason and session identity |

The delegation is one-way: Claude Code calls the bridge, the bridge starts a DSH
agent run. DSH never calls Claude Code back, and the child is a DSH agent with
DSH's own tools and sandbox — not Claude Code running as a subagent.

## Verified environment (this machine, 2026-10-11)

Nothing below is assumed from the public master branch; every value was read
from the installed application.

| Fact | Value |
|---|---|
| Install root | `C:\Users\mwam0\AppData\Local\Programs\DeepSeek Harness` |
| Application version | `44.0.0` (the `version` file in the install root); CLI `dsh --version` → **`0.2.0-rc.2`** |
| Launcher | `resources\runtime\cli\bin\dsh.cmd` (runs `DeepSeek Harness.exe` with `ELECTRON_RUN_AS_NODE=1 --expose-internals` on the bundled CLI entry) |
| CLI entry | `resources\app.asar\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\cli.js` (inside the asar archive) |
| Harness home | `%DSH_HOME%` = `C:\Users\mwam0\.dsh`; profile `desktop` is managed exclusively by the Electron app, so a CLI boot of `desktop` is refused |
| Shipped CLI-bootable profiles | `headless`, `web`, `acp`, `sdk`, `sdk-minimal` (created on first use from the shipped templates) |
| Model route | pi-ai provider `opencode-go` ("OpenCode Go"), model `deepseek-v4.1-flash` ("DeepSeek V4.1 Flash"), api `openai-completions`, `https://opencode.ai/zen/go/v1`, context 1 000 000, max output 384 000 |
| Credential | `OPENCODE_API_KEY` (pi-ai's env auth for `opencode-go`); present as a **user** environment variable, so any normally launched process inherits it |
| Session store | `%DSH_HOME%\sessions\<workspace-slug>\<session-id>\session.v4.jsonl.zstd` (multi-frame zstd JSONL) |
| Node.js | `C:\Program Files\nodejs\node.exe` v24.15.0 (needs ≥ 22.15 for `zlib.zstdDecompressSync`) |

Other integration surfaces found in the installation were inspected and are
**not** used (see the next section): `dsh-sdk-protocol` / `dsh-sdk-jsonrpc-server`
/ `dsh-sdk-app`, `dsh-acp` / `dsh-acp-app`, `dsh-mcp-client` / `dsh-mcp-resources`,
`dsh-host-webserver` + `dsh-api-*`, and `dsh-hooks-claude-code`.

## Why a standalone MCP server instead of a DSH-internal plugin

| Option | What exists here | Verdict |
|---|---|---|
| **Standalone MCP server (chosen)** driving the documented `dsh --profile headless` CLI | `headless` is a shipped template with `--json` (NDJSON events), `--session-id` (session adoption) and exit-code semantics documented in the installed package | Reuses DSH's launcher, agent loop, sandboxed tools, session persistence and session adoption. Process-per-run gives a verifiable lifecycle. No dependencies, no build step, only the documented CLI contract. |
| MCP plugin *inside* DSH | No MCP **server** plugin ships — `dsh-mcp-client` / `dsh-mcp-resources` are the opposite direction. Would mean authoring a Cordis plugin, building it and installing it into the profile with pnpm | Much more code and an install step, compiled against `0.2.0-rc.2` internals that change between releases. Rejected |
| DSH SDK JSON-RPC server (`dsh-sdk-jsonrpc-server`, profile `sdk`) | Documented wire protocol: `initialize`, `session/prompt`, `shutdown` plus `session.event` / `session.status` notifications — and explicitly **no cancel method**: "a client abandons a turn by closing the runtime process" | Would still need an MCP translation layer, and requirement 4 (cancel and *prove* the stop) would degrade to killing a shared runtime. Rejected |
| ACP (`dsh-acp`, profile `acp`) | DSH as an ACP *agent* for an editor that is an ACP client | Claude Code is not an ACP client. Wrong direction. Rejected |
| Desktop app HTTP API (`dsh-host-webserver`, `dsh-api-*`, `127.0.0.1:19387`) | The running Web/desktop session exposes session control over HTTP | Ties delegation to a live GUI session and its browser-session secret; the `desktop` profile cannot be booted by the CLI at all. Rejected |
| Claude Code hooks inside DSH (`dsh-hooks-claude-code`) | DSH *consumes* Claude Code `hooks.json` command hooks during DSH runs | The reverse of what was asked (it lets DSH reuse Claude Code hooks). Unrelated. Rejected |

Practical consequence: the bridge only needs the DSH launcher plus the exported
CLI surface. If DSH keeps `--profile headless --json --session-id` and the
session store layout, an application upgrade does not require touching it.

## How the five requirements are met

| Requirement | Implementation |
|---|---|
| 1. Start a DSH implementation agent with a work folder and a task | `dsh_delegate` (task + optional `workspace`) spawns `dsh --profile headless --json` with the workspace as its process cwd; the task is written to stdin (no command-line length or quoting limits) |
| 2. Execution state, final report, end reason, session info | `dsh_status` (state, elapsed, pid liveness, exit code, event counters, record paths) and `dsh_result` (report + process outcome + session id and log path) |
| 3. Review feedback as a rework request, stating session vs new run | `dsh_rework` with `mode: "continue"` (adopts `--session-id`, keeps the conversation) or `mode: "new_run"`; the reply always carries `same_session`, `session_mode` and a sentence naming which one happened |
| 4. Cancel a run and confirm it stopped | `dsh_cancel` kills the process tree (`taskkill /T /F`), waits for the run's own exit event, then re-probes the pid (`process.kill(pid, 0)`) and `tasklist /FO CSV /NH`; `verified_stopped` is only true when all checks agree |
| 5. Report provider/model/profile actually used and where the records are | `dsh_result` returns `model_route` read back from DSH's own session log (`request/header` → `config.provider/model/reasoningEffort`, `request/context` → `contextWindow`), plus the profile name/patch path and every record path |

The completion decision is deliberately split, because a model report is a claim
and a clean exit is not a correctness proof:

* `model_report.text` — the child's last non-empty assistant message (`final` event).
* `process_outcome` — exit code, signal, and the `turn/end` reason (`completed`, or whatever aborted the turn).
* `judgement.process_finished_normally` — exit code 0 **and** `turn/end` reason `completed`.
* `judgement.model_report_present` — whether the child said anything at all.

Claude Code is expected to verify the workspace itself (diff, tests) before
treating a run as done.

## Install on Windows

### Prerequisites

* DeepSeek Harness installed for the current user (default location is fine).
* Node.js ≥ 22.15 on PATH (the bridge reads zstd-compressed session logs).
* `OPENCODE_API_KEY` set for the user account (the OpenCode Go credential).
* Claude Code (`claude` CLI) for registration.

### One-step setup

```powershell
cd <repo>\tools\claude-dsh-bridge
pwsh -File install.ps1 -Register
```

The script:

1. locates Node.js and validates zstd support;
2. locates the DeepSeek Harness installation through the bridge's own resolver
   (and prints which path it found, plus whether the CLI entry is a real file or
   lives inside `app.asar`);
3. loads `dsh headless --help` once so DSH creates `%DSH_HOME%\profiles\headless`
   from its shipped template, then writes `profile\cordis.patch.yml` into it
   (existing custom patch layers are preserved unless `-Force`, which keeps a backup);
4. verifies the composed profile with `--dump-config` and fails if
   `opencode-go` / `deepseek-v4.1-flash` are not in it;
5. reports whether `OPENCODE_API_KEY` is present (never its value);
6. with `-Register`, registers the MCP server for Claude Code.

Manual equivalent, if the script is not used:

```powershell
$dsh = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
& $dsh headless --help                     # creates %DSH_HOME%\profiles\headless
Copy-Item .\profile\cordis.patch.yml "$env:DSH_HOME\profiles\headless\cordis.patch.yml"
claude mcp add --scope user dsh-flash -- "C:\Program Files\nodejs\node.exe" "<repo>\tools\claude-dsh-bridge\server.js"
```

### PATH notes

* The bridge does **not** need `dsh` on PATH: it resolves the installation by
  absolute path (explicit `DSH_BRIDGE_ROOT` / `DSH_BRIDGE_LAUNCHER`, then PATH,
  then the default install locations) and validates the launcher before use.
* Registration stores the **absolute** Node.js path, so Claude Code does not need
  a working `node` on PATH either. If you register by hand with
  `claude mcp add ... -- node server.js`, the PATH lookup happens when Claude Code
  starts the server.
* DSH's own PATH management (the launcher's `install` operation writes
  `HKCU\Environment\Path` and records ownership under
  `HKCU\Software\DeepSeekHarness\Command`) is untouched by this bridge.

### Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `DSH_BRIDGE_HOME` | `%LOCALAPPDATA%\claude-dsh-bridge` | bridge run records (`runs\<run_id>\`) |
| `DSH_HOME` | `%USERPROFILE%\.dsh` | DSH home: profiles and session store |
| `DSH_BRIDGE_PROFILE` | `headless` | DSH profile every delegated run boots |
| `DSH_BRIDGE_PERMISSION_MODE` | `workspace-write` | default sandbox mode for runs |
| `DSH_BRIDGE_MAX_RUNS` | `1` | concurrent delegated children (one, per the agreed scope) |
| `DSH_BRIDGE_DEFAULT_TIMEOUT_SECONDS` | `0` | cancel a run after this long; 0 = no timeout |
| `DSH_BRIDGE_ROOT` | – | install root when auto-detection is not enough |

Pass them at registration time with `claude mcp add -e KEY=value`, or set them
in the environment Claude Code is launched from.

### Claude Code registration and approvals

```powershell
claude mcp add --scope user dsh-flash -- "<node path>" "<repo>\tools\claude-dsh-bridge\server.js"
claude mcp list          # dsh-flash should report "Connected"
claude mcp get dsh-flash
```

Claude Code asks for approval the first time a tool is used; the tools appear as
`mcp__dsh-flash__dsh_delegate`, `mcp__dsh-flash__dsh_status`,
`mcp__dsh-flash__dsh_result`, `mcp__dsh-flash__dsh_rework`,
`mcp__dsh-flash__dsh_cancel` and `mcp__dsh-flash__dsh_info`. Restart Claude Code
after registering so it connects.

## Tool reference

### `dsh_delegate`
`task` (required), `workspace`, `session_id`, `permission_mode`, `timeout_seconds`, `wait_seconds`.
Starts a run and returns `run_id`, `session_mode` (`new` / `continued`), session id (once reported), profile, permission mode and record paths.

### `dsh_status`
`run_id` (required), `wait_seconds`. Returns `status` (`running` / `completed` / `failed` / `cancelled` / `timed_out`), elapsed time, pid liveness, exit code, `turn_end_reason`, event counters, stderr tail, cancel evidence, session record path.

### `dsh_result`
`run_id` (required), `recent_events`. Returns the split judgement, `model_report`, `process_outcome`, `session`, `profile`, `model_route` (provider/model actually used) and every record path. While a run is still going it returns the partial text instead of a final report.

### `dsh_rework`
`feedback` (required), `mode` (`continue` default / `new_run`), `run_id`, `session_id`, `workspace`, `task`, `permission_mode`, `timeout_seconds`, `wait_seconds`.
`continue` adopts the previous session (DSH only adopts a session in the working directory it recorded, so a mismatched `workspace` is refused with a message telling you to use `new_run`). The reply states `same_session` and, for `continue`, the adopted session id.

### `dsh_cancel`
`run_id` (required), `grace_seconds`. Returns `cancelled`, `status`, `exit_code` and `verification[]` — the taskkill result, the observed exit event, the pid probe and the tasklist CSV verdict.

### `dsh_info`
`run_id` (optional). DSH version, installation paths and how they were found, profile name/dir/patch and configured provider/model/reasoning effort, credential presence, run list, session store location and the route observed in a run's session log.

## Records and evidence locations

| What | Where |
|---|---|
| Bridge run record | `%DSH_BRIDGE_HOME%\runs\<run_id>\meta.json` |
| Raw `--json` event stream of a run | `%DSH_BRIDGE_HOME%\runs\<run_id>\events.ndjson` |
| Child stderr (reasoning, diagnostics) | `%DSH_BRIDGE_HOME%\runs\<run_id>\stderr.log` |
| DSH's own durable session log | `%DSH_HOME%\sessions\<workspace-slug>\<session-id>\session.v4.jsonl.zstd` |
| Session projection cache | `%DSH_HOME%\storages\session_projcache\sessions\<session-id>.json` |

`dsh_status` and `dsh_result` return these paths, so the parent can cite them.

## Verification performed

Commands (run from `tools\claude-dsh-bridge`):

```powershell
node test/unit.js   # protocol, launcher, session-log and cancel parsing; no model call
node test/e2e.js    # real MCP client -> real DSH runs -> real file changes
```

| Check | Result |
|---|---|
| `node test/unit.js` | **10/10 passed** (MCP initialize/notifications/errors, launcher command line, asar CLI entry detection, multi-frame zstd session-log facts, tasklist CSV parsing, configuration and profile patch reading) |
| `node test/e2e.js` | **33/33 passed in 88 s** (first complete run: 33/33 in 60 s), driving the MCP server exactly as Claude Code does |
| `claude mcp get dsh-flash` | **Status: √ Connected** — Claude Code started the server itself and completed the MCP handshake |
| Pre-bridge smoke run | `dsh --profile headless --json "implement multiply …"` → exit 0 in 26 s, `demo/calc.js` really changed, `node demo/check.js` green |

What the end-to-end cycle established (evidence JSON in `test/artifacts/e2e-*.json`):

1. **Delegate → real change → verify.** `dsh_delegate` implemented `multiply` in the scratch copy of `demo/calc.js`; the file content changed on disk, and the *parent's own* run of `node demo/check.js` printed `PASSED 3/3`.
2. **Result hand-back.** `dsh_result` returned the child's final report, `process_finished_normally: true` (exit 0 + `turn/end` `completed`), a non-empty `model_report`, the session id, and `model_route` = `opencode-go` / `deepseek-v4.1-flash` read back from the session log, with the log path.
3. **Review feedback into the same session.** `dsh_rework` with `mode: "continue"` reported `same_session: true` and adopted the same `session-…` id; the child added the `TypeError` guard and new cases, the parent's `multiply('x', 2)` probe throws `TypeError`, and `node demo/check.js` printed `PASSED 6/6`.
4. **One child at a time.** A second `dsh_delegate` while a run was active was refused with `a delegated run is already active (…)`.
5. **Cancel with proof.** A deliberately long run was cancelled: `taskkill /T /F` exit 0, the run's own exit event fired, `process.kill(pid, 0)` reported the pid gone and `tasklist /FO CSV /NH` no longer listed it → `verified_stopped: true`; the run's status stayed `cancelled` and `dsh_result` reported `process_finished_normally: false`.

## Unverified / known limits

* **An interactive Claude Code tool call was not performed** here. What is
  verified: the MCP handshake and all six tools over real stdio (test/e2e.js), and
  that Claude Code's own health check connects to the registered server
  (`claude mcp get dsh-flash` → `Connected`). The remaining step is an actual
  prompt inside Claude Code.
* **Permission allow-listing** for `mcp__dsh-flash__*` in `.claude/settings.json`
  is described by Claude Code's own documentation and was not exercised here; the
  interactive approval prompt is the path that was verified by registration.
* **Single child only** (`DSH_BRIDGE_MAX_RUNS=1`). Raising it is possible but
  untested.
* **Headless approvals fail closed.** A run has no interactive answerer, so a tool
  call that needs an approval beyond `workspace-write` inside its workspace will
  not be granted. Write tasks accordingly, or pass `permission_mode` explicitly.
* **Windows-only cancellation path.** On other platforms `dsh_cancel` falls back to
  `SIGKILL` on the direct child and does not reap descendants.
* **Session-log reading** needs Node.js ≥ 22.15 with zstd; without it the route
  fields report `available: false` while everything else keeps working.
* **Run records are not garbage-collected**; `%DSH_BRIDGE_HOME%\runs` grows with use.
* The bridge does not try to reconstruct a diff or run the repository's tests: the
  parent decides what "verified" means for its own change.

## First delegation from Claude Code

A parent flow that matches the requirement set:

1. `dsh_info` — confirm the installed DSH version, the configured route and that
   `OPENCODE_API_KEY` is present before spending a run.
2. `dsh_delegate` with the task and the work folder (optionally
   `wait_seconds: 240` to skip a poll round).
3. `dsh_status` until the state is no longer `running`; check `exit_code` and
   `turn_end_reason` — not just the child's wording.
4. `dsh_result` for the report and `model_route`; review the actual diff and run
   the project's own checks yourself.
5. `dsh_rework` with `mode: "continue"` and concrete findings to keep the child's
   context; use `mode: "new_run"` when the review invalidates the approach. The
   reply names which one happened.
6. `dsh_cancel` if a run drifts; `verified_stopped` tells you the process really
   is gone.

## File map

| Path | Role |
|---|---|
| `server.js` | MCP entry point: configuration, tool definitions, handlers |
| `lib/mcp.js` | stdio JSON-RPC MCP server (initialize, tools/list, tools/call, ping, notifications) |
| `lib/launcher.js` | locating the DSH installation (including the CLI entry inside `app.asar`) and building the run command |
| `lib/runner.js` | run lifecycle: spawn, event capture, status/result views, cancellation with verification |
| `lib/session-log.js` | reading DSH's multi-frame zstd session log for the route actually used |
| `profile/cordis.patch.yml` | the DSH profile patch installed into `%DSH_HOME%\profiles\headless` |
| `install.ps1` | profile preparation, verification and Claude Code registration |
| `demo/calc.js`, `demo/check.js` | reference fixtures the end-to-end check copies into a scratch folder |
| `test/unit.js`, `test/e2e.js`, `test/mcp-client.js` | logic checks, the real delegation cycle, and the MCP client used by both |
