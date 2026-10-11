"use strict";

// Reading the durable DSH session log.
//
// `%DSH_HOME%\sessions\<workspace-slug>\<session-id>\session.v4.jsonl.zstd` is
// the authoritative execution record of a delegated run: it carries the model
// route actually used (`request/header`, `request/context`), the sandbox and
// approval policy the run started under, and the terminal `turn/end` reason.
// The file is a concatenation of zstd frames, so it is decoded frame by frame
// and reading stops as soon as the facts are known.

const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const ZSTD_MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);
const DEFAULT_MAX_FRAMES = 96;
const DEFAULT_MAX_BYTES = 48 * 1024 * 1024;

function zstdAvailable() {
  return typeof zlib.zstdDecompressSync === "function";
}

/** Locate the session directory of one session id under the harness home. */
function findSessionRecord(dshHome, sessionId) {
  if (!dshHome || !sessionId) return null;
  const root = path.join(dshHome, "sessions");
  let workspaces;
  try {
    workspaces = fs.readdirSync(root, { withFileTypes: true });
  } catch {
    return null;
  }
  for (const workspace of workspaces) {
    if (!workspace.isDirectory()) continue;
    const dir = path.join(root, workspace.name, sessionId);
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    const file = entries.find((entry) => entry.isFile() && /^session\.v\d+\.jsonl(\.zstd)?$/.test(entry.name));
    if (!file) continue;
    const full = path.join(dir, file.name);
    const stats = fs.statSync(full);
    return { dir, file: full, sizeBytes: stats.size, modifiedAt: stats.mtimeMs };
  }
  return null;
}

function* frames(buffer) {
  let offset = 0;
  while (offset < buffer.length) {
    const next = buffer.indexOf(ZSTD_MAGIC, offset + ZSTD_MAGIC.length);
    const end = next === -1 ? buffer.length : next;
    yield buffer.subarray(offset, end);
    offset = end;
  }
}

function decodeFrame(frame, compressed) {
  const buffer = Buffer.isBuffer(frame) ? frame : Buffer.from(frame);
  if (!compressed) return buffer.toString("utf8");
  return zlib.zstdDecompressSync(buffer).toString("utf8");
}

/**
 * Extract the facts a reviewer needs from one session log.
 * @returns {object} always resolves: an unreadable or compressed-in-an-unsupported
 *   way log reports `unavailable` instead of failing the caller.
 */
function readSessionFacts(
  recordPath,
  { maxFrames = DEFAULT_MAX_FRAMES, maxBytes = DEFAULT_MAX_BYTES, sessionId = null } = {},
) {
  const facts = {
    record_path: recordPath,
    session_id: sessionId,
    readable: false,
    truncated_scan: false,
    record_counts: {},
    provider: null,
    model: null,
    reasoning_effort: null,
    max_tokens: null,
    context_window: null,
    permission_preset: null,
    sandbox_mode: null,
    approval_policy: null,
    turn_end_reason: null,
    title: null,
    unavailable: null,
  };

  let buffer;
  try {
    buffer = fs.readFileSync(recordPath);
  } catch (error) {
    facts.unavailable = `cannot read session log: ${error.message}`;
    return facts;
  }

  const compressed = recordPath.endsWith(".zstd");
  if (compressed && !zstdAvailable()) {
    facts.unavailable = "this Node.js lacks zlib.zstdDecompressSync; run the bridge on Node.js 22.15+ to read session logs";
    return facts;
  }
  if (buffer.length > maxBytes) {
    facts.truncated_scan = true;
    buffer = buffer.subarray(0, maxBytes);
  }

  const increment = (key) => {
    facts.record_counts[key] = (facts.record_counts[key] || 0) + 1;
  };

  let frameCount = 0;
  for (const frame of frames(buffer)) {
    if (frameCount >= maxFrames) {
      facts.truncated_scan = true;
      break;
    }
    frameCount += 1;
    let text;
    try {
      text = decodeFrame(frame, compressed);
    } catch (error) {
      facts.unavailable = facts.unavailable || `undecodable frame: ${error.message}`;
      continue;
    }
    facts.readable = true;
    for (const line of text.split("\n")) {
      if (line === "") continue;
      let record;
      try {
        record = JSON.parse(line);
      } catch {
        continue;
      }
      increment(record.type || "unknown");
      const data = record.data || {};
      if (record.type === "request/header" && data.header && data.header.config) {
        const config = data.header.config;
        facts.provider = config.provider ?? facts.provider;
        facts.model = config.model ?? facts.model;
        facts.reasoning_effort = config.reasoningEffort ?? facts.reasoning_effort;
        facts.max_tokens = config.maxTokens ?? facts.max_tokens;
      }
      if (record.type === "request/context") {
        facts.provider = data.provider ?? facts.provider;
        facts.model = data.model ?? facts.model;
        facts.context_window = data.contextWindow ?? facts.context_window;
      }
      if (record.type === "permission/preset") facts.permission_preset = data.preset ?? data.name ?? facts.permission_preset;
      if (record.type === "sandbox/mode") facts.sandbox_mode = data.mode ?? facts.sandbox_mode;
      if (record.type === "approval/policy") facts.approval_policy = data.policy ?? facts.approval_policy;
      if (record.type === "session/title") facts.title = data.title ?? facts.title;
      if (record.type === "turn/end") facts.turn_end_reason = data.reason ?? facts.turn_end_reason;
    }
    if (facts.provider && facts.turn_end_reason) break;
  }

  facts.frames_scanned = frameCount;
  return facts;
}

/** Session log facts for one (dshHome, sessionId) pair, plus where the log lives. */
function describeSession(dshHome, sessionId, options) {
  const record = findSessionRecord(dshHome, sessionId);
  if (!record) {
    return { session_id: sessionId, record_path: null, record_found: false, note: "no session log found yet" };
  }
  const facts = readSessionFacts(record.file, { ...options, sessionId });
  return { session_id: sessionId, record_found: true, ...facts, ...record };
}

module.exports = { findSessionRecord, readSessionFacts, describeSession, zstdAvailable, ZSTD_MAGIC };
