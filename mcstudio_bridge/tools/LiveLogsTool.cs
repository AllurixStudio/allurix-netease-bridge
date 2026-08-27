using System;
using System.Collections;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("0.2.0.0")]

[McpTool("live_logs", "Read the currently selected MCStudio server log window in real time. Read-only; does not change server selection.")]
public class LiveLogsTool : McpToolBase
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ServerLogViewType = "MCStudio.View.Apollo.ServerLogView";

    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"server_id\":{\"type\":\"integer\",\"description\":\"Optional. Must match the server currently selected in MCStudio's log window.\"}}}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int requestedServerId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "server_id", -1);
            Type applicationType = FindLoadedType("System.Windows.Application");
            if (applicationType == null) return "{\"error\":\"WPF Application type not found\"}";
            PropertyInfo currentProperty = applicationType.GetProperty("Current", StaticFlags);
            object application = currentProperty == null ? null : currentProperty.GetValue(null, null);
            if (application == null) return "{\"error\":\"Application.Current is null\"}";

            object dispatcher = ReadProperty(application, "Dispatcher");
            if (dispatcher == null) return "{\"error\":\"Application dispatcher not found\"}";

            MethodInfo invoke = null;
            object[] invokeArguments = null;
            string result = null;
            Action read = delegate { result = ReadSelectedServerLog(application, requestedServerId); };
            foreach (MethodInfo method in dispatcher.GetType().GetMethods(InstanceFlags))
            {
                if (method.Name != "Invoke") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && AcceptsAction(parameters[0].ParameterType))
                {
                    invoke = method;
                    invokeArguments = new object[] { read };
                    break;
                }
                if (parameters.Length == 2 && AcceptsAction(parameters[0].ParameterType) &&
                    parameters[1].ParameterType == typeof(object[]))
                {
                    invoke = method;
                    invokeArguments = new object[] { read, new object[0] };
                    break;
                }
            }
            if (invoke == null) return "{\"error\":\"Dispatcher.Invoke overload not found\"}";
            invoke.Invoke(dispatcher, invokeArguments);
            return result ?? "{\"error\":\"Live log read returned no result\"}";
        }
        catch (Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) message += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(message) + "\"}";
        }
    }

    private static string ReadSelectedServerLog(object application, int requestedServerId)
    {
        object windows = ReadProperty(application, "Windows");
        IEnumerable enumerable = windows as IEnumerable;
        if (enumerable == null) return "{\"error\":\"Application.Windows is not enumerable\"}";

        bool foundView = false;
        foreach (object window in enumerable)
        {
            if (window == null || window.GetType().FullName != ServerLogViewType) continue;
            foundView = true;
            object viewModel = ReadProperty(window, "DataContext");
            object selectedServer = ReadProperty(viewModel, "SelectServer");
            int selectedServerId = ReadIntProperty(selectedServer, "serverid", -1);
            if (requestedServerId >= 0 && selectedServerId != requestedServerId) continue;

            object record = ReadProperty(viewModel, "LogRecord");
            object document = ReadProperty(record, "LogFlowDocument");
            string text = ReadFlowDocumentText(document);
            if (text == null) return "{\"error\":\"MCS ServerLog LogFlowDocument unavailable\"}";

            int lineCount = 0;
            if (text.Length > 0)
            {
                lineCount = 1;
                foreach (char value in text)
                    if (value == '\n') lineCount++;
            }
            return "{\"source\":\"MCStudio.ServerLogView.LogRecord.LogFlowDocument\",\"realtime\":true" +
                ",\"server_id\":" + selectedServerId +
                ",\"line_count\":" + lineCount +
                ",\"content\":\"" + ApolloToolHelpers.EscapeJson(text) + "\"}";
        }

        if (!foundView) return "{\"error\":\"MCStudio ServerLogView not found\"}";
        if (requestedServerId >= 0)
            return "{\"error\":\"Requested server is not selected in MCStudio ServerLogView\"}";
        return "{\"error\":\"MCStudio ServerLogView has no readable log\"}";
    }

    private static string ReadFlowDocumentText(object document)
    {
        if (document == null) return null;
        object start = ReadProperty(document, "ContentStart");
        object end = ReadProperty(document, "ContentEnd");
        Type textRangeType = FindLoadedType("System.Windows.Documents.TextRange");
        if (start == null || end == null || textRangeType == null) return null;

        ConstructorInfo constructor = null;
        foreach (ConstructorInfo candidate in textRangeType.GetConstructors(InstanceFlags))
        {
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 2 && parameters[0].ParameterType.IsInstanceOfType(start) &&
                parameters[1].ParameterType.IsInstanceOfType(end))
            {
                constructor = candidate;
                break;
            }
        }
        if (constructor == null) return null;

        try
        {
            object range = constructor.Invoke(new object[] { start, end });
            object value = ReadProperty(range, "Text");
            return value as string;
        }
        catch { return null; }
    }

    private static object ReadProperty(object target, string name)
    {
        if (target == null) return null;
        PropertyInfo property = target.GetType().GetProperty(name, InstanceFlags);
        if (property == null || property.GetIndexParameters().Length != 0) return null;
        try { return property.GetValue(target, null); }
        catch { return null; }
    }

    private static int ReadIntProperty(object target, string name, int fallback)
    {
        object value = ReadProperty(target, name);
        if (value == null) return fallback;
        try { return Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(fullName, false);
            if (type != null) return type;
        }
        return null;
    }

    private static bool AcceptsAction(Type type)
    {
        return type == typeof(Delegate) || type == typeof(Action) || type.IsAssignableFrom(typeof(Action));
    }

}
