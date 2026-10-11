"use strict";

// Fast, deterministic checks for the bridge's own logic: the MCP surface, the
// launcher command line, the session-log reader and the configuration defaults.
// No model call and no DSH run happens here; test/e2e.js covers those.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const zlib = require("node:zlib");

const { createServer } = require("../lib/mcp");
const { resolveInstallation, buildInvocation } = require("../lib/launcher");
const { readSessionFacts, describeSession } = require("../lib/session-log");
const { tasklistReportsPid } = require("../lib/runner");
const { loadConfig, readConfiguredModel } = require("../server");

const results = [];
async function check(name, run) {
  try {
    await run();
    results.push({ name, ok: true });
    console.log(`PASS ${name}`);
  } catch (error) {
    results.push({ name, ok: false, error: error.message });
    console.log(`FAIL ${name}: ${error.message}`);
  }
}

/** Capture protocol frames written to stdout while `run` executes. */
async function captureFrames(run) {
  const frames = [];
  const original = process.stdout.write;
  process.stdout.write = (chunk) => {
    frames.push(String(chunk));
    return true;
  };
  try {
    await run();
  } finally {
    process.stdout.write = original;
  }
  return frames
    .join("")
    .split("\n")
    .filter((line) => line.trim() !== "")
    .map((line) => JSON.parse(line));
}

async function main() {
  await check("mcp: initialize answers with a supported protocol version and tools capability", async () => {
    const tools = [{ name: "noop", description: "does nothing", inputSchema: { type: "object" }, handler: async () => ({ ok: true }) }];
    const server = createServer({ name: "test", version: "0", tools });
    const frames = await captureFrames(async () => {
      await server.accept(
        JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18" } }),
      );
    });
    assert.equal(frames.length, 1);
    assert.equal(frames[0].result.protocolVersion, "2025-06-18");
    assert.deepEqual(frames[0].result.capabilities.tools, { listChanged: false });
  });

  await check("mcp: an unknown protocol version falls back to the newest supported one", async () => {
    const server = createServer({ name: "test", version: "0", tools: [] });
    const frames = await captureFrames(async () => {
      await server.accept(JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "1999-01-01" } }));
    });
    assert.equal(frames[0].result.protocolVersion, "2025-06-18");
  });

  await check("mcp: tools/call reports a throwing tool as an error result, not a crash", async () => {
    const tools = [
      {
        name: "boom",
        description: "always fails",
        inputSchema: { type: "object" },
        handler: async () => {
          throw new Error("nope");
        },
      },
    ];
    const server = createServer({ name: "test", version: "0", tools });
    const frames = await captureFrames(async () => {
      await server.accept(JSON.stringify({ jsonrpc: "2.0", id: 7, method: "tools/call", params: { name: "boom", arguments: {} } }));
    });
    assert.equal(frames[0].result.isError, true);
    assert.match(frames[0].result.content[0].text, /nope/);
  });

  await check("mcp: unknown methods and tools answer JSON-RPC errors", async () => {
    const server = createServer({ name: "test", version: "0", tools: [] });
    const frames = await captureFrames(async () => {
      await server.accept(JSON.stringify({ jsonrpc: "2.0", id: 1, method: "does/not/exist" }));
      await server.accept(JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "ghost" } }));
    });
    assert.equal(frames[0].error.code, -32601);
    assert.equal(frames[1].error.code, -32602);
  });

  await check("mcp: notifications are not answered", async () => {
    const server = createServer({ name: "test", version: "0", tools: [] });
    const frames = await captureFrames(async () => {
      await server.accept(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized", params: {} }));
    });
    assert.equal(frames.length, 0);
  });

  await check("launcher: the installed DeepSeek Harness is found and the run command mirrors the shipped launcher", async () => {
    const installation = resolveInstallation(process.env);
    assert.ok(fs.existsSync(installation.exe), `missing ${installation.exe}`);
    assert.ok(
      ["file", "inside-asar", "unverified"].includes(installation.cli_entry),
      `cli entry not launchable: ${installation.cli_entry} ${installation.cli_entry_note || ""}`,
    );
    const invocation = buildInvocation({
      installation,
      profile: "headless",
      sessionId: "session-example",
      permissionMode: "workspace-write",
      task: "do the thing",
    });
    assert.deepEqual(invocation.args.slice(2), ["--profile", "headless", "--json", "--session-id", "session-example"]);
    assert.equal(invocation.env.ELECTRON_RUN_AS_NODE, "1");
    assert.equal(invocation.env.DSH_PERMISSION_MODE, "workspace-write");
    assert.equal(invocation.env.DSH_SESSION_ID, undefined);
    assert.equal(invocation.env.DSH_PROFILE, undefined);
    assert.equal(invocation.stdin, "do the thing");
  });

  await check("session log: a multi-frame zstd log yields the route, policies and turn/end reason", async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dsh-bridge-log-"));
    const file = path.join(dir, "session.v4.jsonl.zstd");
    const records = [
      { type: "session", data: { cwd: "C:\\work" } },
      { type: "sandbox/mode", data: { mode: "workspace-write" } },
      { type: "approval/policy", data: { policy: "ask" } },
      { type: "request/header", data: { header: { config: { provider: "opencode-go", model: "deepseek-v4.1-flash", reasoningEffort: "max", maxTokens: 384000 } } } },
      { type: "request/context", data: { provider: "opencode-go", model: "deepseek-v4.1-flash", contextWindow: 1000000 } },
      { type: "turn/end", data: { reason: { kind: "completed" } } },
    ];
    // One zstd frame per record: the real log is written incrementally.
    const frames = records.map((record) => zlib.zstdCompressSync(Buffer.from(`${JSON.stringify(record)}\n`, "utf8")));
    fs.writeFileSync(file, Buffer.concat(frames));

    const facts = readSessionFacts(file);
    assert.equal(facts.readable, true);
    assert.equal(facts.provider, "opencode-go");
    assert.equal(facts.model, "deepseek-v4.1-flash");
    assert.equal(facts.reasoning_effort, "max");
    assert.equal(facts.context_window, 1000000);
    assert.equal(facts.sandbox_mode, "workspace-write");
    assert.equal(facts.approval_policy, "ask");
    assert.deepEqual(facts.turn_end_reason, { kind: "completed" });
    assert.equal(facts.record_counts["request/header"], 1);
    fs.rmSync(dir, { recursive: true, force: true });
  });

  await check("session log: a missing session reports absence instead of throwing", async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dsh-bridge-home-"));
    const described = describeSession(dir, "session-does-not-exist");
    assert.equal(described.record_found, false);
    assert.equal(described.record_path, null);
    const unreadable = readSessionFacts(path.join(dir, "nope.zstd"));
    assert.equal(unreadable.readable, false);
    assert.match(unreadable.unavailable, /cannot read session log/);
    fs.rmSync(dir, { recursive: true, force: true });
  });

  await check("cancel: a still-running pid is found in tasklist CSV output while localized text is not misread", async () => {
    const csv = [
      '"node.exe","90120","Console","1","256,772 K"',
      '"node.exe","71524","Console","1","12,345 K"',
    ].join("\r\n");
    assert.equal(tasklistReportsPid(csv, 90120), true);
    assert.equal(tasklistReportsPid(csv, 4242), false);
    // A killed run's tasklist answers with a localized "no tasks" sentence.
    const localizedEmpty = "情報: 指定された条件に一致するタスクは実行されていません。\r\n";
    assert.equal(tasklistReportsPid(localizedEmpty, 90120), false);
  });

  await check("config: environment overrides win over defaults, and the profile patch yields the configured model", async () => {
    const config = loadConfig({ LOCALAPPDATA: "C:\\Users\\x\\AppData\\Local" });
    assert.equal(config.profile, "headless");
    assert.equal(config.defaultPermissionMode, "workspace-write");
    assert.equal(config.maxConcurrentRuns, 1);
    assert.equal(config.home, path.join("C:\\Users\\x\\AppData\\Local", "claude-dsh-bridge"));

    const overridden = loadConfig({
      DSH_BRIDGE_HOME: "D:\\bridge",
      DSH_BRIDGE_PROFILE: "headless-alt",
      DSH_BRIDGE_MAX_RUNS: "3",
      DSH_HOME: "D:\\dsh",
    });
    assert.equal(overridden.home, "D:\\bridge");
    assert.equal(overridden.profile, "headless-alt");
    assert.equal(overridden.maxConcurrentRuns, 3);
    assert.equal(overridden.profileDir, path.join("D:\\dsh", "profiles", "headless-alt"));

    const configured = readConfiguredModel(path.join(__dirname, "..", "profile"));
    assert.equal(configured.configured_provider, "opencode-go");
    assert.equal(configured.configured_model, "deepseek-v4.1-flash");
    assert.equal(configured.configured_reasoning_effort, "max");
  });

  const failed = results.filter((result) => !result.ok);
  console.log(`\n${results.length - failed.length}/${results.length} checks passed`);
  process.exit(failed.length === 0 ? 0 : 1);
}

main().catch((error) => {
  console.error(`unit checks crashed: ${error.stack}`);
  process.exit(1);
});
