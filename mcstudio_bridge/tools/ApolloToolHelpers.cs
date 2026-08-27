using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

internal static class ApolloToolHelpers
{
    internal static int ExtractJsonInt(string json, string key, int fallback)
    {
        string search = "\"" + key + "\"";
        int index = json.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return fallback;
        index = json.IndexOf(':', index + search.Length);
        if (index < 0) return fallback;
        index++;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        int start = index;
        if (index < json.Length && json[index] == '-') index++;
        while (index < json.Length && char.IsDigit(json[index])) index++;
        int value;
        return index > start && int.TryParse(json.Substring(start, index - start), out value)
            ? value
            : fallback;
    }

    internal static bool ExtractJsonBool(string json, string key, bool fallback)
    {
        string search = "\"" + key + "\"";
        int index = json.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return fallback;
        index = json.IndexOf(':', index + search.Length);
        if (index < 0) return fallback;
        index++;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        if (index + 4 <= json.Length &&
            string.Compare(json, index, "true", 0, 4, StringComparison.Ordinal) == 0)
            return true;
        if (index + 5 <= json.Length &&
            string.Compare(json, index, "false", 0, 5, StringComparison.Ordinal) == 0)
            return false;
        return fallback;
    }

    internal static string ExtractJsonString(string json, string key)
    {
        string search = "\"" + key + "\"";
        int index = json.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return null;
        index = json.IndexOf(':', index + search.Length);
        if (index < 0) return null;
        index++;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        if (index >= json.Length || json[index] != '"') return null;

        StringBuilder value = new StringBuilder();
        bool escaped = false;
        for (index++; index < json.Length; index++)
        {
            char character = json[index];
            if (!escaped)
            {
                if (character == '"') return value.ToString();
                if (character == '\\') { escaped = true; continue; }
                value.Append(character);
                continue;
            }

            switch (character)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                default: return null;
            }
            escaped = false;
        }
        return null;
    }

    internal static Assembly FindAssembly(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name == name) return assembly;
        return null;
    }

    internal static object ResolveProjectListViewModel(Assembly mcStudio, out Type viewModelType)
    {
        viewModelType = mcStudio == null
            ? null
            : mcStudio.GetType("MCStudio.ViewModel.Apollo.ApolloProjectListViewModel");
        Assembly mvvm = FindAssembly("GalaSoft.MvvmLight.Extras");
        Type iocType = mvvm == null ? null : mvvm.GetType("GalaSoft.MvvmLight.Ioc.SimpleIoc");
        if (viewModelType == null || iocType == null) return null;
        object ioc = ReadMember(iocType, null, "Default", true);
        MethodInfo getInstance = ioc == null
            ? null
            : ioc.GetType().GetMethod("GetInstance", new Type[] { typeof(Type) });
        return getInstance == null
            ? null
            : getInstance.Invoke(ioc, new object[] { viewModelType });
    }

    internal static bool QueueOnUi(Action action)
    {
        Assembly framework = FindAssembly("PresentationFramework");
        Type appType = framework == null ? null : framework.GetType("System.Windows.Application");
        object app = appType == null ? null : ReadMember(appType, null, "Current", true);
        object dispatcher = app == null ? null : ReadMember(app.GetType(), app, "Dispatcher", false);
        MethodInfo beginInvoke = dispatcher == null
            ? null
            : dispatcher.GetType().GetMethod("BeginInvoke", new Type[] { typeof(Delegate), typeof(object[]) });
        if (beginInvoke == null) return false;
        beginInvoke.Invoke(dispatcher, new object[] { action, null });
        return true;
    }

    internal static object FindProject(Assembly mcStudio, int apolloId)
    {
        if (mcStudio == null) return null;
        Type managerType = mcStudio.GetType("MCStudio.Modules.Apollo.ApolloProjectManager");
        if (managerType == null) return null;
        object manager = ReadMember(managerType, null, "Instance", true);
        IEnumerable projects = ReadMember(managerType, manager, "ApolloProjectList", false) as IEnumerable;
        if (projects == null) return null;
        foreach (object project in projects)
            if (ReadProjectId(project) == apolloId) return project;
        return null;
    }

    internal static int ReadProjectId(object project)
    {
        if (project == null) return -1;
        foreach (string name in new[] { "apollo_id", "ApolloId", "project_id", "ProjectId", "id", "Id" })
        {
            int value = ReadInt(ReadMember(project.GetType(), project, name, false), -1);
            if (value > 0) return value;
        }
        string path = Convert.ToString(ReadMember(project.GetType(), project, "ProjectPath", false)) ?? "";
        Match match = Regex.Match(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            "(?:^|[\\\\/])(\\d+)$"
        );
        return match.Success ? ReadInt(match.Groups[1].Value, -1) : -1;
    }

    internal static object ReadMember(Type type, object target, string name, bool isStatic)
    {
        if (type == null || (!isStatic && target == null)) return null;
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy |
            (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        try
        {
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(target, null);
            FieldInfo field = type.GetField(name, flags);
            return field == null ? null : field.GetValue(target);
        }
        catch { return null; }
    }

    internal static int ReadInt(object value, int fallback)
    {
        int result;
        return value != null && int.TryParse(Convert.ToString(value), out result)
            ? result
            : fallback;
    }

    internal static string EscapeJson(string value)
    {
        if (value == null) return "";
        StringBuilder escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\b': escaped.Append("\\b"); break;
                case '\f': escaped.Append("\\f"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (character < 0x20)
                        escaped.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        escaped.Append(character);
                    break;
            }
        }
        return escaped.ToString();
    }
}