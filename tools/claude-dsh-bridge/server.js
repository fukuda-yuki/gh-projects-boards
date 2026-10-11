#!/usr/bin/env node
"use strict";

// Claude Code ⇄ DeepSeek Harness bridge (MCP, stdio).
//
// Claude Code stays the orchestrator: it plans, splits work, reviews results and
// decides when work is done. This server hands one implementation task at a time
// to a DSH agent run — the shipped `headless` profile driving DeepSeek V4.1
// Flash over OpenCode Go — and reports the run's state, its report, its end
// reason and where its execution record lives.
//
// The delegation is one-way: DSH never calls Claude Code back. It reuses DSH's
// own launcher, agent loop, sandboxed tools and session store rather than
// reimplementing agent behaviour here.

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const readline = require("node:readline");
const { spawn } = require("node:child_process");

const { createServer } = require("./lib/mcp");
const { resolveInstallation } = require("./lib/launcher");
const { RunManager } = require("./lib/runner");
const { describeSession, zstdAvailable } = require("./lib/session-log");

const SERVER_NAME = "dsh-flash-bridge";
const SERVER_VERSION = "0.1.0";
const PROFILE_PATCH_FILE = "cordis.patch.yml";

/** Server configuration, taken from the environment Claude Code launches it with. */
function loadConfig(env = process.env) {
  const homeDefault = env.LOCALAPPDATA
    ? path.join(env.LOCALAPPDATA, "claude-dsh-bridge")
    : path.join(os.homedir(), ".claude-dsh-bridge");
  const dshHome = env.DSH_HOME || path.join(os.homedir(), ".dsh");
  const profile = env.DSH_BRIDGE_PROFILE || "headless";
  return {
    home: env.DSH_BRIDGE_HOME || homeDefault,
    dshHome,
    profile,
    profileDir: path.join(dshHome, "profiles", profile),
    defaultPermissionMode: env.DSH_BRIDGE_PERMISSION_MODE || "workspace-write",
    maxConcurrentRuns: Number.parseInt(env.DSH_BRIDGE_MAX_RUNS || "1", 10) || 1,
    defaultTimeoutSeconds: Number.parseInt(env.DSH_BRIDGE_DEFAULT_TIMEOUT_SECONDS || "0", 10) || 0,
  };
}

/** The provider/model the profile is configured to use, read from its patch layer. */
function readConfiguredModel(profileDir) {
  const patchPath = path.join(profileDir, PROFILE_PATCH_FILE);
  const result = { profile_dir: profileDir, patch_path: patchPath, patch_exists: fs.existsSync(patchPath) };
  if (!result.patch_exists) return result;
  let provider = null;
  let model = null;
  let reasoningEffort = null;
  let insideDefaultModel = false;
  for (const rawLine of fs.readFileSync(patchPath, "utf8").split(/\r?\n/)) {
    const line = rawLine.trim();
    if (/^-?\s*id:\s*agent-default-model\b/.test(line)) {
      insideDefaultModel = true;
      continue;
    }
    if (insideDefaultModel && /^-?\s*id:/.test(line)) insideDefaultModel = false;
    if (!insideDefaultModel) continue;
    const providerMatch = /^provider:\s*(\S+)/.exec(line);
    const modelMatch = /^model:\s*(\S+)/.exec(line);
    const effortMatch = /^reasoningEffort:\s*(\S+)/.exec(line);
    if (providerMatch) provider = providerMatch[1];
    if (modelMatch) model = modelMatch[1];
    if (effortMatch) reasoningEffort = effortMatch[1];
  }
  return { ...result, configured_provider: provider, configured_model: model, configured_reasoning_effort: reasoningEffort };
}

/** Cached `dsh --version`; the CLI has no version file to read. */
function createVersionProbe(installation, log) {
  let cached = null;
  return async () => {
    if (cached) return cached;
    cached = await new Promise((resolve) => {
      let child;
      try {
        child = spawn(installation.exe, ["--expose-internals", installation.cliJs, "--version"], {
          windowsHide: true,
          stdio: ["ignore", "pipe", "pipe"],
        });
      } catch (error) {
        resolve({ version: null, error: error.message });
        return;
      }
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", (chunk) => {
        stdout += chunk.toString("utf8");
      });
      child.stderr.on("data", (chunk) => {
        stderr += chunk.toString("utf8");
      });
      child.on("error", (error) => resolve({ version: null, error: error.message }));
      child.on("close", (code) => {
        const version = stdout.trim().split(/\r?\n/)[0] || null;
        if (code !== 0) log(`dsh --version exited ${code}: ${stderr.trim()}`);
        resolve({ version, exit_code: code, stderr: stderr.trim() || undefined });
      });
    });
    return cached;
  };
}

function createBridge(env = process.env, log = (message) => process.stderr.write(`[${SERVER_NAME}] ${message}\n`)) {
  const config = loadConfig(env);
  const installation = resolveInstallation(env);
  fs.mkdirSync(config.home, { recursive: true });
  const manager = new RunManager({
    home: config.home,
    installation,
    dshHome: config.dshHome,
    profile: config.profile,
    defaultPermissionMode: config.defaultPermissionMode,
    maxConcurrentRuns: config.maxConcurrentRuns,
    log,
  });
  const versionProbe = createVersionProbe(installation, log);

  /** Route facts for a run, read from DSH's own session log. */
  async function routeFor(runId) {
    if (!runId) return null;
    const run = manager.get(runId);
    if (!run.session_id) return { available: false, note: "the run has not reported a session id yet" };
    const facts = await manager.attachSessionRecord(run);
    if (!facts || !facts.record_found) return { available: false, session_id: run.session_id, note: "no session log found yet" };
    return {
      available: Boolean(facts.provider && facts.model),
      session_id: run.session_id,
      provider: facts.provider,
      model: facts.model,
      reasoning_effort: facts.reasoning_effort,
      context_window: facts.context_window,
      max_tokens: facts.max_tokens,
      sandbox_mode: facts.sandbox_mode,
      approval_policy: facts.approval_policy,
      permission_preset: facts.permission_preset,
      turn_end_reason: facts.turn_end_reason,
      record_path: facts.record_path,
      record_found: true,
      source: "DSH session log (request/header, request/context)",
    };
  }

  async function delegate(args) {
    const workspace = args.workspace ? path.resolve(args.workspace) : process.cwd();
    const sessionId = args.session_id || null;
    const run = manager.start({
      mode: sessionId ? "continue" : "new",
      workspace,
      task: args.task,
      sessionId,
      permissionMode: args.permission_mode,
      timeoutSeconds: Number.isFinite(args.timeout_seconds) ? args.timeout_seconds : config.defaultTimeoutSeconds,
    });
    const settled = args.wait_seconds > 0 ? manager.status((await manager.waitFor(run.id, args.wait_seconds)).id) : manager.status(run.id);
    return {
      run_id: settled.run_id,
      status: settled.status,
      session_mode: sessionId ? "continued" : "new",
      session_id: settled.session_id,
      adopted_session_id: settled.adopted_session_id,
      workspace,
      profile: config.profile,
      permission_mode: settled.permission_mode,
      next_step: settled.status === "running" ? "poll dsh_status, then read dsh_result" : "read dsh_result",
      records: settled.records,
    };
  }

  async function status(args) {
    const run = args.wait_seconds > 0 ? await manager.waitFor(args.run_id, args.wait_seconds) : manager.get(args.run_id);
    const view = manager.status(run.id);
    return { ...view, session_route: await routeFor(run.id) };
  }

  async function result(args) {
    const run = manager.get(args.run_id);
    if (run.session_id) await manager.attachSessionRecord(run);
    const view = manager.result(args.run_id, { recentEvents: args.recent_events || 0 });
    const route = await routeFor(args.run_id);
    return {
      ...view,
      profile: { ...view.profile, ...readConfiguredModel(config.profileDir) },
      model_route: route,
    };
  }

  async function rework(args) {
    const mode = args.mode || "continue";
    const previous = args.run_id ? manager.get(args.run_id) : null;
    if (previous && previous.status === "running") {
      const error = new Error(
        `run ${previous.id} is still running; wait for it to finish or cancel it before sending review feedback`,
      );
      error.code = "EBUSY";
      throw error;
    }

    if (mode === "new_run") {
      const workspace = args.workspace ? path.resolve(args.workspace) : previous ? previous.workspace : process.cwd();
      const task = args.task || args.feedback;
      const run = manager.start({
        mode: "new",
        workspace,
        task,
        permissionMode: args.permission_mode,
        timeoutSeconds: Number.isFinite(args.timeout_seconds) ? args.timeout_seconds : config.defaultTimeoutSeconds,
        parentRunId: previous ? previous.id : null,
      });
      const settled = args.wait_seconds > 0 ? manager.status((await manager.waitFor(run.id, args.wait_seconds)).id) : manager.status(run.id);
      return {
        run_id: settled.run_id,
        status: settled.status,
        same_session: false,
        session_mode: "new",
        session_id: settled.session_id,
        previous_run_id: previous ? previous.id : null,
        previous_session_id: previous ? previous.session_id : null,
        note:
          "This run starts a NEW session with no history from the previous run; the feedback is the whole task text.",
        workspace,
        records: settled.records,
      };
    }

    const sessionId = args.session_id || (previous && previous.session_id) || null;
    if (!sessionId) {
      const error = new Error(
        "continuing needs a session: pass run_id of a finished run, or session_id. " +
          "Use mode 'new_run' when the review should start a fresh session instead.",
      );
      error.code = "ENOSESSION";
      throw error;
    }
    const workspace = args.workspace ? path.resolve(args.workspace) : previous ? previous.workspace : process.cwd();
    if (previous && path.resolve(workspace) !== previous.workspace) {
      const error = new Error(
        `DSH only adopts a session in the working directory it recorded (${previous.workspace}); ` +
          `use mode 'new_run' for a different folder.`,
      );
      error.code = "EWORKSPACE";
      throw error;
    }
    const run = manager.start({
      mode: "continue",
      workspace,
      task: args.feedback,
      sessionId,
      permissionMode: args.permission_mode,
      timeoutSeconds: Number.isFinite(args.timeout_seconds) ? args.timeout_seconds : config.defaultTimeoutSeconds,
      parentRunId: previous ? previous.id : null,
    });
    const settled = args.wait_seconds > 0 ? manager.status((await manager.waitFor(run.id, args.wait_seconds)).id) : manager.status(run.id);
    return {
      run_id: settled.run_id,
      status: settled.status,
      same_session: true,
      session_mode: "continued",
      session_id: settled.session_id,
      adopted_session_id: sessionId,
      previous_run_id: previous ? previous.id : null,
      note: `This run continues session ${sessionId}: the feedback is delivered into the existing conversation.`,
      workspace,
      records: settled.records,
    };
  }

  async function cancel(args) {
    return manager.cancel(args.run_id, { graceSeconds: Number.isFinite(args.grace_seconds) ? args.grace_seconds : 20 });
  }

  async function info(args) {
    const version = await versionProbe();
    const profile = readConfiguredModel(config.profileDir);
    const active = manager.activeRuns().map((run) => run.id);
    const credential = (name) => (env[name] && String(env[name]).trim() !== "" ? "set" : "missing");
    return {
      bridge: {
        home: config.home,
        node: process.version,
        zstd_session_log_support: zstdAvailable(),
        max_concurrent_runs: config.maxConcurrentRuns,
        default_permission_mode: config.defaultPermissionMode,
      },
      dsh: {
        version: version.version,
        version_probe_error: version.error,
        home: config.dshHome,
        installation_root: installation.root,
        installation_source: installation.source,
        executable: installation.exe,
        cli_entry: installation.cliJs,
        launcher: installation.launcher,
      },
      profile: {
        name: config.profile,
        dir: profile.profile_dir,
        patch_path: profile.patch_path,
        configured_provider: profile.configured_provider,
        configured_model: profile.configured_model,
        configured_reasoning_effort: profile.configured_reasoning_effort,
      },
      credentials: {
        OPENCODE_API_KEY: credential("OPENCODE_API_KEY"),
        DEEPSEEK_API_KEY: credential("DEEPSEEK_API_KEY"),
        note: "presence only; the bridge never reads or reports credential values",
      },
      sessions: {
        store: path.join(config.dshHome, "sessions"),
        runs_home: config.home,
      },
      runs: { active, recent: manager.list({ limit: 10 }) },
      observed_route: await routeFor(args && args.run_id ? args.run_id : (manager.list({ limit: 1 })[0] || {}).run_id),
    };
  }

  const tools = [
    {
      name: "dsh_delegate",
      title: "Delegate an implementation task to a DSH agent",
      description:
        "Start one DeepSeek Harness agent run (DeepSeek V4.1 Flash through OpenCode Go) that investigates, edits and " +
        "verifies code in `workspace`. Returns immediately with a run_id; poll dsh_status and read dsh_result. " +
        "One child runs at a time. Pass session_id to continue an existing session instead of starting a new one.",
      inputSchema: {
        type: "object",
        properties: {
          task: { type: "string", description: "The task text handed to the DSH agent as its user message." },
          workspace: {
            type: "string",
            description: "Absolute working folder for the run; defaults to the Claude Code project directory.",
          },
          session_id: {
            type: "string",
            description: "Adopt this existing DSH session (e.g. session-<uuid>) instead of starting a new one.",
          },
          permission_mode: {
            type: "string",
            enum: ["read-only", "workspace-write", "danger-full-access"],
            description: "Sandbox mode for the run; defaults to workspace-write rooted at `workspace`.",
          },
          timeout_seconds: { type: "integer", description: "Cancel the run after this many seconds; 0 means no timeout." },
          wait_seconds: { type: "integer", description: "Block up to this many seconds for the run to finish before returning." },
        },
        required: ["task"],
        additionalProperties: false,
      },
      handler: delegate,
    },
    {
      name: "dsh_status",
      title: "Read the state of a delegated run",
      description:
        "Report run state, elapsed time, exit code, turn/end reason, session id, event counters and the record paths " +
        "of a delegated run. Use wait_seconds to block until the run leaves 'running'.",
      inputSchema: {
        type: "object",
        properties: {
          run_id: { type: "string", description: "Run id returned by dsh_delegate or dsh_rework." },
          wait_seconds: { type: "integer", description: "Block up to this many seconds for the run to finish; 0 returns at once." },
        },
        required: ["run_id"],
        additionalProperties: false,
      },
      handler: status,
    },
    {
      name: "dsh_result",
      title: "Read the report and outcome of a delegated run",
      description:
        "Return the child model's final report separately from the process outcome (exit code and turn/end reason), " +
        "with the session record path and the provider/model actually used.",
      inputSchema: {
        type: "object",
        properties: {
          run_id: { type: "string", description: "Run id returned by dsh_delegate or dsh_rework." },
          recent_events: {
            type: "integer",
            description: "Attach this many recent run events (tool calls, messages) as review evidence; 0 omits them.",
          },
        },
        required: ["run_id"],
        additionalProperties: false,
      },
      handler: result,
    },
    {
      name: "dsh_rework",
      title: "Send review feedback back to the DSH agent",
      description:
        "Deliver review feedback as a new request. mode 'continue' (default) adopts the previous session so the agent " +
        "keeps its history; mode 'new_run' starts a fresh session in which the feedback alone is the task. The reply " +
        "states which of the two happened.",
      inputSchema: {
        type: "object",
        properties: {
          feedback: { type: "string", description: "The review findings or correction request to deliver." },
          mode: {
            type: "string",
            enum: ["continue", "new_run"],
            description: "continue adopts the previous session; new_run starts a new session.",
          },
          run_id: { type: "string", description: "The finished run whose session should be continued." },
          session_id: { type: "string", description: "Session to continue when no run_id is available." },
          workspace: { type: "string", description: "Working folder; must match the previous run's folder when continuing." },
          task: { type: "string", description: "Task text for mode 'new_run'; defaults to the feedback text." },
          permission_mode: {
            type: "string",
            enum: ["read-only", "workspace-write", "danger-full-access"],
            description: "Sandbox mode for the new run.",
          },
          timeout_seconds: { type: "integer", description: "Cancel the run after this many seconds; 0 means no timeout." },
          wait_seconds: { type: "integer", description: "Block up to this many seconds for the run to finish before returning." },
        },
        required: ["feedback"],
        additionalProperties: false,
      },
      handler: rework,
    },
    {
      name: "dsh_cancel",
      title: "Cancel a delegated run and verify it stopped",
      description:
        "Terminate the run's process tree, then verify independently that the process is gone (taskkill plus tasklist " +
        "and an exit probe). Returns the verification evidence; verified_stopped is false if any check still sees it.",
      inputSchema: {
        type: "object",
        properties: {
          run_id: { type: "string", description: "Run id to cancel." },
          grace_seconds: { type: "integer", description: "Seconds to wait for the process to exit before re-probing." },
        },
        required: ["run_id"],
        additionalProperties: false,
      },
      handler: cancel,
    },
    {
      name: "dsh_info",
      title: "Report the DSH environment and record locations",
      description:
        "Report the installed DSH version, installation paths, profile and configured model, credential presence, " +
        "run record locations and the provider/model observed in the session log of a run.",
      inputSchema: {
        type: "object",
        properties: {
          run_id: { type: "string", description: "Optional run whose observed provider/model should be reported." },
        },
        additionalProperties: false,
      },
      handler: info,
    },
  ];

  return { config, installation, manager, tools, routeFor };
}

const INSTRUCTIONS = [
  "This server delegates implementation work from Claude Code to a DeepSeek Harness agent running DeepSeek V4.1 Flash.",
  "Flow: dsh_delegate (one task, one workspace) -> dsh_status until it is not 'running' -> dsh_result -> review the diff",
  "and evidence yourself -> dsh_rework with concrete findings (mode 'continue' keeps the agent's session, 'new_run' starts over).",
  "One child runs at a time: finish or dsh_cancel the active run before starting another.",
  "dsh_result keeps the child's own report apart from the process outcome; only you can decide that the work is done.",
].join(" ");

function main() {
  const log = (message) => process.stderr.write(`[${SERVER_NAME}] ${message}\n`);
  const bridge = createBridge(process.env, log);
  const server = createServer({
    name: SERVER_NAME,
    version: SERVER_VERSION,
    instructions: INSTRUCTIONS,
    tools: bridge.tools,
    log,
  });

  const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  rl.on("line", (line) => server.accept(line));
  rl.on("close", () => {
    log("stdin closed; cancelling active runs");
    shutdown(0);
  });

  let shuttingDown = false;
  function shutdown(code) {
    if (shuttingDown) return;
    shuttingDown = true;
    const active = bridge.manager.activeRuns();
    Promise.all(active.map((run) => bridge.manager.cancel(run.id, { reason: "bridge shutdown", graceSeconds: 10 })))
      .catch(() => {})
      .finally(() => process.exit(code));
    if (active.length === 0) process.exit(code);
  }

  process.on("SIGINT", () => shutdown(0));
  process.on("SIGTERM", () => shutdown(0));
  process.on("uncaughtException", (error) => log(`uncaught exception: ${error && error.stack ? error.stack : error}`));
  process.on("unhandledRejection", (error) => log(`unhandled rejection: ${error}`));
  log(`ready (profile=${bridge.config.profile}, dsh=${bridge.installation.root})`);
}

if (require.main === module) main();

module.exports = { createBridge, loadConfig, readConfiguredModel };
