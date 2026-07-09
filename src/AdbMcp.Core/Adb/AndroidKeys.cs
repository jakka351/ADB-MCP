using System;
using System.Collections.Generic;

namespace AdbMcp.Adb
{
    /// <summary>Friendly-name to Android keycode mapping for the press_key tool.</summary>
    public static class AndroidKeys
    {
        private static readonly Dictionary<string, int> Map =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "back", 4 },
            { "home", 3 },
            { "menu", 82 },
            { "recents", 187 }, { "appswitch", 187 }, { "app_switch", 187 }, { "overview", 187 },
            { "power", 26 },
            { "enter", 66 }, { "return", 66 },
            { "tab", 61 },
            { "space", 62 },
            { "del", 67 }, { "delete", 67 }, { "backspace", 67 },
            { "forwarddel", 112 }, { "forward_del", 112 },
            { "escape", 111 }, { "esc", 111 },
            { "up", 19 }, { "down", 20 }, { "left", 21 }, { "right", 22 },
            { "dpad_center", 23 }, { "center", 23 },
            { "volume_up", 24 }, { "volup", 24 },
            { "volume_down", 25 }, { "voldown", 25 },
            { "mute", 164 },
            { "camera", 27 },
            { "call", 5 }, { "endcall", 6 }, { "end_call", 6 },
            { "search", 84 },
            { "notification", 83 },
            { "brightness_up", 221 }, { "brightness_down", 220 },
            { "media_play_pause", 85 }, { "play_pause", 85 },
            { "media_next", 87 }, { "media_previous", 88 },
            { "wakeup", 224 }, { "wake", 224 }, { "sleep", 223 },
            { "page_up", 92 }, { "page_down", 93 },
            { "move_home", 122 }, { "move_end", 123 },
        };

        /// <summary>
        /// Resolve a key spec to a numeric Android keycode. Accepts a friendly name
        /// ("back"), a raw number ("4"), or the "KEYCODE_" form ("KEYCODE_BACK" -> back).
        /// Returns null when unknown.
        /// </summary>
        public static int? Resolve(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim();

            if (int.TryParse(key, out int code)) return code;

            if (key.StartsWith("KEYCODE_", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("KEYCODE_".Length);

            if (Map.TryGetValue(key, out int mapped)) return mapped;
            return null;
        }

        public static IEnumerable<string> KnownNames => Map.Keys;
    }
}
