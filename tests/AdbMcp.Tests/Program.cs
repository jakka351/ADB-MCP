using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using AdbMcp.Adb;
using AdbMcp.Config;
using AdbMcp.Imaging;
using AdbMcp.Mcp;
using AdbMcp.Safety;
using AdbMcp.Scrcpy;
using Newtonsoft.Json.Linq;

namespace AdbMcp.Tests
{
    /// <summary>
    /// Dependency-light self-test harness. Exercises the pure/offline logic — argument
    /// quoting, input escaping, hierarchy parsing + element resolution, the shell
    /// allowlist, frame encoding, and a full in-memory MCP request/response round-trip.
    /// No device required. Exits non-zero on the first failure.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.WriteLine("== ADB-MCP self-tests ==");

            TestArgQuoting();
            TestInputEncoding();
            TestHierarchyParsing();
            TestElementResolution();
            TestShellPolicy();
            TestFrameProcessor();
            TestScrcpyControlProtocol();
            TestScrcpyVideoDemux();
            TestNv12Converter();
            TestMcpRoundTrip();

            Console.WriteLine();
            Console.WriteLine("Passed: " + _passed + "   Failed: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // ---- individual suites --------------------------------------------------------

        private static void TestArgQuoting()
        {
            Section("ArgQuoting");
            Eq("plain", "abc", ArgQuoting.Quote("abc"));
            Eq("spaces", "\"a b\"", ArgQuoting.Quote("a b"));
            Eq("embedded quote", "\"a\\\"b\"", ArgQuoting.Quote("a\"b"));
            Eq("trailing backslash", "\"a b\\\\\"", ArgQuoting.Quote("a b\\"));
            Eq("join", "-s dev shell", ArgQuoting.Join(new[] { "-s", "dev", "shell" }));
        }

        private static void TestInputEncoding()
        {
            Section("InputEncoding");
            Eq("space -> %s", "hello%sworld", InputEncoding.EscapeForInputText("hello world"));
            Eq("ampersand escaped", "a\\&b", InputEncoding.EscapeForInputText("a&b"));
            Eq("quote escaped", "say\\'hi\\'", InputEncoding.EscapeForInputText("say'hi'"));
            Eq("empty", "", InputEncoding.EscapeForInputText(""));
        }

        private const string SampleXml =
            "<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>" +
            "<hierarchy rotation=\"0\">" +
            "<node index=\"0\" text=\"\" resource-id=\"\" class=\"android.widget.FrameLayout\" package=\"com.example\" content-desc=\"\" clickable=\"false\" enabled=\"true\" bounds=\"[0,0][1080,2340]\">" +
            "<node index=\"0\" text=\"Messages\" resource-id=\"com.example:id/title\" class=\"android.widget.TextView\" package=\"com.example\" content-desc=\"\" clickable=\"false\" enabled=\"true\" bounds=\"[42,100][400,180]\" />" +
            "<node index=\"1\" text=\"\" resource-id=\"com.example:id/send\" class=\"android.widget.ImageButton\" package=\"com.example\" content-desc=\"Send\" clickable=\"true\" enabled=\"true\" bounds=\"[980,2000][1060,2080]\" />" +
            "<node index=\"2\" text=\"Type a message\" resource-id=\"com.example:id/compose\" class=\"android.widget.EditText\" package=\"com.example\" content-desc=\"\" clickable=\"true\" enabled=\"true\" bounds=\"[42,2000][960,2080]\" />" +
            "</node></hierarchy>";

        private static void TestHierarchyParsing()
        {
            Section("UiHierarchy.Parse");
            var h = UiHierarchy.Parse(SampleXml);
            Eq("node count", 4, h.Nodes.Count);

            var interesting = h.Interesting();
            Eq("interesting count", 3, interesting.Count);
            Eq("top-most is Messages", "Messages", interesting[0].Text);
            // Ties on Top (2000) break by Left: compose (42) before Send (980).
            Eq("second is compose editable", true, interesting[1].IsEditable);

            var send = h.Nodes.Find(n => n.ResourceId.EndsWith("/send"));
            Assert("send node found", send != null);
            Eq("send center x", 1020, send.CenterX);
            Eq("send center y", 2040, send.CenterY);
            Eq("send is clickable", true, send.Clickable);
        }

        private static void TestElementResolution()
        {
            Section("UiHierarchy.Resolve");
            var h = UiHierarchy.Parse(SampleXml);

            var byText = h.Resolve("Messages", null, null, clickableOnly: false);
            Eq("resolve by text count", 1, byText.Count);
            Eq("resolve by text class", "TextView", byText[0].ShortClass);

            var byDesc = h.Resolve(null, null, "Send", clickableOnly: true);
            Eq("resolve send by desc count", 1, byDesc.Count);
            Eq("resolve send center", 1020, byDesc[0].CenterX);

            var byId = h.Resolve(null, "compose", null, clickableOnly: false);
            Eq("resolve by partial id count", 1, byId.Count);
            Eq("resolve by id editable", true, byId[0].IsEditable);

            var none = h.Resolve("does-not-exist", null, null, clickableOnly: false);
            Eq("no match", 0, none.Count);
        }

        private static void TestShellPolicy()
        {
            Section("ShellPolicy");
            var strict = new ShellPolicy(ServerConfig.DefaultAllowlist, allowUnlistedWithConfirm: false);
            Assert("input allowlisted", strict.Evaluate("input tap 100 200").Allowed);
            Assert("dumpsys allowlisted", strict.Evaluate("dumpsys window").Allowed);
            Assert("rm blocked", !strict.Evaluate("rm -rf /sdcard").Allowed);
            Assert("pm uninstall blocked", !strict.Evaluate("pm uninstall com.foo").Allowed);

            var lax = new ShellPolicy(ServerConfig.DefaultAllowlist, allowUnlistedWithConfirm: true);
            var d = lax.Evaluate("rm -rf /sdcard");
            Assert("override allows with confirm", d.Allowed && d.RequiresConfirmation);
        }

        private static void TestFrameProcessor()
        {
            Section("FrameProcessor");
            byte[] png;
            using (var bmp = new Bitmap(200, 100, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.Clear(Color.CornflowerBlue);
                bmp.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }

            var jpeg = FrameProcessor.Encode(png, "jpeg", maxDimension: 50, jpegQuality: 60);
            Eq("downscaled width", 50, jpeg.Width);
            Eq("downscaled height", 25, jpeg.Height);
            Eq("jpeg mime", "image/jpeg", jpeg.MimeType);
            Assert("jpeg SOI marker", jpeg.Data.Length > 2 && jpeg.Data[0] == 0xFF && jpeg.Data[1] == 0xD8);
            Assert("base64 non-empty", !string.IsNullOrEmpty(jpeg.Base64));

            var pngOut = FrameProcessor.Encode(png, "png", maxDimension: 0, jpegQuality: 60);
            Eq("png keeps size", 200, pngOut.Width);
            Eq("png mime", "image/png", pngOut.MimeType);
        }

        private static void TestScrcpyControlProtocol()
        {
            Section("ScrcpyProtocol (control channel)");

            var key = ScrcpyProtocol.InjectKeycode(ScrcpyProtocol.KEY_ACTION_DOWN, 66, 0, 0);
            Eq("keycode msg length", 14, key.Length);
            Eq("keycode type", (byte)0, key[0]);
            Eq("keycode value BE low byte", (byte)66, key[5]);

            var text = ScrcpyProtocol.InjectText("Hi");
            Eq("text msg length", 7, text.Length);
            Eq("text type", (byte)1, text[0]);
            Eq("text length BE", (byte)2, text[4]);
            Eq("text first char", (byte)72, text[5]);

            var touch = ScrcpyProtocol.InjectTouch(ScrcpyProtocol.ACTION_DOWN, ScrcpyProtocol.POINTER_ID_FINGER,
                100, 200, 1080, 1920, ScrcpyProtocol.PRESSURE_MAX, ScrcpyProtocol.BUTTON_PRIMARY, ScrcpyProtocol.BUTTON_PRIMARY);
            Eq("touch msg length", 32, touch.Length);
            Eq("touch type", (byte)2, touch[0]);
            Eq("pointerId last byte (-2)", (byte)0xFE, touch[9]);
            Eq("touch x BE low byte", (byte)100, touch[13]);
            Eq("touch pressure high byte", (byte)0xFF, touch[22]);
            Eq("touch buttons low byte", (byte)1, touch[31]);

            var back = ScrcpyProtocol.BackOrScreenOn(ScrcpyProtocol.ACTION_DOWN);
            Eq("back msg length", 2, back.Length);
            Eq("back type", (byte)4, back[0]);

            Eq("pressure 1.0", (ushort)0xFFFF, ScrcpyProtocol.ToFixedPointPressure(1.0));
            Eq("pressure 0.0", (ushort)0, ScrcpyProtocol.ToFixedPointPressure(0.0));
            Eq("pressure 0.5", (ushort)0x8000, ScrcpyProtocol.ToFixedPointPressure(0.5));
        }

        private static void TestScrcpyVideoDemux()
        {
            Section("ScrcpyVideoStream (demux)");

            var ms = new MemoryStream();
            // 64-byte device name
            var name = new byte[64];
            var nb = Encoding.UTF8.GetBytes("TestDevice");
            Array.Copy(nb, name, nb.Length);
            ms.Write(name, 0, 64);
            // codec meta: "h264" + width + height
            var cm = new BeBuffer(12);
            cm.Bytes(new byte[] { 0x68, 0x32, 0x36, 0x34 });
            cm.I32(1080);
            cm.I32(1920);
            WriteAll(ms, cm.ToArray());
            // config packet (FLAG_CONFIG = bit 63 = long.MinValue), 4 bytes payload
            var h1 = new BeBuffer(12); h1.I64(long.MinValue); h1.I32(4);
            WriteAll(ms, h1.ToArray()); WriteAll(ms, new byte[] { 1, 2, 3, 4 });
            // key frame packet (FLAG_KEY_FRAME = bit 62), pts=12345, 3 bytes payload
            var h2 = new BeBuffer(12); h2.I64(0x4000000000000000L | 12345L); h2.I32(3);
            WriteAll(ms, h2.ToArray()); WriteAll(ms, new byte[] { 9, 8, 7 });
            ms.Position = 0;

            var vs = new ScrcpyVideoStream(ms, hasFrameMeta: true);
            Eq("device name", "TestDevice", vs.ReadDeviceName());
            var meta = vs.ReadCodecMeta();
            Eq("codec id", "h264", meta.CodecId);
            Eq("codec width", 1080, meta.Width);
            Eq("codec height", 1920, meta.Height);

            var p1 = vs.ReadPacket();
            Eq("packet1 is config", true, p1.IsConfig);
            Eq("packet1 length", 4, p1.Data.Length);

            var p2 = vs.ReadPacket();
            Eq("packet2 is keyframe", true, p2.IsKeyFrame);
            Eq("packet2 not config", false, p2.IsConfig);
            Eq("packet2 pts", 12345L, p2.PtsUs);
            Eq("packet2 length", 3, p2.Data.Length);

            Assert("stream end returns null", vs.ReadPacket() == null);
        }

        private static void TestNv12Converter()
        {
            Section("Nv12Converter");

            // Grayscale: Y=128, U=V=128 -> R=G=B ~130.
            var gray = FilledNv12(4, 4, y: 128, u: 128, v: 128);
            using (var bmp = Nv12Converter.ToBitmap(gray, 4, 4, 4))
            {
                var px = bmp.GetPixel(2, 2);
                Assert("gray R in range", px.R >= 128 && px.R <= 133);
                Eq("gray R==G", px.R, px.G);
                Eq("gray G==B", px.G, px.B);
            }

            // High V (red-ish): R should dominate B and G.
            var reddish = FilledNv12(4, 4, y: 128, u: 128, v: 255);
            using (var bmp = Nv12Converter.ToBitmap(reddish, 4, 4, 4))
            {
                var px = bmp.GetPixel(1, 1);
                Assert("high V -> R > B", px.R > px.B);
                Assert("high V -> R > G", px.R > px.G);
            }
        }

        private static byte[] FilledNv12(int w, int h, byte y, byte u, byte v)
        {
            int ySize = w * h;
            int uvSize = w * h / 2;
            var data = new byte[ySize + uvSize];
            for (int i = 0; i < ySize; i++) data[i] = y;
            for (int i = 0; i < uvSize; i += 2) { data[ySize + i] = u; data[ySize + i + 1] = v; }
            return data;
        }

        private static void WriteAll(Stream s, byte[] b) => s.Write(b, 0, b.Length);

        private static void TestMcpRoundTrip()
        {
            Section("McpServer round-trip");
            var registry = DefaultTools.BuildRegistry();
            var adb = new AdbClient("adb", "dummy-serial", 1000);
            var ctx = new ToolContext(adb, new ServerConfig(),
                new ScrcpyMirror("scrcpy", "dummy-serial"),
                new ShellPolicy(ServerConfig.DefaultAllowlist, false));

            string input = string.Join("\n", new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"wait\",\"arguments\":{\"seconds\":0}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"send_sms\",\"arguments\":{\"phone\":\"+1555\",\"message\":\"hi\"}}}",
            }) + "\n";

            var sw = new StringWriter();
            new McpServer(registry, ctx, new StringReader(input), sw).Run();

            var lines = sw.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Eq("response count (notification is silent)", 4, lines.Length);

            var init = JObject.Parse(lines[0]);
            Eq("initialize protocol echoed", "2024-11-05", init["result"]?["protocolVersion"]?.ToString());

            var list = JObject.Parse(lines[1]);
            Assert("tools/list returns >=14", (list["result"]?["tools"] as JArray)?.Count >= 14);

            var wait = JObject.Parse(lines[2]);
            Eq("wait not error", false, wait["result"]?["isError"]?.Value<bool>());

            var sms = JObject.Parse(lines[3]);
            string smsText = sms["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
            Assert("sms gated (confirmation required)", smsText.Contains("CONFIRMATION REQUIRED"));
        }

        // ---- tiny assertion framework -------------------------------------------------

        private static void Section(string name) => Console.WriteLine("\n[" + name + "]");

        private static void Eq<T>(string label, T expected, T actual)
        {
            if (Equals(expected, actual)) Pass(label);
            else Fail(label, "expected <" + expected + ">, got <" + actual + ">");
        }

        private static void Assert(string label, bool condition)
        {
            if (condition) Pass(label);
            else Fail(label, "condition was false");
        }

        private static void Pass(string label)
        {
            _passed++;
            Console.WriteLine("  PASS  " + label);
        }

        private static void Fail(string label, string detail)
        {
            _failed++;
            Console.WriteLine("  FAIL  " + label + " — " + detail);
        }
    }
}
