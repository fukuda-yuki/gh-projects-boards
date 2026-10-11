"use strict";

// End-to-end check of the bridge: delegate -> real file change -> parent-side
// verification -> result hand-back -> review feedback (same session) -> cancel.
//
// It drives the MCP server exactly as Claude Code does (stdio, JSON-RPC, tools),
// sends real tasks to a real DSH run on DeepSeek V4.1 Flash, and verifies the
// workspace itself instead of trusting the child's report.
//
// Requires OPENCODE_API_KEY in the environment (the OpenCode Go credential) and
// writes outside the Claude Code workspace (bridge home, DSH session store).
//
// Usage: node test/e2e.js [--keep]

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const { createClient } = require("./mcp-client");

const BRIDGE_DIR = path.resolve(__dirname, "..");
const WORK_DIR = path.join(BRIDGE_DIR, "demo", ".work");
const ARTIFACTS = path.join(__dirname, "artifacts");
const keep = process.argv.includes("--keep");

const checks = [];
function record(name, ok, detail) {
  checks.push({ name, ok, detail });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}${detail ? ` — ${detail}` : ""}`);
}

function expect(name, condition, detail) {
  record(name, Boolean(condition), detail);
  if (!condition) throw new Error(`check failed: ${name}${detail ? ` (${detail})` : ""}`);
}

/** Run a command in the delegated workspace and return its exit code and output. */
function runInWorkspace(script, args = []) {
  const result = spawnSync(process.execPath, [script, ...args], { cwd: WORK_DIR, encoding: "utf8" });
  return { code: result.status, stdout: `${result.stdout || ""}${result.stderr || ""}`.trim() };
}

function prepareWorkspace() {
  fs.rmSync(WORK_DIR, { recursive: true, force: true });
  fs.mkdirSync(WORK_DIR, { recursive: true });
  for (const file of ["calc.js", "check.js"]) {
    fs.copyFileSync(path.join(BRIDGE_DIR, "demo", file), path.join(WORK_DIR, file));
  }
}

async function waitForRun(client, runId, seconds) {
  const status = await client.callTool("dsh_status", { run_id: runId, wait_seconds: seconds });
  expect(`dsh_status returns a payload for ${runId}`, status.parsed !== null, status.text.slice(0, 200));
  return status.parsed;
}

async function main() {
  if (!process.env.OPENCODE_API_KEY) {
    console.error("OPENCODE_API_KEY is not set in this process; the delegated run cannot authenticate to OpenCode Go.");
    process.exit(2);
  }

  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const home = process.env.DSH_BRIDGE_HOME || path.join(ARTIFACTS, `home-${stamp}`);
  fs.mkdirSync(home, { recursive: true });
  fs.mkdirSync(ARTIFACTS, { recursive: true });
  prepareWorkspace();

  const stderrLog = [];
  const client = createClient({
    command: process.execPath,
    args: [path.join(BRIDGE_DIR, "server.js")],
    env: { ...process.env, DSH_BRIDGE_HOME: home },
    // The server never needs the delegated folder as its own cwd, and a live
    // process holding it would block the scratch-folder cleanup below.
    cwd: BRIDGE_DIR,
    onStderr: (chunk) => {
      stderrLog.push(chunk);
      if (process.env.DSH_BRIDGE_E2E_VERBOSE) process.stderr.write(chunk);
    },
  });

  const evidence = { started_at: new Date().toISOString(), home, workspace: WORK_DIR, steps: {} };
  const manifestBefore = fs.readFileSync(path.join(WORK_DIR, "check.js"), "utf8");

  try {
    const initialized = await client.handshake();
    record("MCP handshake", initialized.serverInfo.name === "dsh-flash-bridge", initialized.serverInfo.name);

    const listed = await client.request("tools/list", {});
    const names = listed.tools.map((tool) => tool.name).sort();
    expect(
      "six tools are exposed",
      names.join(",") === "dsh_cancel,dsh_delegate,dsh_info,dsh_result,dsh_rework,dsh_status",
      names.join(","),
    );

    const info = (await client.callTool("dsh_info", {})).parsed;
    expect("dsh_info reports the installed DSH version", typeof info.dsh.version === "string" && info.dsh.version.length > 0, info.dsh.version);
    expect(
      "dsh_info reports the configured OpenCode Go route",
      info.profile.configured_provider === "opencode-go" && info.profile.configured_model === "deepseek-v4.1-flash",
      `${info.profile.configured_provider}/${info.profile.configured_model}`,
    );
    expect("dsh_info reports the OpenCode Go credential as present", info.credentials.OPENCODE_API_KEY === "set", info.credentials.OPENCODE_API_KEY);
    evidence.steps.info = info;

    // --- 1. delegate a real change -------------------------------------------
    const task =
      "Implement `multiply(a, b)` in `calc.js` in this working folder so that `node check.js` exits 0. " +
      "Keep `add` unchanged and keep the existing CommonJS export style. Run `node check.js` yourself, confirm every " +
      "case passes, and report the exact command you ran with its raw output.";
    const delegated = (await client.callTool("dsh_delegate", { task, workspace: WORK_DIR })).parsed;
    expect("dsh_delegate starts a new session", delegated.session_mode === "new", delegated.session_mode);
    expect("dsh_delegate returns a run id", typeof delegated.run_id === "string" && delegated.run_id.startsWith("run-"), delegated.run_id);
    evidence.steps.delegate = delegated;

    const first = await waitForRun(client, delegated.run_id, 600);
    evidence.steps.first_status = first;
    expect("first run finished", first.status !== "running", first.status);
    expect("first run completed with exit code 0", first.status === "completed" && first.exit_code === 0, `${first.status}/${first.exit_code}`);
    expect(
      "first run reported a completed turn",
      first.turn_end_reason && first.turn_end_reason.kind === "completed",
      JSON.stringify(first.turn_end_reason),
    );
    expect("first run reported its session id", typeof first.session_id === "string" && first.session_id.startsWith("session-"), first.session_id);
    expect("first run wrote its event record", fs.existsSync(first.records.events) && fs.statSync(first.records.events).size > 0, first.records.events);

    const calcAfterFirst = fs.readFileSync(path.join(WORK_DIR, "calc.js"), "utf8");
    expect("the delegated run changed the real file", calcAfterFirst !== fs.readFileSync(path.join(BRIDGE_DIR, "demo", "calc.js"), "utf8"));
    const verified = runInWorkspace("check.js");
    expect("parent-side verification passes after the delegated change", verified.code === 0, verified.stdout.split("\n").slice(-1)[0]);

    const firstResult = (await client.callTool("dsh_result", { run_id: delegated.run_id, recent_events: 6 })).parsed;
    evidence.steps.first_result = firstResult;
    expect("dsh_result separates the process outcome from the model report", firstResult.judgement.process_finished_normally === true && firstResult.judgement.model_report_present === true);
    expect("dsh_result returns the model's final report", typeof firstResult.model_report.text === "string" && firstResult.model_report.text.length > 0);
    expect(
      "dsh_result reports the provider and model actually used",
      firstResult.model_route.provider === "opencode-go" && firstResult.model_route.model === "deepseek-v4.1-flash",
      `${firstResult.model_route.provider}/${firstResult.model_route.model}`,
    );
    expect(
      "dsh_result points at the durable session log",
      typeof firstResult.model_route.record_path === "string" && fs.existsSync(firstResult.model_route.record_path),
      firstResult.model_route.record_path,
    );

    // --- 2. review feedback into the same session ---------------------------
    const feedback =
      "Review findings on the change you just made:\n" +
      "1. `multiply` must throw a `TypeError` when either argument is not a finite number " +
      "(`multiply('x', 2)`, `multiply(NaN, 2)`, `multiply(Infinity, 2)`).\n" +
      "2. Extend `check.js` with those three cases so `node check.js` still exits 0.\n" +
      "Keep `add` unchanged. Run `node check.js` and report its raw output.";
    const reworked = (await client.callTool("dsh_rework", { run_id: delegated.run_id, feedback })).parsed;
    evidence.steps.rework = reworked;
    expect("dsh_rework continues the same session", reworked.same_session === true, reworked.note);
    expect("dsh_rework adopts the previous session id", reworked.adopted_session_id === first.session_id, reworked.adopted_session_id);

    const second = await waitForRun(client, reworked.run_id, 600);
    evidence.steps.second_status = second;
    expect("rework run completed", second.status === "completed" && second.exit_code === 0, `${second.status}/${second.exit_code}`);
    expect("rework stayed in the adopted session", second.session_id === first.session_id, `${second.session_id} vs ${first.session_id}`);

    const guard = spawnSync(
      process.execPath,
      ["-e", "const {multiply}=require('./calc.js'); try { multiply('x',2); console.log('NO_THROW'); } catch (e) { console.log(e.constructor.name); }"],
      { cwd: WORK_DIR, encoding: "utf8" },
    );
    expect("the rework enforces the requested TypeError", guard.stdout.trim() === "TypeError", guard.stdout.trim());
    const checkAfterSecond = fs.readFileSync(path.join(WORK_DIR, "check.js"), "utf8");
    expect("the rework extended the verification cases", checkAfterSecond !== manifestBefore);
    const verifiedAgain = runInWorkspace("check.js");
    expect("parent-side verification passes after the rework", verifiedAgain.code === 0, verifiedAgain.stdout.split("\n").slice(-1)[0]);

    // --- 3. one child at a time, then cancel and prove it stopped -----------
    const longTask =
      "Do not modify any file. Run exactly this command and wait for it to finish before you report anything: " +
      "`node -e \"setTimeout(() => console.log('slept'), 300000)\"`. Then report its output.";
    const longRun = (await client.callTool("dsh_delegate", { task: longTask, workspace: WORK_DIR })).parsed;
    evidence.steps.long_run = longRun;

    const busy = await client.callTool("dsh_delegate", { task: "noop", workspace: WORK_DIR });
    expect("a second child is refused while one is active", busy.isError === true, busy.text.slice(0, 160));

    let running = null;
    for (let attempt = 0; attempt < 40; attempt += 1) {
      running = (await client.callTool("dsh_status", { run_id: longRun.run_id })).parsed;
      if (running.status === "running" && running.process_alive) break;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
    expect("the long run is running before cancellation", running && running.status === "running", running && running.status);

    const cancelled = (await client.callTool("dsh_cancel", { run_id: longRun.run_id, grace_seconds: 20 })).parsed;
    evidence.steps.cancel = cancelled;
    expect("dsh_cancel reports the run as cancelled", cancelled.cancelled === true && cancelled.status === "cancelled", `${cancelled.cancelled}/${cancelled.status}`);
    expect("dsh_cancel verified the process actually stopped", cancelled.verified_stopped === true, JSON.stringify(cancelled.verification));

    const afterCancel = (await client.callTool("dsh_status", { run_id: longRun.run_id })).parsed;
    evidence.steps.after_cancel = afterCancel;
    expect("the cancelled run stays stopped", afterCancel.status === "cancelled" && afterCancel.process_alive === false, `${afterCancel.status}/${afterCancel.process_alive}`);
    const cancelledResult = (await client.callTool("dsh_result", { run_id: longRun.run_id })).parsed;
    expect(
      "a cancelled run is not reported as a normal completion",
      cancelledResult.judgement.process_finished_normally === false,
      JSON.stringify(cancelledResult.judgement),
    );
  } finally {
    try {
      client.close();
      // Let the server finish; it cancels any active run on stdin close.
      await new Promise((resolve) => {
        if (client.child.exitCode !== null) {
          resolve();
          return;
        }
        client.child.once("close", resolve);
        setTimeout(resolve, 20000).unref?.();
      });
    } catch (error) {
      console.error(`cleanup: closing the server failed: ${error.message}`);
    }
    evidence.finished_at = new Date().toISOString();
    evidence.checks = checks;
    evidence.server_stderr_tail = stderrLog.join("").split("\n").slice(-25);
    evidence.calc_js = fs.existsSync(path.join(WORK_DIR, "calc.js")) ? fs.readFileSync(path.join(WORK_DIR, "calc.js"), "utf8") : null;
    evidence.check_js = fs.existsSync(path.join(WORK_DIR, "check.js")) ? fs.readFileSync(path.join(WORK_DIR, "check.js"), "utf8") : null;
    const evidencePath = path.join(ARTIFACTS, `e2e-${stamp}.json`);
    fs.mkdirSync(ARTIFACTS, { recursive: true });
    fs.writeFileSync(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
    console.log(`\nevidence: ${evidencePath}`);
    if (!keep) {
      try {
        fs.rmSync(WORK_DIR, { recursive: true, force: true });
      } catch (error) {
        console.error(`cleanup: could not remove ${WORK_DIR}: ${error.message}`);
      }
    }
  }

  const failed = checks.filter((check) => !check.ok);
  console.log(`${checks.length - failed.length}/${checks.length} checks passed`);
  process.exit(failed.length === 0 ? 0 : 1);
}

main().catch((error) => {
  console.error(`\nE2E failed: ${error && error.stack ? error.stack : error}`);
  const failed = checks.filter((check) => !check.ok);
  console.log(`${checks.length - failed.length}/${checks.length} checks passed before the failure`);
  process.exit(1);
});
