using Symphony.Domain;

namespace Symphony.Tracker;

public interface IIssueTrackerClient {
	Task<IReadOnlyList<IssueRecord>> FetchCandidateIssuesAsync(CancellationToken cancellationToken);
	Task<IReadOnlyList<IssueRecord>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken cancellationToken);
	Task<IReadOnlyList<IssueRecord>> FetchIssueStatesByIdsAsync(IReadOnlyList<string> issueIds, CancellationToken cancellationToken);
}
