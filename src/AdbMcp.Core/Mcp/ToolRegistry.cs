using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Mcp
{
    /// <summary>Holds the registered tools and renders the tools/list payload.</summary>
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            new Dictionary<string, ITool>(StringComparer.Ordinal);
        private readonly List<ITool> _ordered = new List<ITool>();

        public void Register(ITool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            if (_tools.ContainsKey(tool.Name))
                throw new InvalidOperationException("Duplicate tool name: " + tool.Name);
            _tools[tool.Name] = tool;
            _ordered.Add(tool);
        }

        public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool);

        public int Count => _ordered.Count;

        public IEnumerable<string> Names
        {
            get { foreach (var t in _ordered) yield return t.Name; }
        }

        /// <summary>Build the array of tool descriptors returned by tools/list.</summary>
        public JArray ToListJson()
        {
            var arr = new JArray();
            foreach (var t in _ordered)
            {
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema ?? new JObject { ["type"] = "object" },
                });
            }
            return arr;
        }
    }
}
