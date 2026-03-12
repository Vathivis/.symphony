using Symphony.Configuration;
using Symphony.Domain;

namespace Symphony.Tests;

public sealed class WorkflowTemplateRendererTests {
	[Fact]
	public void Render_ThrowsTemplateRenderErrorForUnknownVariable() {
		var renderer = new WorkflowTemplateRenderer();
		var definition = new WorkflowDefinition(
			new Dictionary<string, object?>(),
			"Hello {{ issue.missing_field }}");

		var exception = Assert.Throws<WorkflowLoadException>(() =>
			renderer.Render(definition, CreateIssue(), attempt: null));

		Assert.Equal("template_render_error", exception.Code);
	}

	private static IssueRecord CreateIssue() {
		return new IssueRecord(
			"issue-1",
			"ABC-123",
			"Investigate failure",
			"Description",
			2,
			"Todo",
			"abc-123-branch",
			"https://linear.app/issue/ABC-123",
			["bug"],
			[],
			DateTimeOffset.Parse("2026-03-05T10:00:00Z"),
			DateTimeOffset.Parse("2026-03-05T11:00:00Z"));
	}
}
