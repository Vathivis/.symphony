using Scriban;
using Scriban.Runtime;
using Symphony.Domain;

namespace Symphony.Configuration;

public sealed class WorkflowTemplateRenderer {
	public string Render(WorkflowDefinition definition, IssueRecord issue, int? attempt) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(issue);

		var templateText = string.IsNullOrWhiteSpace(definition.PromptTemplate)
			? "You are working on an issue from Linear."
			: definition.PromptTemplate;

		var template = Template.ParseLiquid(templateText);
		if (template.HasErrors) {
			throw new WorkflowLoadException(
				"template_parse_error",
				string.Join("; ", template.Messages.Select(message => message.Message)));
		}

		var globals = new ScriptObject();
		globals.Add("issue", ToTemplateIssue(issue));
		globals.Add("attempt", attempt);

		var context = new TemplateContext {
			StrictVariables = true,
			EnableRelaxedFunctionAccess = false,
			EnableRelaxedIndexerAccess = false,
			EnableRelaxedMemberAccess = false,
			LoopLimit = 10_000
		};
		context.PushGlobal(globals);

		try {
			return template.Render(context).Trim();
		} catch (Exception exception) {
			throw new WorkflowLoadException(
				"template_render_error",
				exception.Message,
				exception);
		}
	}

	private static ScriptObject ToTemplateIssue(IssueRecord issue) {
		var scriptObject = new ScriptObject();
		scriptObject.Add("id", issue.Id);
		scriptObject.Add("identifier", issue.Identifier);
		scriptObject.Add("title", issue.Title);
		scriptObject.Add("description", issue.Description);
		scriptObject.Add("priority", issue.Priority);
		scriptObject.Add("state", issue.State);
		scriptObject.Add("branch_name", issue.BranchName);
		scriptObject.Add("url", issue.Url);
		scriptObject.Add("labels", issue.Labels.ToArray());
		scriptObject.Add("blocked_by", issue.BlockedBy.Select(blocker => new ScriptObject {
			["id"] = blocker.Id,
			["identifier"] = blocker.Identifier,
			["state"] = blocker.State
		}).ToArray());
		scriptObject.Add("created_at", issue.CreatedAt?.ToString("O"));
		scriptObject.Add("updated_at", issue.UpdatedAt?.ToString("O"));
		return scriptObject;
	}
}
