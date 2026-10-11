"use strict";

// Locating and launching the installed DeepSeek Harness CLI.
//
// DSH ships `resources\runtime\cli\bin\dsh.cmd`, a launcher that runs the
// bundled `dsh-desktop-host` CLI entry with Electron in Node mode (see the
// .cmd contents). The bridge reproduces that exact command line instead of
// going through cmd.exe, so a delegated task never depends on shell quoting
// and never blocks the parent on a shell prompt.

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");

const APP_EXE_NAME = "DeepSeek Harness.exe";
const LAUNCHER_RELATIVE = path.join("resources", "runtime", "cli", "bin", "dsh.cmd");
const CLI_RELATIVE = path.join(
  "resources",
  "app.asar",
  "dsh",
  "node_modules",
  "@deepseek-ai",
  "dsh-desktop-host",
  "lib",
  "cli.js",
);

const isFile = (candidate) => {
  try {
    return fs.statSync(candidate).isFile();
  } catch {
    return false;
  }
};

/**
 * Whether a path inside an asar archive exists.
 *
 * The bundled CLI lives in `resources\app.asar`, which only Electron's patched
 * filesystem can read; plain Node.js must parse the archive header instead.
 * @returns {boolean|null} null when the archive cannot be inspected.
 */
function asarEntryExists(asarPath, innerPath) {
  let fd;
  try {
    fd = fs.openSync(asarPath, "r");
    const head = Buffer.alloc(16);
    fs.readSync(fd, head, 0, 16, 0);
    const headerSize = head.readUInt32LE(12);
    if (!Number.isInteger(headerSize) || headerSize <= 0 || headerSize > 64 * 1024 * 1024) return null;
    const header = Buffer.alloc(headerSize);
    fs.readSync(fd, header, 0, headerSize, 16);
    let node = JSON.parse(header.toString("utf8"));
    for (const part of innerPath.split(/[\\/]+/)) {
      if (!node.files || !node.files[part]) return false;
      node = node.files[part];
    }
    return true;
  } catch {
    return null;
  } finally {
    if (fd !== undefined) {
      try {
        fs.closeSync(fd);
      } catch {
        /* the handle is already gone */
      }
    }
  }
}

/** Split `<...>.asar\<inner path>`; returns null when the path is outside an asar. */
function splitAsarPath(target) {
  const match = /^(.*?\.asar)[\\/](.+)$/.exec(target);
  return match ? { asarPath: match[1], innerPath: match[2] } : null;
}

/**
 * Confirm that a candidate installation can actually be launched.
 * @returns {{launchable: boolean, cli_entry: string, note?: string}}
 */
function inspectInstallation(candidate) {
  if (!isFile(candidate.exe)) return { launchable: false, cli_entry: "missing", note: `${candidate.exe} not found` };
  if (isFile(candidate.cliJs)) return { launchable: true, cli_entry: "file" };
  const asar = splitAsarPath(candidate.cliJs);
  if (!asar) return { launchable: false, cli_entry: "missing", note: `${candidate.cliJs} not found` };
  const present = asarEntryExists(asar.asarPath, asar.innerPath);
  if (present === true) return { launchable: true, cli_entry: "inside-asar" };
  if (present === null) {
    return { launchable: true, cli_entry: "unverified", note: `cannot inspect ${asar.asarPath}` };
  }
  return { launchable: false, cli_entry: "missing", note: `${asar.innerPath} not found inside ${asar.asarPath}` };
}

/** Default installation roots shipped by the Windows installer. */
function defaultRoots(env) {
  return [
    env.DSH_BRIDGE_ROOT,
    env.LOCALAPPDATA && path.join(env.LOCALAPPDATA, "Programs", "DeepSeek Harness"),
    env.ProgramFiles && path.join(env.ProgramFiles, "DeepSeek Harness"),
    env["ProgramFiles(x86)"] && path.join(env["ProgramFiles(x86)"], "DeepSeek Harness"),
  ].filter(Boolean);
}

/** Every `dsh.cmd` reachable through PATH. */
function pathLaunchers(env) {
  const found = [];
  for (const entry of String(env.PATH || env.Path || "").split(path.delimiter)) {
    if (!entry) continue;
    const candidate = path.join(entry.replace(/^"|"$/g, ""), "dsh.cmd");
    if (isFile(candidate)) found.push(candidate);
  }
  return found;
}

function describeRoot(root, source) {
  return {
    root,
    source,
    launcher: path.join(root, LAUNCHER_RELATIVE),
    exe: path.join(root, APP_EXE_NAME),
    cliJs: path.join(root, CLI_RELATIVE),
  };
}

/**
 * Resolve the DSH installation to launch.
 * @param env - environment to inspect; defaults to process.env.
 * @returns {{root, source, launcher, exe, cliJs}} or throws with every attempt listed.
 */
function resolveInstallation(env = process.env) {
  const attempts = [];

  const candidates = [];
  if (env.DSH_BRIDGE_LAUNCHER) {
    const launcher = path.resolve(env.DSH_BRIDGE_LAUNCHER);
    // <root>\resources\runtime\cli\bin\dsh.cmd
    candidates.push(describeRoot(path.resolve(path.dirname(launcher), "..", "..", "..", ".."), "DSH_BRIDGE_LAUNCHER"));
  }
  for (const launcher of pathLaunchers(env)) {
    candidates.push(describeRoot(path.resolve(path.dirname(launcher), "..", "..", "..", ".."), "PATH"));
  }
  for (const root of defaultRoots(env)) candidates.push(describeRoot(root, "default location"));

  for (const candidate of candidates) {
    const inspection = inspectInstallation(candidate);
    if (!inspection.launchable) {
      attempts.push(`${candidate.source}: ${inspection.note}`);
      continue;
    }
    return { ...candidate, cli_entry: inspection.cli_entry, cli_entry_note: inspection.note };
  }

  const error = new Error(
    `Cannot locate a launchable DeepSeek Harness installation. Tried:\n  ${attempts.join("\n  ")}\n` +
      "Set DSH_BRIDGE_ROOT to the folder that contains 'DeepSeek Harness.exe'.",
  );
  error.code = "ENOLAUNCHER";
  throw error;
}

/**
 * Arguments and environment for one headless run.
 *
 * The task travels on stdin: a review request can exceed the Windows command
 * line limit, and stdin avoids quoting rules entirely.
 */
function buildInvocation({ installation, profile, sessionId, permissionMode, task }) {
  const args = ["--expose-internals", installation.cliJs, "--profile", profile, "--json"];
  if (sessionId) args.push("--session-id", sessionId);

  const env = { ...process.env };
  // A harness session variable inherited from the interactive app must not leak
  // into the delegated run: the run is its own session in the headless profile.
  delete env.DSH_SESSION_ID;
  delete env.DSH_WEB_URL;
  delete env.DSH_SHELL;
  delete env.DSH_PROFILE;
  delete env.DSH_PROFILE_DIR;
  env.ELECTRON_RUN_AS_NODE = "1";
  env.DSH_PERMISSION_MODE = permissionMode;

  return { command: installation.exe, args, env, stdin: task };
}

/**
 * Start one headless DSH run and hand back the running child process.
 * @returns {ChildProcess} with piped stdout/stderr; the task is already written to stdin.
 */
function spawnRun({ installation, workspace, task, sessionId, profile, permissionMode }) {
  const invocation = buildInvocation({ installation, profile, sessionId, permissionMode, task });
  const child = spawn(invocation.command, invocation.args, {
    cwd: workspace,
    env: invocation.env,
    windowsHide: true,
    stdio: ["pipe", "pipe", "pipe"],
  });
  // A refused stdin (the run failed before reading its task) surfaces on stdout
  // and stderr as well; it must not crash the bridge.
  child.stdin.on("error", () => {});
  child.stdin.end(invocation.stdin, "utf8");
  return { child, invocation: { command: invocation.command, args: invocation.args } };
}

module.exports = {
  resolveInstallation,
  inspectInstallation,
  asarEntryExists,
  spawnRun,
  buildInvocation,
  APP_EXE_NAME,
  CLI_RELATIVE,
};
