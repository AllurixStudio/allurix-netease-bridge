using System;
using System.Collections;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("0.1.2.0")]

[McpTool("client_logs", "Read the current MCStudio local-client log buffer. Read-only; does not control the client or change its log window.")]
public class ClientLogsTool : McpToolBase
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public override string InputSchemaJson { get { return "{\"type\":\"object\",\"properties\":{\"tail_lines\":{\"type\":\"integer\",\"description\":\"Number of latest lines to return (1-1000, default 200).\",\"minimum\":1,\"maximum\":1000}}}"; } }

    public override string Execute(string argumentsJson, McpContext context)
    {
        int tailLines = Math.Max(1, Math.Min(ApolloToolHelpers.ExtractJsonInt(argumentsJson, "tail_lines", 200), 1000));
        try { return context.InvokeOnUIThread(() => ReadClientLog(tailLines)); }
        catch (Exception ex) { return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}"; }
    }

    private static string ReadClientLog(int tailLines)
    {
        Type applicationType = FindLoadedType("System.Windows.Application");
        object application = applicationType == null ? null : applicationType.GetProperty("Current", StaticFlags).GetValue(null, null);
        IEnumerable windows = ReadMember(application, "Windows") as IEnumerable;
        if (windows == null) return "{\"error\":\"MCStudio WPF application unavailable\"}";

        foreach (object window in windows)
        {
            object viewModel = ReadMember(window, "DataContext");
            if (viewModel == null || viewModel.GetType().FullName != "MCStudio.ViewModel.Develop.DeveloperMenuViewModel") continue;
            object game = ReadMember(viewModel, "_game");
            object log = ReadMember(game, "CppModLog");
            MethodInfo getText = log == null ? null : log.GetType().GetMethod("GetText", InstanceFlags, null, Type.EmptyTypes, null);
            string text = getText == null ? null : getText.Invoke(log, null) as string;
            if (text == null) return "{\"error\":\"MCStudio client log buffer unavailable\"}";

            int totalLineCount = CountLines(text);
            string content = TailLines(text, tailLines);
            return "{\"source\":\"MCStudio.CppGameM.CppModLog.GetText\",\"realtime\":true" +
                ",\"name\":\"" + ApolloToolHelpers.EscapeJson(Convert.ToString(ReadMember(log, "Name"))) + "\"" +
                ",\"port\":" + ReadInt(ReadMember(log, "Port"), -1) +
                ",\"line_count\":" + CountLines(content) +
                ",\"total_line_count\":" + totalLineCount +
                ",\"truncated\":" + (content.Length < text.Length ? "true" : "false") +
                ",\"content\":\"" + ApolloToolHelpers.EscapeJson(content) + "\"}";
        }
        return "{\"error\":\"MCStudio DeveloperMenuViewModel not found; launch a local client from MCStudio first\"}";
    }

    private static object ReadMember(object target, string name)
    {
        if (target == null) return null;
        try
        {
            PropertyInfo property = target.GetType().GetProperty(name, InstanceFlags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target, null);
            FieldInfo field = target.GetType().GetField(name, InstanceFlags);
            return field == null ? null : field.GetValue(target);
        }
        catch { return null; }
    }

    private static Type FindLoadedType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(name, false);
            if (type != null) return type;
        }
        return null;
    }

    private static int ReadInt(object value, int fallback)
    {
        try { return value == null ? fallback : Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static int CountLines(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        string[] lines = value.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 0 && lines[lines.Length - 1].Length == 0 ? lines.Length - 1 : lines.Length;
    }

    private static string TailLines(string value, int lineLimit)
    {
        if (CountLines(value) <= lineLimit) return value;
        string normalized = value.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        int total = CountLines(normalized);
        string result = string.Join("\n", lines, total - lineLimit, lineLimit);
        return normalized.EndsWith("\n", StringComparison.Ordinal) ? result + "\n" : result;
    }

}
