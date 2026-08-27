using System;
using System.Reflection;
using MC.Mcp;

[assembly: AssemblyVersion("0.2.0.0")]

[McpTool("development_test", "Start an Apollo project development test through MCStudio's native TestCommand. Requires explicit confirmation.")]
public class DevelopmentTestTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get
        {
            return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Apollo project ID\"},\"confirmation\":{\"type\":\"string\",\"description\":\"Must be exactly 'DEVELOPMENT TEST <apollo_id>'\"}},\"required\":[\"apollo_id\",\"confirmation\"]}";
        }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";
            string requiredConfirmation = "DEVELOPMENT TEST " + apolloId;
            if (ApolloToolHelpers.ExtractJsonString(argumentsJson, "confirmation") != requiredConfirmation)
                return "{\"error\":\"Invalid confirmation. Must be exactly '" + requiredConfirmation + "'.\"}";

            Assembly mcs = ApolloToolHelpers.FindAssembly("MCStudio");
            if (mcs == null) return "{\"error\":\"MCStudio unavailable\"}";

            object project = ApolloToolHelpers.FindProject(mcs, apolloId);
            if (project == null) return "{\"error\":\"Apollo project " + apolloId + " not found\"}";

            Type viewModelType;
            object viewModel = ApolloToolHelpers.ResolveProjectListViewModel(mcs, out viewModelType);
            if (viewModel == null) return "{\"error\":\"Apollo ViewModel unavailable\"}";
            object testCommand = viewModelType.GetProperty("TestCommand", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(viewModel, null);
            if (testCommand == null) return "{\"error\":\"MCStudio TestCommand unavailable\"}";

            Action startTest = () => ExecuteCommand(testCommand, project);
            if (!ApolloToolHelpers.QueueOnUi(startTest))
                return "{\"error\":\"MCStudio UI dispatcher unavailable\"}";
            return "{\"status\":\"request_queued\",\"apollo_id\":" + apolloId + ",\"action\":\"development_test\"}";
        }
        catch (Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null) message += " | " + ex.InnerException.Message;
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(message) + "\"}";
        }
    }


    private static void ExecuteCommand(object command, object parameter)
    {
        MethodInfo canExecute = FindCommandMethod(command, "CanExecute", parameter);
        if (canExecute != null && !(bool)canExecute.Invoke(command, new object[] { parameter }))
            throw new InvalidOperationException("MCStudio development test is currently unavailable.");
        MethodInfo execute = FindCommandMethod(command, "Execute", parameter);
        if (execute == null) throw new MissingMethodException(command.GetType().FullName, "Execute");
        execute.Invoke(command, new object[] { parameter });
    }

    private static MethodInfo FindCommandMethod(object command, string name, object parameter)
    {
        foreach (MethodInfo method in command.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == name && parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(parameter)) return method;
        }
        return null;
    }
}