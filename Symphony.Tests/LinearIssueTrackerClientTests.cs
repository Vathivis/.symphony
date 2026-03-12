using Symphony.Tracker;

namespace Symphony.Tests;

public sealed class LinearIssueTrackerClientTests {
	[Fact]
	public void CandidateIssuesQuery_UsesProjectSlugIdFilterRequiredBySpec() {
		Assert.Contains(
			"project: { slugId: { eq: $projectSlug } }",
			LinearIssueTrackerClient.CandidateIssuesQuery,
			StringComparison.Ordinal);
	}

	[Fact]
	public void IssueStatesByIdsQuery_UsesGraphQlIdListTypeRequiredBySpec() {
		Assert.Contains(
			"$ids: [ID!]",
			LinearIssueTrackerClient.IssueStatesByIdsQuery,
			StringComparison.Ordinal);
	}
}
