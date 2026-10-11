"use strict";

// Minimal MCP server over the stdio transport: newline-delimited JSON-RPC 2.0
// on stdin/stdout.
//
// Implemented here instead of depending on `@modelcontextprotocol/sdk` so the
// bridge installs on a Windows workstation with no package manager step and no
// third-party version to pin. Only the surface Claude Code needs is covered:
// initialize, tools/list, tools/call, ping, and notifications.

const SUPPORTED_PROTOCOL_VERSIONS = ["2025-06-18", "2025-03-26", "2024-11-05"];
const DEFAULT_PROTOCOL_VERSION = SUPPORTED_PROTOCOL_VERSIONS[0];

/**
 * Build a stdio MCP server.
 * @param name - serverInfo.name reported to the client.
 * @param version - serverInfo.version reported to the client.
 * @param instructions - optional usage guidance returned by initialize.
 * @param tools - tool descriptors: {name, title, description, inputSchema, handler}.
 * @param log - diagnostic sink; stdout stays reserved for protocol frames.
 */
function createServer({ name, version, instructions, tools, log = () => {} }) {
  const byName = new Map(tools.map((tool) => [tool.name, tool]));
  let initializeSeen = false;
  let chain = Promise.resolve();

  const write = (message) => {
    process.stdout.write(`${JSON.stringify(message)}\n`);
  };

  const reply = (id, result) => write({ jsonrpc: "2.0", id, result });
  const replyError = (id, code, message, data) =>
    write({ jsonrpc: "2.0", id, error: data === undefined ? { code, message } : { code, message, data } });

  const toolList = () =>
    tools.map(({ name: toolName, title, description, inputSchema }) => ({
      name: toolName,
      ...(title ? { title } : {}),
      description,
      inputSchema,
    }));

  async function dispatch(message) {
    const { id, method, params } = message;
    const isNotification = id === undefined || id === null;

    if (method === "initialize") {
      initializeSeen = true;
      const requested = params && params.protocolVersion;
      const protocolVersion = SUPPORTED_PROTOCOL_VERSIONS.includes(requested) ? requested : DEFAULT_PROTOCOL_VERSION;
      reply(id, {
        protocolVersion,
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name, version },
        ...(instructions ? { instructions } : {}),
      });
      return;
    }

    // Notifications carry no reply; `initialized` and `cancelled` need no action.
    if (isNotification) {
      if (method !== "notifications/initialized" && method !== "notifications/cancelled") {
        log(`ignored notification: ${method}`);
      }
      return;
    }

    if (method === "ping") {
      reply(id, {});
      return;
    }

    if (method === "tools/list") {
      reply(id, { tools: toolList() });
      return;
    }

    if (method === "tools/call") {
      const requested = params && params.name;
      const tool = byName.get(requested);
      if (!tool) {
        replyError(id, -32602, `Unknown tool: ${requested}`);
        return;
      }
      try {
        const result = await tool.handler((params && params.arguments) || {});
        reply(id, {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
          isError: false,
        });
      } catch (error) {
        log(`tool ${requested} failed: ${error && error.stack ? error.stack : error}`);
        reply(id, {
          content: [{ type: "text", text: JSON.stringify({ error: String((error && error.message) || error) }, null, 2) }],
          isError: true,
        });
      }
      return;
    }

    replyError(id, -32601, `Method not found: ${method}`);
  }

  /**
   * Queue one incoming frame and return the promise that settles after it has
   * been dispatched. Frames are handled in arrival order, never concurrently.
   */
  function accept(line) {
    const trimmed = line.trim();
    if (trimmed === "") return chain;
    let message;
    try {
      message = JSON.parse(trimmed);
    } catch {
      log("ignored malformed frame on stdin");
      return chain;
    }
    chain = chain.then(() => dispatch(message)).catch((error) => log(`dispatch failed: ${error}`));
    return chain;
  }

  return {
    /** Whether an initialize request has been answered (used by tests). */
    get initialized() {
      return initializeSeen;
    },
    accept,
    toolList,
  };
}

module.exports = { createServer, SUPPORTED_PROTOCOL_VERSIONS, DEFAULT_PROTOCOL_VERSION };
