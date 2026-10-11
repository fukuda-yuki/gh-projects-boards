"use strict";

// Run lifecycle for delegated DSH work.
//
// One delegated task is one `dsh --profile headless --json` process. The run
// manager keeps the process handle, mirrors the JSON event stream to disk, and
// answers status/result/cancel queries from that recorded evidence, so the
// parent can be told apart: what the child model reported, and whether the run
// actually finished.

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { spawnRun } = require("./launcher");
const { describeSession } = require("./session-log");

const ACTIVE_STATUSES = new Set(["running"]);
const RECENT_EVENT_LIMIT = 400;
const STDERR_TAIL_LIMIT = 40;
const PROFILE_PATCH_FILE = "cordis.patch.yml";

const nowIso = () => new Date().toISOString();

/** Wall-clock time the run has been (or was) working, in milliseconds. */
function elapsedMs(run) {
  if (!run.started_at) return null;
  const end = run.ended_at ? Date.parse(run.ended_at) : Date.now();
  return end - Date.parse(run.started_at);
}

function writeJsonAtomic(file, value) {
  const temp = `${file}.tmp`;
  fs.writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  fs.renameSync(temp, file);
}

/** Run one Windows command and collect its output; never throws. */
function runCommand(command, args, { timeoutMs = 20000 } = {}) {
  return new Promise((resolve) => {
    let child;
    try {
      child = spawn(command, args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    } catch (error) {
      resolve({ ok: false, code: null, stdout: "", stderr: String(error.message), error: String(error.message) });
      return;
    }
    let stdout = "";
    let stderr = "";
    const timer = setTimeout(() => {
      child.kill();
      resolve({ ok: false, code: null, stdout, stderr: `${stderr}\n(timed out)` });
    }, timeoutMs);
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString("utf8");
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString("utf8");
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      resolve({ ok: false, code: null, stdout, stderr, error: String(error.message) });
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({ ok: code === 0, code, stdout, stderr });
    });
  });
}

/**
 * Whether `tasklist` still reports a pid.
 *
 * The CSV form keeps the pid in its own ASCII field, so the answer does not
 * depend on the machine's console code page (localized output would otherwise
 * arrive here mojibaked).
 */
function tasklistReportsPid(stdout, pid) {
  const rows = stdout.split(/\r?\n/).filter((line) => line.trim() !== "");
  for (const row of rows) {
    const fields = row.split(",").map((field) => field.replace(/^"|"$/g, "").trim());
    if (fields.length >= 2 && fields[1] === String(pid)) return true;
  }
  return rows.some((row) => row.includes(String(pid)));
}

class RunManager {
  /**
   * @param home - directory holding run records (`<home>/runs/<run_id>`).
   * @param installation - resolved DSH installation (see launcher.js).
   * @param dshHome - harness home whose session store holds execution records.
   * @param profile - DSH profile the delegated runs boot.
   * @param defaultPermissionMode - sandbox mode for runs that do not name one.
   * @param maxConcurrentRuns - concurrent delegated children; 1 keeps a single child.
   */
  constructor({ home, installation, dshHome, profile, defaultPermissionMode, maxConcurrentRuns, log = () => {} }) {
    this.home = home;
    this.runsDir = path.join(home, "runs");
    this.installation = installation;
    this.dshHome = dshHome;
    this.profile = profile;
    this.defaultPermissionMode = defaultPermissionMode;
    this.maxConcurrentRuns = maxConcurrentRuns;
    this.log = log;
    this.runs = new Map();
    fs.mkdirSync(this.runsDir, { recursive: true });
  }

  activeRuns() {
    return [...this.runs.values()].filter((run) => ACTIVE_STATUSES.has(run.status));
  }

  /** Refuse a new child while the configured concurrency is already in use. */
  assertCapacity() {
    const active = this.activeRuns();
    if (active.length >= this.maxConcurrentRuns) {
      const names = active.map((run) => run.id).join(", ");
      const error = new Error(
        `a delegated run is already active (${names}); this bridge runs one child at a time. ` +
          "Wait for dsh_status to leave 'running', or call dsh_cancel on it.",
      );
      error.code = "EBUSY";
      throw error;
    }
  }

  /**
   * Start one delegated run.
   * @param mode - "new" for a fresh session, "continue" to adopt `sessionId`.
   */
  start({ mode, workspace, task, sessionId = null, permissionMode, timeoutSeconds = 0, parentRunId = null }) {
    this.assertCapacity();
    const resolvedWorkspace = path.resolve(workspace);
    if (!fs.existsSync(resolvedWorkspace) || !fs.statSync(resolvedWorkspace).isDirectory()) {
      const error = new Error(`workspace is not a directory: ${resolvedWorkspace}`);
      error.code = "ENOWORKSPACE";
      throw error;
    }

    const id = `run-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    const dir = path.join(this.runsDir, id);
    fs.mkdirSync(dir, { recursive: true });

    const run = {
      id,
      mode,
      workspace: resolvedWorkspace,
      task,
      adopted_session_id: mode === "continue" ? sessionId : null,
      session_id: mode === "continue" ? sessionId : null,
      parent_run_id: parentRunId,
      profile: this.profile,
      profile_patch: path.join(this.dshHome, "profiles", this.profile, PROFILE_PATCH_FILE),
      permission_mode: permissionMode || this.defaultPermissionMode,
      timeout_seconds: timeoutSeconds,
      status: "running",
      created_at: nowIso(),
      started_at: null,
      ended_at: null,
      exit_code: null,
      signal: null,
      pid: null,
      turn_end_reason: null,
      error_event: null,
      final_text: null,
      last_text: null,
      event_counts: {},
      events_parsed: 0,
      events_unparsed: 0,
      last_event_at: null,
      stderr_tail: [],
      cancel: null,
      session_record: null,
      paths: {
        run_dir: dir,
        meta: path.join(dir, "meta.json"),
        events: path.join(dir, "events.ndjson"),
        stderr: path.join(dir, "stderr.log"),
      },
      recent_events: [],
      child: null,
      exitPromise: null,
      exitInfo: null,
      stdoutBuffer: "",
    };

    writeJsonAtomic(run.paths.meta, this.serialize(run));
    this.runs.set(id, run);

    let spawned;
    try {
      spawned = spawnRun({
        installation: this.installation,
        workspace: resolvedWorkspace,
        task,
        sessionId: mode === "continue" ? sessionId : null,
        profile: this.profile,
        permissionMode: run.permission_mode,
      });
    } catch (error) {
      run.status = "failed";
      run.error_event = `spawn failed: ${error.message}`;
      run.ended_at = nowIso();
      writeJsonAtomic(run.paths.meta, this.serialize(run));
      throw error;
    }

    run.child = spawned.child;
    run.pid = spawned.child.pid;
    run.started_at = nowIso();
    fs.writeFileSync(run.paths.events, "", "utf8");
    fs.writeFileSync(run.paths.stderr, "", "utf8");

    const eventsStream = fs.createWriteStream(run.paths.events, { flags: "a" });
    const stderrStream = fs.createWriteStream(run.paths.stderr, { flags: "a" });

    spawned.child.stdout.on("data", (chunk) => {
      eventsStream.write(chunk);
      this.consumeStdout(run, chunk.toString("utf8"));
    });
    spawned.child.stderr.on("data", (chunk) => {
      stderrStream.write(chunk);
      this.consumeStderr(run, chunk.toString("utf8"));
    });

    run.exitPromise = new Promise((resolve) => {
      spawned.child.on("close", (code, signal) => {
        run.exitInfo = { code, signal };
        eventsStream.end();
        stderrStream.end();
        this.finalize(run, code, signal);
        resolve(run.exitInfo);
      });
      spawned.child.on("error", (error) => {
        run.error_event = `child process error: ${error.message}`;
      });
    });

    if (timeoutSeconds > 0) {
      run.timeoutTimer = setTimeout(() => {
        this.log(`run ${id} exceeded ${timeoutSeconds}s; cancelling`);
        this.cancel(id, { reason: `timeout after ${timeoutSeconds}s`, status: "timed_out" }).catch((error) =>
          this.log(`timeout cancel failed for ${id}: ${error.message}`),
        );
      }, timeoutSeconds * 1000);
      run.timeoutTimer.unref?.();
    }

    writeJsonAtomic(run.paths.meta, this.serialize(run));
    return this.serialize(run);
  }

  consumeStdout(run, text) {
    run.stdoutBuffer += text;
    let index = run.stdoutBuffer.indexOf("\n");
    while (index !== -1) {
      const line = run.stdoutBuffer.slice(0, index);
      run.stdoutBuffer = run.stdoutBuffer.slice(index + 1);
      if (line.trim() !== "") this.consumeEvent(run, line);
      index = run.stdoutBuffer.indexOf("\n");
    }
  }

  consumeEvent(run, line) {
    let event;
    try {
      event = JSON.parse(line);
    } catch {
      run.events_unparsed += 1;
      return;
    }
    run.events_parsed += 1;
    run.last_event_at = nowIso();
    const type = typeof event.type === "string" ? event.type : "unknown";
    run.event_counts[type] = (run.event_counts[type] || 0) + 1;

    if (type === "session" && typeof event.sessionId === "string") run.session_id = event.sessionId;
    if (type === "final" && typeof event.text === "string") run.final_text = event.text;
    if (type === "text" && typeof event.text === "string") run.last_text = event.text;
    if (type === "error" && !run.error_event) run.error_event = event.message || "error event";
    if (type === "status" && event.phase === "turn_end") run.turn_end_reason = event.reason ?? null;

    run.recent_events.push(event);
    if (run.recent_events.length > RECENT_EVENT_LIMIT) run.recent_events.splice(0, run.recent_events.length - RECENT_EVENT_LIMIT);
    if (type === "session" || type === "final" || type === "error") writeJsonAtomic(run.paths.meta, this.serialize(run));
  }

  consumeStderr(run, text) {
    for (const line of text.split(/\r?\n/)) {
      if (line.trim() === "") continue;
      run.stderr_tail.push(line);
    }
    if (run.stderr_tail.length > STDERR_TAIL_LIMIT) {
      run.stderr_tail.splice(0, run.stderr_tail.length - STDERR_TAIL_LIMIT);
    }
  }

  /** Map the process outcome onto a run status once the child is gone. */
  finalize(run, code, signal) {
    if (run.timeoutTimer) {
      clearTimeout(run.timeoutTimer);
      run.timeoutTimer = null;
    }
    run.exit_code = code;
    run.signal = signal;
    run.ended_at = nowIso();
    if (run.cancel) {
      run.status = run.cancel.status || "cancelled";
    } else if (code === 0 && run.turn_end_reason && run.turn_end_reason.kind === "completed") {
      run.status = "completed";
    } else {
      run.status = "failed";
    }
    writeJsonAtomic(run.paths.meta, this.serialize(run));
    this.log(`run ${run.id} ${run.status} (exit=${code}${signal ? `, signal=${signal}` : ""})`);
  }

  processAlive(run) {
    if (!run.pid) return false;
    try {
      process.kill(run.pid, 0);
      return true;
    } catch {
      return false;
    }
  }

  /** Resolve a run from memory, else from its persisted record. */
  get(runId) {
    const inMemory = this.runs.get(runId);
    if (inMemory) return inMemory;
    const metaPath = path.join(this.runsDir, runId, "meta.json");
    if (!fs.existsSync(metaPath)) {
      const error = new Error(`unknown run_id: ${runId}`);
      error.code = "ENORUN";
      throw error;
    }
    return { ...JSON.parse(fs.readFileSync(metaPath, "utf8")), restored: true, child: null, recent_events: [] };
  }

  /** Wait until a run leaves 'running', or the deadline passes. */
  async waitFor(runId, seconds) {
    const run = this.get(runId);
    if (!ACTIVE_STATUSES.has(run.status)) return run;
    const deadline = Date.now() + Math.max(0, seconds) * 1000;
    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 250));
      const current = this.runs.get(runId);
      if (!current || !ACTIVE_STATUSES.has(current.status)) return current || run;
    }
    return this.runs.get(runId) || run;
  }

  /**
   * Stop a delegated run and prove that it stopped.
   *
   * Verification is independent of the child: the process tree is killed with
   * taskkill, the run's own exit event must arrive, and tasklist must no longer
   * report the pid.
   */
  async cancel(runId, { reason = "cancelled by the parent", graceSeconds = 20, status = "cancelled" } = {}) {
    const run = this.runs.get(runId) || this.get(runId);
    const base = {
      run_id: runId,
      requested_at: nowIso(),
      reason,
      pid: run.pid ?? null,
      verification: [],
      verified_stopped: false,
      already_finished: !ACTIVE_STATUSES.has(run.status),
    };
    if (base.already_finished) {
      return { ...base, cancelled: false, status: run.status, exit_code: run.exit_code ?? null };
    }

    run.cancel = { requested_at: base.requested_at, reason, status, verified_stopped: false, taskkill: null };
    if (run.timeoutTimer) {
      clearTimeout(run.timeoutTimer);
      run.timeoutTimer = null;
    }

    let taskkill = null;
    if (process.platform === "win32" && run.pid) {
      taskkill = await runCommand("taskkill", ["/PID", String(run.pid), "/T", "/F"]);
      base.verification.push({
        check: "taskkill /T /F",
        code: taskkill.code,
        output: `${taskkill.stdout}${taskkill.stderr}`.trim(),
      });
    } else {
      base.verification.push({ check: "child.kill(SIGKILL)", note: "non-Windows fallback: descendants are not reaped" });
      run.child?.kill("SIGKILL");
    }

    const exited = await Promise.race([
      run.exitPromise || Promise.resolve(run.exitInfo),
      new Promise((resolve) => setTimeout(() => resolve(null), Math.max(1, graceSeconds) * 1000)),
    ]);
    base.verification.push({ check: "process exit observed", exited: exited !== null, exit_code: run.exit_code });
    if (exited === null) {
      run.child?.kill("SIGKILL");
    }

    const initialAlive = this.processAlive(run);
    if (initialAlive) {
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
    const alive = this.processAlive(run);
    let tasklist = null;
    let listed = false;
    if (process.platform === "win32" && run.pid) {
      tasklist = await runCommand("tasklist", ["/FI", `PID eq ${run.pid}`, "/FO", "CSV", "/NH"]);
      listed = tasklistReportsPid(tasklist.stdout, run.pid);
      base.verification.push({
        check: `tasklist /FI "PID eq ${run.pid}" /FO CSV /NH`,
        exit_code: tasklist.code,
        pid_listed: listed,
        output: tasklist.stdout.trim(),
        output_note: "console output arrives in the machine's OEM code page; pid_listed is the authoritative field",
      });
    }
    base.verified_stopped = exited !== null && !alive && !listed;
    base.verification.push({ check: "pid no longer alive", alive_before_recheck: initialAlive, alive });
    base.exit_code = run.exit_code ?? null;
    base.status = run.status;
    base.elapsed_ms = run.started_at ? Date.now() - Date.parse(run.started_at) : null;

    run.cancel = {
      ...run.cancel,
      verified_at: nowIso(),
      verified_stopped: base.verified_stopped,
      taskkill: taskkill ? { code: taskkill.code, output: `${taskkill.stdout}${taskkill.stderr}`.trim() } : null,
    };
    if (run.status === "running") {
      // The exit event may already have set a status; keep cancellation authoritative.
      run.status = status;
    }
    await this.attachSessionRecord(run);
    writeJsonAtomic(run.paths.meta, this.serialize(run));
    return { ...base, cancelled: true };
  }

  /** Locate (and once found, remember) the durable session log of a run. */
  async attachSessionRecord(run) {
    if (!run.session_id) return null;
    if (run.session_record && run.session_record.record_path) return run.session_record;
    try {
      const facts = describeSession(this.dshHome, run.session_id);
      if (facts.record_found) run.session_record = facts;
      return facts;
    } catch (error) {
      this.log(`session record lookup failed for ${run.id}: ${error.message}`);
      return null;
    }
  }

  serialize(run) {
    const { child, exitPromise, timeoutTimer, recent_events, stdoutBuffer, ...rest } = run;
    return {
      ...rest,
      process_alive: this.processAlive(run),
      elapsed_ms: elapsedMs(run),
    };
  }

  /** Status view for dsh_status. */
  status(runId) {
    const run = this.get(runId);
    const session = run.session_record || (run.session_id ? { session_id: run.session_id, record_found: false } : null);
    return {
      run_id: run.id,
      status: run.status,
      workspace: run.workspace,
      mode: run.mode,
      session_id: run.session_id,
      adopted_session_id: run.adopted_session_id ?? null,
      parent_run_id: run.parent_run_id ?? null,
      started_at: run.started_at,
      ended_at: run.ended_at,
      elapsed_ms: elapsedMs(run),
      pid: run.pid,
      process_alive: this.runs.get(runId) ? this.processAlive(run) : false,
      exit_code: run.exit_code,
      signal: run.signal ?? null,
      turn_end_reason: run.turn_end_reason,
      error_event: run.error_event,
      finished: run.status !== "running",
      event_counts: run.event_counts,
      events_parsed: run.events_parsed,
      events_unparsed: run.events_unparsed,
      last_event_at: run.last_event_at,
      stderr_tail: run.stderr_tail,
      cancel: run.cancel,
      profile: run.profile,
      permission_mode: run.permission_mode,
      session_record_path: session && session.record_path ? session.record_path : null,
      records: run.paths,
    };
  }

  /**
   * Result view for dsh_result: the child's own report and the process outcome
   * stay separate fields so neither can be mistaken for the other.
   */
  result(runId, { recentEvents = 0 } = {}) {
    const run = this.get(runId);
    const live = this.runs.get(runId);
    const processOk = run.status === "completed";
    const hasReport = typeof run.final_text === "string" && run.final_text.trim() !== "";
    const session = run.session_record || null;
    const events = live ? live.recent_events : [];
    const toolCalls = events
      .filter((event) => event.type === "tool_call")
      .map((event) => ({ tool: event.tool, input: event.input, callId: event.callId }));

    return {
      run_id: run.id,
      status: run.status,
      judgement: {
        process_finished_normally: processOk,
        model_report_present: hasReport,
        note:
          "model_report is the child model's own message. process_finished_normally reflects the DSH process " +
          "(exit code 0 with a turn/end reason of 'completed'). Keep them apart: a model can claim success on a " +
          "run that failed, and a clean exit says nothing about whether the change is correct.",
      },
      process_outcome: {
        finished: run.status !== "running",
        exit_code: run.exit_code,
        signal: run.signal ?? null,
        turn_end_reason: run.turn_end_reason,
        error_event: run.error_event,
        started_at: run.started_at,
        ended_at: run.ended_at,
        elapsed_ms: elapsedMs(run),
      },
      model_report: {
        available: hasReport,
        text: hasReport ? run.final_text : null,
        partial_text: !hasReport && typeof run.last_text === "string" ? run.last_text : null,
      },
      session: {
        session_id: run.session_id,
        adopted: run.mode === "continue",
        adopted_session_id: run.adopted_session_id ?? null,
        record_path: session && session.record_path ? session.record_path : null,
        record_found: Boolean(session && session.record_path),
      },
      profile: { name: run.profile, patch: run.profile_patch ?? null },
      records: {
        run_dir: run.paths.run_dir,
        meta: run.paths.meta,
        events: run.paths.events,
        stderr: run.paths.stderr,
        session_log: session && session.record_path ? session.record_path : null,
      },
      tool_calls: toolCalls.length > 0 ? toolCalls : undefined,
      recent_events: recentEvents > 0 ? events.slice(-recentEvents) : undefined,
    };
  }

  /** Runs known to this server: live children plus every record on disk. */
  list({ limit = 20 } = {}) {
    const seen = new Map();
    let entries = [];
    try {
      entries = fs.readdirSync(this.runsDir, { withFileTypes: true }).filter((entry) => entry.isDirectory());
    } catch {
      entries = [];
    }
    for (const entry of entries) {
      const metaPath = path.join(this.runsDir, entry.name, "meta.json");
      try {
        const meta = JSON.parse(fs.readFileSync(metaPath, "utf8"));
        seen.set(meta.id, {
          run_id: meta.id,
          status: meta.status,
          mode: meta.mode,
          workspace: meta.workspace,
          session_id: meta.session_id,
          created_at: meta.created_at,
          ended_at: meta.ended_at,
          exit_code: meta.exit_code,
        });
      } catch {
        // A record without a readable meta.json is not a run this bridge owns.
      }
    }
    return [...seen.values()]
      .sort((a, b) => String(b.created_at).localeCompare(String(a.created_at)))
      .slice(0, limit);
  }
}

module.exports = { RunManager, runCommand, tasklistReportsPid };
