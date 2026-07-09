using AdbMcp.Tools;

namespace AdbMcp.Mcp
{
    /// <summary>Registers the full ADB-MCP tool surface onto a registry.</summary>
    public static class DefaultTools
    {
        public static ToolRegistry BuildRegistry()
        {
            var r = new ToolRegistry();

            // Perception
            r.Register(new ListDevicesTool());
            r.Register(new GetStateTool());
            r.Register(new GetScreenshotTool());
            r.Register(new GetNotificationsTool());

            // Actuators
            r.Register(new TapTool());
            r.Register(new TypeTextTool());
            r.Register(new SwipeTool());
            r.Register(new PressKeyTool());
            r.Register(new OpenAppTool());

            // Gated / system
            r.Register(new SendSmsTool());
            r.Register(new ShellTool());
            r.Register(new StartMirrorTool());
            r.Register(new StopMirrorTool());
            r.Register(new WaitTool());

            // scrcpy low-latency stream (H.264 video + control socket)
            r.Register(new StartStreamTool());
            r.Register(new StopStreamTool());
            r.Register(new StreamStatusTool());

            return r;
        }
    }
}
