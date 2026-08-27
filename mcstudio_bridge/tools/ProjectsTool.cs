using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MC.Mcp;

[assembly: AssemblyVersion("0.1.3.0")]

[McpTool("projects", "List Apollo projects and their current server nodes from MCStudio. Read-only.")]
public class ProjectsTool : McpToolBase
{
    private static readonly string[] NodeLists = {
        "LobbyList", "GameList", "MasterList", "ServiceList", "ProxyList"
    };

    public override string InputSchemaJson
    {
        get { return "{\"type\":\"object\",\"properties\":{}}"; }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            Assembly mcs = ApolloToolHelpers.FindAssembly("MCStudio");
            if (mcs == null) return "{\"error\":\"MCStudio assembly unavailable\"}";
            Type managerType = mcs.GetType("MCStudio.Modules.Apollo.ApolloProjectManager");
            if (managerType == null) return "{\"error\":\"ApolloProjectManager type unavailable\"}";
            object manager = ApolloToolHelpers.ReadMember(managerType, null, "Instance", true);
            if (manager == null) return "{\"error\":\"ApolloProjectManager.Instance unavailable\"}";
            IEnumerable projects = ApolloToolHelpers.ReadMember(
                manager.GetType(),
                manager,
                "ApolloProjectList",
                false
            ) as IEnumerable;
            if (projects == null)
                return "{\"error\":\"ApolloProjectList unavailable on " +
                    ApolloToolHelpers.EscapeJson(manager.GetType().FullName) + "\"}";

            StringBuilder json = new StringBuilder("{\"projects\":[");
            bool firstProject = true;
            foreach (object project in projects)
            {
                int projectId = ApolloToolHelpers.ReadProjectId(project);
                if (project == null || projectId <= 0) continue;
                if (!firstProject) json.Append(',');
                firstProject = false;

                string path = Convert.ToString(ApolloToolHelpers.ReadMember(
                    project.GetType(),
                    project,
                    "ProjectPath",
                    false
                ));
                string name = FirstString(project, "DisplayName", "ProjectName", "Name");
                if (string.IsNullOrEmpty(name))
                    name = Path.GetFileName((path ?? "").TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    ));
                if (string.IsNullOrEmpty(name)) name = "Apollo " + projectId;

                json.Append("{\"id\":").Append(projectId)
                    .Append(",\"name\":\"").Append(ApolloToolHelpers.EscapeJson(name))
                    .Append("\",\"nodes\":[");
                AppendNodes(json, project);
                json.Append("]}");
            }
            return json.Append("]}").ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(
                ex.GetType().Name + ": " + ex.Message
            ) + "\"}";
        }
    }

    private static void AppendNodes(StringBuilder json, object project)
    {
        bool first = true;
        HashSet<int> seen = new HashSet<int>();
        List<string> listNames = new List<string>(NodeLists);

        foreach (PropertyInfo property in project.GetType().GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        ))
        {
            if (property.Name.EndsWith("List", StringComparison.Ordinal) &&
                !listNames.Contains(property.Name))
                listNames.Add(property.Name);
        }
        foreach (FieldInfo field in project.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        ))
        {
            if (field.Name.EndsWith("List", StringComparison.Ordinal) &&
                !listNames.Contains(field.Name))
                listNames.Add(field.Name);
        }

        foreach (string listName in listNames)
        {
            IEnumerable nodes = ApolloToolHelpers.ReadMember(
                project.GetType(),
                project,
                listName,
                false
            ) as IEnumerable;
            if (nodes == null) continue;

            string fallbackType = listName.Substring(0, listName.Length - 4)
                .ToLowerInvariant();
            foreach (object node in nodes)
            {
                int id = ApolloToolHelpers.ReadInt(ApolloToolHelpers.ReadMember(
                    node == null ? null : node.GetType(),
                    node,
                    "serverid",
                    false
                ), -1);
                if (id < 0 || !seen.Add(id)) continue;

                string type = FirstString(node, "type");
                if (string.IsNullOrEmpty(type)) type = fallbackType;
                string name = FirstString(node, "DisplayName", "FullDisplayName", "Name");
                if (string.IsNullOrEmpty(name)) name = type + " " + id;

                if (!first) json.Append(',');
                first = false;
                json.Append("{\"id\":").Append(id)
                    .Append(",\"name\":\"").Append(ApolloToolHelpers.EscapeJson(name))
                    .Append("\",\"type\":\"").Append(ApolloToolHelpers.EscapeJson(type))
                    .Append("\"}");
            }
        }
    }

    private static string FirstString(object target, params string[] names)
    {
        if (target == null) return "";
        foreach (string name in names)
        {
            string value = Convert.ToString(ApolloToolHelpers.ReadMember(
                target.GetType(),
                target,
                name,
                false
            ));
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return "";
    }
}