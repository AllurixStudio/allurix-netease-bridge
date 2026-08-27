using System;
using System.Collections;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("1.2.0.0")]

/// <summary>
/// Fetches deployment activity logs for the requested project.
///
/// Strategy:
/// 1. ApolloProject.LogList (the list bound to MCStudio's deployment-log UI)
/// 2. GetActLog(project) → List (only has data during active deployment)
/// 3. GetAutoDeployLog(project) → AutoDeployLogs.entity (List, historical)
/// 4. If all are empty, reports "no active deployment"
///
/// Note: Deploy logs are ephemeral. They only contain data during or shortly
/// after a deployment action. When ActId=0, no deployment is in progress.
/// </summary>
[McpTool("deploy_logs", "Fetch deployment activity logs for an Apollo project.")]
public class DeployLogsTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Apollo project ID\"}},\"required\":[\"apollo_id\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";

            Assembly mcsAsm = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "MCStudio") { mcsAsm = a; break; }
            if (mcsAsm == null) return "{\"error\":\"MCStudio not found\"}";

            Type projType = mcsAsm.GetType("MCStudio.Model.Apollo.ApolloProject");
            object project = ApolloToolHelpers.FindProject(mcsAsm, apolloId);
            if (project == null) return "{\"error\":\"Apollo project " + apolloId + " not found\"}";

            // Get deployment state
            int actId = 0;
            bool isDeploying = false;
            try { actId = (int)projType.GetProperty("ActId").GetValue(project, null); } catch { }
            try { isDeploying = (bool)projType.GetProperty("IsDeploying").GetValue(project, null); } catch { }

            Type apiType = mcsAsm.GetType("MCStudio.Modules.Apollo.ApolloApi");

            // Strategy 1: the observable list bound to MCStudio's deployment-log UI.
            PropertyInfo projectLogList = projType.GetProperty("LogList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo projectLogListField = projType.GetField("LogList",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object projectLogs = null;
            if (projectLogList != null)
                projectLogs = projectLogList.GetValue(project, null);
            else if (projectLogListField != null)
                projectLogs = projectLogListField.GetValue(project);
            if (projectLogs != null)
            {
                string logs = TrySerializeEnumerable(projectLogs);
                if (logs != null)
                    return "{\"logs\":" + logs + ",\"source\":\"project_log_list\",\"act_id\":" + actId + ",\"is_deploying\":" + (isDeploying ? "true" : "false") + "}";
            }

            // Strategy 2: GetActLog (active deployment logs)
            MethodInfo getActLog = apiType.GetMethod("GetActLog",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getActLog != null)
            {
                object actResult = getActLog.Invoke(null, new object[] { project });
                string logs = TrySerializeEnumerable(actResult);
                if (logs != null)
                    return "{\"logs\":" + logs + ",\"source\":\"act_log\",\"act_id\":" + actId + ",\"is_deploying\":" + (isDeploying ? "true" : "false") + "}";
            }

            // Strategy 3: GetAutoDeployLog → entity field
            MethodInfo getAutoLog = apiType.GetMethod("GetAutoDeployLog",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getAutoLog != null)
            {
                object autoResult = getAutoLog.Invoke(null, new object[] { project });
                if (autoResult != null)
                {
                    // Read the 'entity' field (List<T>)
                    FieldInfo entityField = autoResult.GetType().GetField("entity",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (entityField != null)
                    {
                        object entity = entityField.GetValue(autoResult);
                        string logs = TrySerializeEnumerable(entity);
                        if (logs != null)
                            return "{\"logs\":" + logs + ",\"source\":\"auto_deploy_log\",\"act_id\":" + actId + ",\"is_deploying\":" + (isDeploying ? "true" : "false") + "}";
                    }

                    // Also try property
                    PropertyInfo entityProp = autoResult.GetType().GetProperty("entity",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (entityProp != null)
                    {
                        object entity = entityProp.GetValue(autoResult, null);
                        string logs = TrySerializeEnumerable(entity);
                        if (logs != null)
                            return "{\"logs\":" + logs + ",\"source\":\"auto_deploy_log\",\"act_id\":" + actId + ",\"is_deploying\":" + (isDeploying ? "true" : "false") + "}";
                    }
                }
            }

            // No logs available
            return "{\"logs\":[],\"source\":\"none\",\"act_id\":" + actId + ",\"is_deploying\":" + (isDeploying ? "true" : "false") + ",\"message\":\"No active deployment. Deploy logs are only available during or shortly after a deployment.\"}";
        }
        catch (Exception ex)
        {
            string msg = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(msg) + "\"}";
        }
    }

    /// <summary>Returns a JSON array string if items is non-null and has items; null otherwise.</summary>
    private string TrySerializeEnumerable(object items)
    {
        if (items == null) return null;
        if (items is string) return null;

        IEnumerable enumerable = items as IEnumerable;
        if (enumerable == null) return null;

        string json = "[";
        bool first = true;
        bool hasAny = false;

        foreach (object item in enumerable)
        {
            if (item == null) continue;
            hasAny = true;
            if (!first) json += ",";
            first = false;

            Type itemType = item.GetType();

            if (itemType == typeof(string))
            {
                json += "\"" + ApolloToolHelpers.EscapeJson((string)item) + "\"";
                continue;
            }
            if (itemType.IsPrimitive || itemType == typeof(DateTime) || itemType == typeof(decimal))
            {
                json += "\"" + ApolloToolHelpers.EscapeJson(item.ToString()) + "\"";
                continue;
            }

            // Serialize as object
            json += "{";
            bool firstMember = true;

            foreach (PropertyInfo prop in itemType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object val = prop.GetValue(item, null);
                    if (val == null) continue;
                    string vs = val.ToString();
                    if (vs == val.GetType().FullName) continue;
                    if (!firstMember) json += ",";
                    firstMember = false;
                    if (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(long) ||
                        prop.PropertyType == typeof(double) || prop.PropertyType == typeof(bool))
                        json += "\"" + prop.Name + "\":" + vs.ToLower();
                    else
                        json += "\"" + prop.Name + "\":\"" + ApolloToolHelpers.EscapeJson(vs) + "\"";
                }
                catch { }
            }
            foreach (FieldInfo field in itemType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    object val = field.GetValue(item);
                    if (val == null) continue;
                    string vs = val.ToString();
                    if (vs == val.GetType().FullName) continue;
                    if (!firstMember) json += ",";
                    firstMember = false;
                    if (field.FieldType == typeof(int) || field.FieldType == typeof(long) ||
                        field.FieldType == typeof(double) || field.FieldType == typeof(bool))
                        json += "\"" + field.Name + "\":" + vs.ToLower();
                    else
                        json += "\"" + field.Name + "\":\"" + ApolloToolHelpers.EscapeJson(vs) + "\"";
                }
                catch { }
            }
            json += "}";
        }
        json += "]";

        if (!hasAny) return null;
        return json;
    }

}
