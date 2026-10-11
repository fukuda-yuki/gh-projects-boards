"use strict";

// Minimal MCP client used by the bridge's own tests: spawns a stdio MCP server,
// performs the handshake, and calls tools with the same framing Claude Code uses.

const { spawn } = require("node:child_process");

function createClient({ command, args, env, cwd, onStderr = () => {} }) {
  let child;
  try {
    child = spawn(command, args, {
      cwd,
      env: env || process.env,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
  } catch (error) {
    // Windows sandboxes deny the named pipes a piped stdio spawn needs; say so
    // instead of surfacing a bare EPERM.
    throw new Error(
      `cannot start the MCP server (${command}): ${error.message}. ` +
        "A sandboxed or restricted shell cannot create the pipes this needs; run the check outside it.",
    );
  }

  let buffer = "";
  const pending = new Map();
  let nextId = 1;

  child.stdout.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let index = buffer.indexOf("\n");
    while (index !== -1) {
      const line = buffer.slice(0, index).trim();
      buffer = buffer.slice(index + 1);
      if (line !== "") {
        let message;
        try {
          message = JSON.parse(line);
        } catch {
          onStderr(`unparsable server frame: ${line.slice(0, 200)}`);
          index = buffer.indexOf("\n");
          continue;
        }
        if (message.id !== undefined && pending.has(message.id)) {
          const { resolve, reject } = pending.get(message.id);
          pending.delete(message.id);
          if (message.error) reject(new Error(`${message.error.code}: ${message.error.message}`));
          else resolve(message.result);
        }
      }
      index = buffer.indexOf("\n");
    }
  });
  child.stderr.on("data", (chunk) => onStderr(chunk.toString("utf8")));

  const send = (message) => child.stdin.write(`${JSON.stringify(message)}\n`);
  const notify = (method, params) => send({ jsonrpc: "2.0", method, params });

  const request = (method, params) =>
    new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      send({ jsonrpc: "2.0", id, method, params });
      setTimeout(() => {
        if (pending.has(id)) {
          pending.delete(id);
          reject(new Error(`timeout waiting for ${method}`));
        }
      }, 15 * 60 * 1000).unref?.();
    });

  return {
    child,
    request,
    notify,
    /** initialize + notifications/initialized, as an MCP client does. */
    async handshake({ protocolVersion = "2025-06-18" } = {}) {
      const result = await request("initialize", {
        protocolVersion,
        capabilities: {},
        clientInfo: { name: "claude-dsh-bridge-test", version: "0.1.0" },
      });
      notify("notifications/initialized", {});
      return result;
    },
    async callTool(name, args = {}) {
      const result = await request("tools/call", { name, arguments: args });
      const text = (result.content || []).map((part) => part.text || "").join("");
      let parsed = null;
      try {
        parsed = JSON.parse(text);
      } catch {
        parsed = null;
      }
      return { isError: Boolean(result.isError), result, parsed, text };
    },
    close() {
      try {
        child.stdin.end();
      } catch {
        /* the child may already be gone */
      }
    },
  };
}

module.exports = { createClient };
