using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using MC.Mcp;

[assembly: System.Reflection.AssemblyVersion("0.2.0.0")]

[McpTool("confirm_redeploy", "Confirm the visible MCStudio redeploy dialog through Windows UI Automation. Does not use mouse or keyboard.")]
public class ConfirmRedeployTool : McpToolBase
{
    public override string InputSchemaJson
    {
        get { return "{\"type\":\"object\",\"properties\":{\"apollo_id\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Apollo project ID\"},\"confirmation\":{\"type\":\"string\",\"description\":\"Must be exactly 'CONFIRM REDEPLOY <apollo_id>'\"}},\"required\":[\"apollo_id\",\"confirmation\"]}"; }
    }

    public override string Execute(string argumentsJson, McpContext context)
    {
        try
        {
            int apolloId = ApolloToolHelpers.ExtractJsonInt(argumentsJson, "apollo_id", -1);
            if (apolloId <= 0) return "{\"error\":\"Missing apollo_id\"}";
            string required = "CONFIRM REDEPLOY " + apolloId;
            if (ApolloToolHelpers.ExtractJsonString(argumentsJson, "confirmation") != required)
                return "{\"error\":\"Invalid confirmation. Must be exactly '" + required + "'.\"}";

            AutomationElement button = FindRedeployConfirmButton();
            if (button == null) return "{\"error\":\"No visible redeploy confirmation button found\"}";
            object pattern;
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                return "{\"error\":\"Redeploy confirmation button has no InvokePattern\"}";
            ((InvokePattern)pattern).Invoke();
            return "{\"status\":\"confirmed\",\"apollo_id\":" + apolloId +
                ",\"mechanism\":\"windows_ui_automation\",\"process_id\":" + Process.GetCurrentProcess().Id + "}";
        }
        catch (Exception ex)
        {
            return "{\"error\":\"" + ApolloToolHelpers.EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}";
        }
    }

    private static AutomationElement FindRedeployConfirmButton()
    {
        int processId = Process.GetCurrentProcess().Id;
        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        List<AutomationElement> matches = new List<AutomationElement>();
        foreach (AutomationElement window in windows)
        {
            try
            {
                if (window.Current.ProcessId != processId ||
                    !string.Equals(window.Current.Name, "MC Studio", StringComparison.OrdinalIgnoreCase)) continue;
                string text = GetSubtreeText(window);
                if (text.IndexOf("提示", StringComparison.Ordinal) < 0) continue;
                AutomationElementCollection confirms = FindVisibleButtons(window, "确定");
                AutomationElementCollection cancels = FindVisibleButtons(window, "取消");
                if (confirms.Count == 1 && cancels.Count == 1) matches.Add(confirms[0]);
            }
            catch (ElementNotAvailableException) { }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static AutomationElementCollection FindVisibleButtons(AutomationElement root, string name)
    {
        return root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
    }

    private static string GetSubtreeText(AutomationElement root)
    {
        string text = root.Current.Name ?? "";
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement element in all)
        {
            try
            {
                string name = element.Current.Name;
                if (!string.IsNullOrEmpty(name)) text += " " + name;
            }
            catch (ElementNotAvailableException) { }
        }
        return text;
    }
}
