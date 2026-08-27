using System;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("1.1.0.0")]

[McpTool("hotfix", "Run a server or client hotfix for an Apollo project. Requires explicit confirmation.")]
public class HotfixTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Apollo project ID\"},\"confirmation\":{\"type\":\"string\",\"description\":\"Must be exactly 'HOTFIX <apollo_id>'\"},\"client\":{\"type\":\"boolean\",\"description\":\"Client hotfix instead of server (default false)\"}},\"required\":[\"apollo_id\",\"confirmation\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";
            string requiredConfirmation = "HOTFIX " + apolloId;
            string confirmation = ApolloToolHelpers.ExtractJsonString(argumentsJson, "confirmation");
            if (confirmation != requiredConfirmation)
                return "{\"error\":\"Invalid confirmation. Must be exactly '" + requiredConfirmation + "'.\"}";

            bool isClient = ApolloToolHelpers.ExtractJsonBool(argumentsJson, "client", false);
            Assembly mcsAsm = ApolloToolHelpers.FindAssembly("MCStudio");
            if (mcsAsm == null) return "{\"error\":\"MCStudio not found\"}";

            object project = ApolloToolHelpers.FindProject(mcsAsm, apolloId);
            if (project == null) return "{\"error\":\"Apollo project " + apolloId + " not found\"}";

            Type viewModelType;
            object viewModel = ApolloToolHelpers.ResolveProjectListViewModel(mcsAsm, out viewModelType);
            if (viewModel == null) return "{\"error\":\"ViewModel not resolved\"}";

            string methodName = isClient ? "HotfixClientProject" : "HotfixProject";
            MethodInfo hotfixMethod = viewModelType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (hotfixMethod == null) return "{\"error\":\"" + methodName + " not found\"}";

            object selectedViewModel = viewModel;
            object selectedProject = project;
            Action hotfixAction = () => hotfixMethod.Invoke(selectedViewModel, new object[] { selectedProject });
            if (!ApolloToolHelpers.QueueOnUi(hotfixAction))
                return "{\"error\":\"MCStudio UI dispatcher unavailable\"}";

            string mode = isClient ? "client" : "server";
            return "{\"status\":\"request_queued\",\"apollo_id\":" + apolloId + ",\"action\":\"hotfix_" + mode + "\"}";
        }
        catch (Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) message += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(message) + "\"}";
        }
    }
}