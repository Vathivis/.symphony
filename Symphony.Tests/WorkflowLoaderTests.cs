using Symphony.Configuration;

namespace Symphony.Tests;

public sealed class WorkflowLoaderTests {
	[Fact]
	public void Load_ParsesFrontMatter_UsesDefaults_AndFallsBackToCanonicalLinearSettings() {
		var originalApiKey = Environment.GetEnvironmentVariable("LINEAR_API_KEY");
		var originalProjectSlug = Environment.GetEnvironmentVariable("LINEAR_PROJECT_SLUG");
		var workflowDirectory = Directory.CreateTempSubdirectory();
		var workflowPath = Path.Combine(workflowDirectory.FullName, "WORKFLOW.md");

		try {
			Environment.SetEnvironmentVariable("LINEAR_API_KEY", "test-linear-api-key");
			Environment.SetEnvironmentVariable("LINEAR_PROJECT_SLUG", "test-project-slug");
			File.WriteAllText(workflowPath, """
				---
				tracker:
				  kind: linear
				agent:
				  max_turns: 5
				---
				Hello {{ issue.identifier }}
				""");

			var loader = new WorkflowLoader();
			var workflow = loader.Load(workflowPath, new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero));

			Assert.True(workflow.Validation.IsValid);
			Assert.Equal("test-linear-api-key", workflow.Config.Tracker.ApiKey);
			Assert.Equal("test-project-slug", workflow.Config.Tracker.ProjectSlug);
			Assert.Equal(5, workflow.Config.Agent.MaxTurns);
			Assert.Contains("Todo", workflow.Config.Tracker.ActiveStates);
			Assert.Equal("Hello {{ issue.identifier }}", workflow.Definition.PromptTemplate);
		} finally {
			Environment.SetEnvironmentVariable("LINEAR_API_KEY", originalApiKey);
			Environment.SetEnvironmentVariable("LINEAR_PROJECT_SLUG", originalProjectSlug);
			workflowDirectory.Delete(recursive: true);
		}
	}

	[Fact]
	public void Load_ThrowsMissingWorkflowFileErrorForUnknownPath() {
		var loader = new WorkflowLoader();
		var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");

		var exception = Assert.Throws<WorkflowLoadException>(() =>
			loader.Load(path, new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero)));

		Assert.Equal("missing_workflow_file", exception.Code);
	}
}
