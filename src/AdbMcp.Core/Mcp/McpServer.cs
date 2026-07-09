using System;
using System.IO;
using AdbMcp.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Mcp
{
    /// <summary>
    /// Minimal, dependency-light MCP server implementing JSON-RPC 2.0 over a
    /// newline-delimited stdio transport (one JSON message per line, no embedded
    /// newlines). Handles initialize / tools.list / tools.call / ping and returns
    /// proper JSON-RPC errors for protocol faults while surfacing tool faults as
    /// isError results.
    /// </summary>
    public sealed class McpServer
    {
        public const string ServerName = "adb-mcp";
        public const string ServerVersion = "0.1.0";

        // Protocol revisions we understand; we echo the client's if supported.
        private static readonly string[] SupportedProtocols =
            { "2025-06-18", "2025-03-26", "2024-11-05" };
        private const string DefaultProtocol = "2024-11-05";

        private readonly ToolRegistry _registry;
        private readonly ToolContext _context;
        private readonly TextReader _in;
        private readonly TextWriter _out;
        private readonly object _writeGate = new object();

        public McpServer(ToolRegistry registry, ToolContext context, TextReader input, TextWriter output)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _in = input ?? throw new ArgumentNullException(nameof(input));
            _out = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>Blocking read/dispatch loop. Returns when stdin reaches EOF.</summary>
        public void Run()
        {
            Log.Info("ADB-MCP server ready. Tools: " + string.Join(", ", _registry.Names));
            string line;
            while ((line = _in.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                JObject request;
                try
                {
                    request = JObject.Parse(line);
                }
                catch (JsonException ex)
                {
                    Log.Warn("Parse error: " + ex.Message);
                    WriteMessage(ErrorResponse(null, -32700, "Parse error: " + ex.Message));
                    continue;
                }

                JObject response;
                try
                {
                    response = Dispatch(request);
                }
                catch (Exception ex)
                {
                    Log.Error("Unhandled dispatch error", ex);
                    var id = request["id"];
                    response = ErrorResponse(id, -32603, "Internal error: " + ex.Message);
                }

                if (response != null)
                    WriteMessage(response);
            }
            Log.Info("stdin closed; server exiting.");
        }

        private JObject Dispatch(JObject request)
        {
            var method = request.Value<string>("method");
            var id = request["id"]; // absent => notification
            bool isNotification = id == null || id.Type == JTokenType.Null;

            if (string.IsNullOrEmpty(method))
                return isNotification ? null : ErrorResponse(id, -32600, "Invalid Request: missing method.");

            var prms = request["params"] as JObject ?? new JObject();

            switch (method)
            {
                case "initialize":
                    return Result(id, HandleInitialize(prms));

                case "notifications/initialized":
                case "notifications/cancelled":
                case "initialized":
                    return null; // notifications: no response

                case "ping":
                    return Result(id, new JObject());

                case "tools/list":
                    return Result(id, new JObject { ["tools"] = _registry.ToListJson() });

                case "tools/call":
                    return HandleToolCall(id, prms);

                case "resources/list":
                    return Result(id, new JObject { ["resources"] = new JArray() });

                case "prompts/list":
                    return Result(id, new JObject { ["prompts"] = new JArray() });

                default:
                    return isNotification ? null : ErrorResponse(id, -32601, "Method not found: " + method);
            }
        }

        private JObject HandleInitialize(JObject prms)
        {
            string requested = prms.Value<string>("protocolVersion");
            string negotiated = DefaultProtocol;
            if (!string.IsNullOrEmpty(requested))
            {
                foreach (var v in SupportedProtocols)
                    if (v == requested) { negotiated = requested; break; }
            }

            Log.Info("initialize: client protocol=" + (requested ?? "?") + " -> serving " + negotiated);

            return new JObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion,
                },
                ["instructions"] =
                    "Android device control via ADB. Perceive with get_state (cheap UI hierarchy) first; " +
                    "sample get_screenshot only when the hierarchy is ambiguous. Prefer targeting elements by " +
                    "text/resource-id over raw coordinates. Irreversible actions (send_sms) require confirm=true.",
            };
        }

        private JObject HandleToolCall(JToken id, JObject prms)
        {
            var name = prms.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return ErrorResponse(id, -32602, "tools/call requires a 'name'.");

            if (!_registry.TryGet(name, out var tool))
                return ErrorResponse(id, -32602, "Unknown tool: " + name);

            var arguments = prms["arguments"] as JObject ?? new JObject();

            ToolResult result;
            try
            {
                Log.Debug("tools/call " + name + " " + arguments.ToString(Formatting.None));
                result = tool.Invoke(arguments, _context);
            }
            catch (Exception ex)
            {
                Log.Error("Tool '" + name + "' threw", ex);
                result = ToolResult.Error(name + " failed: " + ex.Message);
            }

            return Result(id, result.ToResultObject());
        }

        // ---- JSON-RPC envelope helpers ------------------------------------------------

        private static JObject Result(JToken id, JObject result)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = result };

        private static JObject ErrorResponse(JToken id, int code, string message)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };

        private void WriteMessage(JObject message)
        {
            // Compact, single-line JSON terminated by newline. Serialize outside the lock,
            // write inside it so concurrent responses never interleave on the wire.
            string payload = message.ToString(Formatting.None);
            lock (_writeGate)
            {
                _out.Write(payload);
                _out.Write('\n');
                _out.Flush();
            }
        }
    }
}
