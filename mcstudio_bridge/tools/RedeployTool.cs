using System;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("1.1.0.0")]

[McpTool("redeploy", "Redeploy an Apollo project through MCStudio. Requires explicit confirmation.")]
public class RedeployTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Apollo project ID\"},\"confirmation\":{\"type\":\"string\",\"description\":\"Must be exactly 'REDEPLOY <apollo_id>'\"}},\"required\":[\"apollo_id\",\"confirmation\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";
            string requiredConfirmation = "REDEPLOY " + apolloId;
            string confirmation = ApolloToolHelpers.ExtractJsonString(argumentsJson, "confirmation");
            if (confirmation != requiredConfirmation)
                return "{\"error\":\"Invalid confirmation. Must be exactly '" + requiredConfirmation + "'.\"}";

            Assembly mcsAsm = ApolloToolHelpers.FindAssembly("MCStudio");
            if (mcsAsm == null) return "{\"error\":\"MCStudio not found\"}";

            Type projectType = mcsAsm.GetType("MCStudio.Model.Apollo.ApolloProject");
            object project = ApolloToolHelpers.FindProject(mcsAsm, apolloId);
            if (project == null) return "{\"error\":\"Apollo project " + apolloId + " not found\"}";

            bool deployable = (bool)projectType.GetProperty("Deployable").GetValue(project, null);
            if (!deployable) return "{\"error\":\"Project is not deployable\"}";
            bool isDeploying = (bool)projectType.GetProperty("IsDeploying").GetValue(project, null);
            if (isDeploying) return "{\"error\":\"Already deploying\"}";

            Type viewModelType;
            object viewModel = ApolloToolHelpers.ResolveProjectListViewModel(mcsAsm, out viewModelType);
            if (viewModel == null) return "{\"error\":\"ViewModel not resolved\"}";

            MethodInfo redeployMethod = viewModelType.GetMethod("RedeployProject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (redeployMethod == null) return "{\"error\":\"RedeployProject not found\"}";

            object selectedProject = project;
            object selectedViewModel = viewModel;
            Action deployAction = () => redeployMethod.Invoke(selectedViewModel, new object[] { selectedProject, true });
            if (!ApolloToolHelpers.QueueOnUi(deployAction))
                return "{\"error\":\"MCStudio UI dispatcher unavailable\"}";

            return "{\"status\":\"request_queued\",\"apollo_id\":" + apolloId + "}";
        }
        catch (Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) message += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(message) + "\"}";
        }
    }
}