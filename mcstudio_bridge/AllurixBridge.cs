using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MC.Mcp;

/// <summary>
/// Dynamic tool loader + MCP lifecycle controller for MCStudio.
///
/// Built-in commands (via "tool" parameter):
///   "list"    - List all loaded tools from tools/ directory
///   "reload"  - Hot-reload: Stop MCP → re-scan tools/ → re-register all → Start MCP
///   "status"  - Show MCP server status, port, loaded tool count
///   Any other - Route to the named tool in tools/
///
/// Architecture:
///   BRegister.dll injects → loads this bridge → ScanAndRegister
///   This bridge is the ONLY tool registered on McpServer.
///   Actual tools live in bin/tools/*.dll as McpToolBase derivatives.
///   Hot-reload re-registers everything without MCStudio restart.
/// </summary>
[McpTool("allurix_bridge", "Allurix MCStudio Bridge. Commands: list, reload, status, or any tool name.")]
public class AllurixBridge : McpToolBase
{
    private static Dictionary<string, McpToolBase> _tools = null;
    private static string _toolsDir = null;
    private static List<string> _loadErrors = new List<string>();

    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"tool\":{\"type\":\"string\",\"description\":\"'list','reload','status', or tool name\"},\"arguments\":{\"type\":\"object\",\"description\":\"Arguments for the target tool\"}},\"required\":[\"tool\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            string toolName = ExtractJsonValue(argumentsJson, "tool");
            if (string.IsNullOrEmpty(toolName))
                return "{\"error\":\"Missing 'tool' parameter\"}";

            if (_toolsDir == null)
            {
                string myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _toolsDir = Path.Combine(myDir, "tools");
            }

            switch (toolName)
            {
                case "list":
                    EnsureLoaded();
                    return ListTools();

                case "reload":
                    return HotReload(context);

                case "status":
                    return GetStatus();

                default:
                    EnsureLoaded();
                    if (_tools == null || !_tools.ContainsKey(toolName))
                        return "{\"error\":\"Tool '" + EscapeJson(toolName) + "' not found. Use tool='list'.\"}";
                    string innerArgs = ExtractJsonObject(argumentsJson, "arguments") ?? "{}";
                    return _tools[toolName].Execute(innerArgs, context);
            }
        }
        catch (Exception ex)
        {
            return "{\"error\":\"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    /// <summary>
    /// Hot-reload: refresh tool DLLs from disk without manual MCP restart.
    /// Since tools are loaded via Assembly.Load(bytes), the file is never locked.
    /// We just re-read all DLLs and rebuild the routing table.
    /// </summary>
    private string HotReload(McpContext context)
    {
        _tools = null;
        EnsureLoaded();
        int count = _tools != null ? _tools.Count : 0;
        return "{\"status\":\"reloaded\",\"tool_count\":" + count + ",\"tools\":" + ListToolNames() + "}";
    }

    private string GetStatus()
    {
        try
        {
            if (_toolsDir == null)
            {
                string myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _toolsDir = Path.Combine(myDir, "tools");
            }
            EnsureLoaded();
            Assembly mcsAsm = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "MCStudio") { mcsAsm = a; break; }
            }
            Type hostType = mcsAsm.GetType("MCStudio.Modules.Mcp.McpServerHost");
            object host = hostType.GetProperty("Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                .GetValue(null, null);

            FieldInfo sf = hostType.GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic);
            object server = sf.GetValue(host);

            Assembly bridgeAsm = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "mcp_csharp_bridge") { bridgeAsm = a; break; }
            }
            Type serverType = bridgeAsm.GetType("MC.Mcp.McpServer");

            string status = serverType.GetProperty("Status").GetValue(server, null).ToString();
            object port = serverType.GetProperty("Port").GetValue(server, null);
            int toolCount = _tools != null ? _tools.Count : 0;

            return "{\"mcp_status\":\"" + status + "\",\"port\":" + port +
                   ",\"loaded_tools\":" + toolCount +
                   ",\"tools_dir\":\"" + EscapeJson(_toolsDir) + "\"," +
                   "\"load_errors\":" + ListLoadErrors() + "}";
        }
        catch (Exception ex)
        {
            return "{\"error\":\"Status check failed: " + EscapeJson(ex.Message) + "\"}";
        }
    }

    private void EnsureLoaded()
    {
        if (_tools != null) return;

        _tools = new Dictionary<string, McpToolBase>(StringComparer.OrdinalIgnoreCase);
        _loadErrors = new List<string>();

        if (!Directory.Exists(_toolsDir))
        {
            Directory.CreateDirectory(_toolsDir);
            return;
        }

        foreach (string dllPath in Directory.GetFiles(_toolsDir, "*.dll"))
        {
            try
            {
                // Load from bytes - file never locked, can be replaced on disk anytime
                byte[] bytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(bytes);

                foreach (Type t in asm.GetTypes())
                {
                    if (t.IsAbstract || !t.IsPublic) continue;
                    if (!typeof(McpToolBase).IsAssignableFrom(t)) continue;

                    object[] attrs = t.GetCustomAttributes(typeof(McpToolAttribute), false);
                    if (attrs.Length == 0) continue;

                    McpToolAttribute attr = (McpToolAttribute)attrs[0];
                    McpToolBase instance = (McpToolBase)Activator.CreateInstance(t);
                    _tools[attr.Name] = instance;
                }
            }
            catch (Exception ex)
            {
                _loadErrors.Add(Path.GetFileName(dllPath) + ": " + ex.Message);
            }
        }
    }

    private string ListLoadErrors()
    {
        string list = "[";
        bool first = true;
        foreach (string error in _loadErrors)
        {
            if (!first) list += ",";
            first = false;
            list += "\"" + EscapeJson(error) + "\"";
        }
        return list + "]";
    }
    private string ListTools()
    {
        if (_tools == null || _tools.Count == 0)
            return "{\"tools\":[],\"tools_dir\":\"" + EscapeJson(_toolsDir) + "\"}";

        string list = "[";
        bool first = true;
        foreach (var kv in _tools)
        {
            if (!first) list += ",";
            first = false;
            object[] attrs = kv.Value.GetType().GetCustomAttributes(typeof(McpToolAttribute), false);
            string desc = attrs.Length > 0 ? ((McpToolAttribute)attrs[0]).Description : "";
            list += "{\"name\":\"" + EscapeJson(kv.Key) + "\",\"description\":\"" + EscapeJson(desc) + "\"}";
        }
        list += "]";
        return "{\"tools\":" + list + ",\"tools_dir\":\"" + EscapeJson(_toolsDir) + "\"}";
    }

    private string ListToolNames()
    {
        if (_tools == null) return "[]";
        string list = "[";
        bool first = true;
        foreach (var kv in _tools) { if (!first) list += ","; first = false; list += "\"" + EscapeJson(kv.Key) + "\""; }
        return list + "]";
    }

    private static string ExtractJsonValue(string json, string key)
    {
        string search = "\"" + key + "\"";
        int idx = json.IndexOf(search);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx + search.Length);
        if (idx < 0) return null;
        while (idx < json.Length - 1 && (json[idx + 1] == ' ' || json[idx + 1] == '\t')) idx++;
        idx++;
        if (idx >= json.Length) return null;
        if (json[idx] == '"')
        {
            int end = json.IndexOf('"', idx + 1);
            if (end < 0) return null;
            return json.Substring(idx + 1, end - idx - 1);
        }
        return null;
    }

    private static string ExtractJsonObject(string json, string key)
    {
        string search = "\"" + key + "\"";
        int idx = json.IndexOf(search);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx + search.Length);
        if (idx < 0) return null;
        int start = json.IndexOf('{', idx);
        if (start < 0) return null;
        int depth = 0;
        for (int i = start; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}') { depth--; if (depth == 0) return json.Substring(start, i - start + 1); }
        }
        return null;
    }

    private static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
