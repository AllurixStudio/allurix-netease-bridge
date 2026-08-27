using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using MC.Mcp;

[assembly: AssemblyVersion("1.3.0.0")]

[McpTool("logs", "Fetch Apollo server logs in-process. Read-only.")]
public class LogsTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"description\":\"Apollo project ID\"},\"server_id\":{\"type\":\"integer\",\"description\":\"Server node ID from the Apollo project\"},\"lines\":{\"type\":\"integer\",\"description\":\"Lines to fetch (negative=from end, default -200)\"},\"offset\":{\"type\":\"integer\",\"description\":\"Offset (default -1 for the latest window)\"}},\"required\":[\"apollo_id\",\"server_id\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";
            int serverId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "server_id", -1);
            if (serverId < 0) return "{\"error\":\"Missing server_id\"}";
            int lines = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "lines", -200);
            int offset = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "offset", -1);

            Assembly mcsAsm = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "MCStudio") { mcsAsm = a; break; }
            if (mcsAsm == null) return "{\"error\":\"MCStudio not found\"}";

            Type projType = mcsAsm.GetType("MCStudio.Model.Apollo.ApolloProject");
            Type serverType = mcsAsm.GetType("MCStudio.Model.Apollo.ApolloServer");
            object project = ApolloToolHelpers.FindProject(mcsAsm, apolloId);
            if (project == null) return "{\"error\":\"Apollo project " + apolloId + " not found\"}";

            object server = FindProjectServer(project, projType, serverType, serverId);
            if (server == null) return "{\"error\":\"Server " + serverId + " not found in Apollo project " + apolloId + "\"}";

            // Call FetchApolloLog
            Type apiType = mcsAsm.GetType("MCStudio.Modules.Apollo.ApolloApi");
            MethodInfo fetchMethod = apiType.GetMethod("FetchApolloLog",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            ParameterInfo[] parms = fetchMethod.GetParameters();
            Type callbackType = parms[4].ParameterType;
            Type responseType = callbackType.GetGenericArguments()[0];

            Type helperType = typeof(CallbackHelper<>).MakeGenericType(responseType);
            object helper = Activator.CreateInstance(helperType);
            ManualResetEvent done = new ManualResetEvent(false);
            helperType.GetField("Done").SetValue(helper, done);
            Delegate callback = Delegate.CreateDelegate(callbackType, helper,
                helperType.GetMethod("OnResult"));

            // Runtime signature: FetchApolloLog(project, server, offset, len, callback).
            fetchMethod.Invoke(null, new object[] { project, server, offset, lines, callback });

            if (!done.WaitOne(15000))
                return "{\"error\":\"Timeout (15s)\"}";

            object resp = helperType.GetField("Result").GetValue(helper);
            if (resp == null) return "{\"error\":\"Null response\"}";

            // Response has: entity (ApolloLogEntity), statusCode, code, message
            // ApolloLogEntity has: content, offset, len, etc.
            string content = "";
            string respOffset = "0";
            string respLen = "0";
            string code = "0";
            string message = "";

            // Get code and message from response
            PropertyInfo codeProp = resp.GetType().GetProperty("code");
            if (codeProp != null) { object v = codeProp.GetValue(resp, null); if (v != null) code = v.ToString(); }
            PropertyInfo msgProp = resp.GetType().GetProperty("message");
            if (msgProp != null) { object v = msgProp.GetValue(resp, null); if (v != null) message = v.ToString(); }

            // Get entity
            PropertyInfo entityProp = resp.GetType().GetProperty("entity");
            if (entityProp != null)
            {
                object entity = entityProp.GetValue(resp, null);
                if (entity != null)
                {
                    // ApolloLogEntity has FIELDS (not properties): content (List), offset (int), len (int)
                    FieldInfo contentField = entity.GetType().GetField("content",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo offsetField = entity.GetType().GetField("offset",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo lenField = entity.GetType().GetField("len",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (offsetField != null)
                    {
                        object ov = offsetField.GetValue(entity);
                        if (ov != null) respOffset = ov.ToString();
                    }
                    if (lenField != null)
                    {
                        object lv = lenField.GetValue(entity);
                        if (lv != null) respLen = lv.ToString();
                    }
                    if (contentField != null)
                    {
                        object cv = contentField.GetValue(entity);
                        if (cv != null)
                        {
                            // content is List<string> - join with newlines
                            IEnumerable items = cv as IEnumerable;
                            if (items != null)
                            {
                                string joined = "";
                                foreach (object item in items)
                                {
                                    if (item != null)
                                    {
                                        if (joined.Length > 0) joined += "\n";
                                        joined += item.ToString();
                                    }
                                }
                                content = joined;
                            }
                            else
                            {
                                content = cv.ToString();
                            }
                        }
                    }
                }
            }

            return "{\"apollo_id\":" + apolloId +
                   ",\"server_id\":" + serverId +
                   ",\"code\":" + code +
                   ",\"message\":\"" + ApolloToolHelpers.EscapeJson(message) + "\"" +
                   ",\"offset\":" + respOffset +
                   ",\"len\":" + respLen +
                   ",\"content\":\"" + ApolloToolHelpers.EscapeJson(content) + "\"}";
        }
        catch (Exception ex)
        {
            string msg = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(msg) + "\"}";
        }
    }

    private static object FindProjectServer(
        object project,
        Type projectType,
        Type serverType,
        int serverId)
    {
        string[] collectionNames = {
            "MasterList", "ServiceList", "LobbyList", "GameList", "ProxyList"
        };
        PropertyInfo serverIdProperty = serverType.GetProperty("serverid",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (string collectionName in collectionNames)
        {
            PropertyInfo collectionProperty = projectType.GetProperty(collectionName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (collectionProperty == null) continue;
            IEnumerable collection = collectionProperty.GetValue(project, null) as IEnumerable;
            if (collection == null) continue;
            foreach (object candidate in collection)
            {
                if (candidate == null || !serverType.IsInstanceOfType(candidate)) continue;
                object value = serverIdProperty.GetValue(candidate, null);
                if (value is int && (int)value == serverId) return candidate;
            }
        }
        return null;
    }

}

public class CallbackHelper<T>
{
    public ManualResetEvent Done;
    public object Result;
    public void OnResult(T response)
    {
        Result = response;
        if (Done != null) Done.Set();
    }
}
