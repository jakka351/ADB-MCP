using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdbMcp.Adb
{
    /// <summary>A single node from an uiautomator view hierarchy dump.</summary>
    public sealed class UiNode
    {
        public int Index { get; set; }
        public string Text { get; set; } = "";
        public string ResourceId { get; set; } = "";
        public string Class { get; set; } = "";
        public string Package { get; set; } = "";
        public string ContentDesc { get; set; } = "";
        public bool Clickable { get; set; }
        public bool LongClickable { get; set; }
        public bool Scrollable { get; set; }
        public bool Checkable { get; set; }
        public bool Checked { get; set; }
        public bool Focusable { get; set; }
        public bool Focused { get; set; }
        public bool Enabled { get; set; }
        public bool Selected { get; set; }
        public bool Password { get; set; }

        // Bounds in device pixels.
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }

        public int CenterX => (Left + Right) / 2;
        public int CenterY => (Top + Bottom) / 2;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool HasArea => Width > 0 && Height > 0;

        public bool IsEditable =>
            Class.IndexOf("EditText", StringComparison.OrdinalIgnoreCase) >= 0 ||
            Class.IndexOf("AutoComplete", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Elements a model would plausibly act on: interactive, or carrying visible label text.</summary>
        public bool IsInteresting =>
            Clickable || LongClickable || Scrollable || Checkable || IsEditable ||
            !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(ContentDesc);

        public string ShortClass
        {
            get
            {
                if (string.IsNullOrEmpty(Class)) return "";
                int dot = Class.LastIndexOf('.');
                return dot >= 0 && dot < Class.Length - 1 ? Class.Substring(dot + 1) : Class;
            }
        }
    }

    /// <summary>
    /// Parses uiautomator XML dumps into a flat, searchable element list and offers
    /// element-grounded lookup (by text / content-desc / resource-id / index) so the
    /// agent can target elements by meaning rather than fragile raw coordinates.
    /// </summary>
    public sealed class UiHierarchy
    {
        private static readonly Regex BoundsRe =
            new Regex(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", RegexOptions.Compiled);

        public List<UiNode> Nodes { get; } = new List<UiNode>();

        public static UiHierarchy Parse(string xml)
        {
            var h = new UiHierarchy();
            if (string.IsNullOrWhiteSpace(xml)) return h;

            // Some adb/OEM builds prepend a status line before the XML. Trim to the root.
            int start = xml.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
            if (start > 0) xml = xml.Substring(start);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                throw new AdbException("Could not parse uiautomator dump XML: " + ex.Message, ex);
            }

            int ordinal = 0;
            foreach (var el in doc.Descendants("node"))
            {
                var n = new UiNode
                {
                    Index = ordinal++,
                    Text = Attr(el, "text"),
                    ResourceId = Attr(el, "resource-id"),
                    Class = Attr(el, "class"),
                    Package = Attr(el, "package"),
                    ContentDesc = Attr(el, "content-desc"),
                    Clickable = Bool(el, "clickable"),
                    LongClickable = Bool(el, "long-clickable"),
                    Scrollable = Bool(el, "scrollable"),
                    Checkable = Bool(el, "checkable"),
                    Checked = Bool(el, "checked"),
                    Focusable = Bool(el, "focusable"),
                    Focused = Bool(el, "focused"),
                    Enabled = Bool(el, "enabled"),
                    Selected = Bool(el, "selected"),
                    Password = Bool(el, "password"),
                };
                ParseBounds(Attr(el, "bounds"), n);
                h.Nodes.Add(n);
            }
            return h;
        }

        private static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

        private static bool Bool(XElement el, string name)
            => string.Equals(el.Attribute(name)?.Value, "true", StringComparison.OrdinalIgnoreCase);

        private static void ParseBounds(string bounds, UiNode n)
        {
            if (string.IsNullOrEmpty(bounds)) return;
            var m = BoundsRe.Match(bounds);
            if (!m.Success) return;
            n.Left = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            n.Top = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            n.Right = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            n.Bottom = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>The interesting, on-screen nodes in stable top-to-bottom order.</summary>
        public List<UiNode> Interesting()
        {
            var list = new List<UiNode>();
            foreach (var n in Nodes)
                if (n.HasArea && n.IsInteresting) list.Add(n);
            list.Sort((a, b) =>
            {
                int c = a.Top.CompareTo(b.Top);
                return c != 0 ? c : a.Left.CompareTo(b.Left);
            });
            return list;
        }

        /// <summary>
        /// Resolve a target to a node. Matching order favours precision: exact resource-id,
        /// then exact text/desc, then case-insensitive "contains". Returns all matches so
        /// callers can report ambiguity.
        /// </summary>
        public List<UiNode> Resolve(string text, string resourceId, string contentDesc, bool clickableOnly)
        {
            IEnumerable<UiNode> pool = Nodes;
            var result = new List<UiNode>();

            Func<UiNode, bool> areaOk = n => n.HasArea && (!clickableOnly || n.Clickable || n.LongClickable || n.Checkable || n.IsEditable);

            if (!string.IsNullOrEmpty(resourceId))
            {
                foreach (var n in pool)
                    if (areaOk(n) && n.ResourceId.Equals(resourceId, StringComparison.OrdinalIgnoreCase)) result.Add(n);
                if (result.Count > 0) return result;
                foreach (var n in pool)
                    if (areaOk(n) && n.ResourceId.IndexOf(resourceId, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(n);
                if (result.Count > 0) return result;
            }

            if (!string.IsNullOrEmpty(contentDesc))
            {
                foreach (var n in pool)
                    if (areaOk(n) && n.ContentDesc.Equals(contentDesc, StringComparison.OrdinalIgnoreCase)) result.Add(n);
                if (result.Count > 0) return result;
                foreach (var n in pool)
                    if (areaOk(n) && n.ContentDesc.IndexOf(contentDesc, StringComparison.OrdinalIgnoreCase) >= 0) result.Add(n);
                if (result.Count > 0) return result;
            }

            if (!string.IsNullOrEmpty(text))
            {
                foreach (var n in pool)
                    if (areaOk(n) && (n.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
                                   || n.ContentDesc.Equals(text, StringComparison.OrdinalIgnoreCase))) result.Add(n);
                if (result.Count > 0) return result;
                foreach (var n in pool)
                    if (areaOk(n) && (n.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                   || n.ContentDesc.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) result.Add(n);
            }

            return result;
        }

        /// <summary>Render the interesting elements as a compact, numbered, model-friendly list.</summary>
        public string Describe(bool includeAll, int max = 200)
        {
            var list = includeAll ? Nodes : Interesting();
            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < list.Count && shown < max; i++)
            {
                var n = list[i];
                if (!includeAll && !n.HasArea) continue;
                sb.Append('[').Append(i).Append("] ").Append(n.ShortClass);
                if (!string.IsNullOrEmpty(n.Text)) sb.Append(" text=\"").Append(Truncate(n.Text, 80)).Append('"');
                if (!string.IsNullOrEmpty(n.ContentDesc)) sb.Append(" desc=\"").Append(Truncate(n.ContentDesc, 80)).Append('"');
                if (!string.IsNullOrEmpty(n.ResourceId)) sb.Append(" id=").Append(n.ResourceId);
                var flags = new List<string>();
                if (n.Clickable) flags.Add("click");
                if (n.LongClickable) flags.Add("longclick");
                if (n.Scrollable) flags.Add("scroll");
                if (n.IsEditable) flags.Add("edit");
                if (n.Checkable) flags.Add(n.Checked ? "checked" : "checkable");
                if (!n.Enabled) flags.Add("disabled");
                if (flags.Count > 0) sb.Append(" {").Append(string.Join(",", flags)).Append('}');
                sb.Append(" @(").Append(n.CenterX).Append(',').Append(n.CenterY).Append(')');
                sb.Append('\n');
                shown++;
            }
            if (shown == 0) sb.Append("(no interactable elements found)\n");
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
