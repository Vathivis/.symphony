using Symphony.Domain;
using Symphony.Orchestration;

namespace Symphony.Tests;

public sealed class SymphonyOrchestratorTests {
	[Fact]
	public void SortIssuesForDispatch_OrdersByPriorityThenOldestCreationThenIdentifier() {
		var issues = new[] {
			CreateIssue("ABC-3", priority: 2, createdAt: "2026-03-05T12:00:00Z"),
			CreateIssue("ABC-2", priority: 1, createdAt: "2026-03-05T13:00:00Z"),
			CreateIssue("ABC-1", priority: 1, createdAt: "2026-03-05T10:00:00Z"),
		};

		var sorted = SymphonyOrchestrator.SortIssuesForDispatch(issues);

		Assert.Equal(["ABC-1", "ABC-2", "ABC-3"], sorted.Select(issue => issue.Identifier).ToArray());
	}

	private static IssueRecord CreateIssue(string identifier, int? priority, string createdAt) {
		return new IssueRecord(
			identifier.ToLowerInvariant(),
			identifier,
			identifier + " title",
			null,
			priority,
			"Todo",
			null,
			null,
			[],
			[],
			DateTimeOffset.Parse(createdAt),
			DateTimeOffset.Parse(createdAt));
	}
}
