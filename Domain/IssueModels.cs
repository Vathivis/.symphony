namespace Symphony.Domain;

public sealed record BlockerReference(
	string? Id,
	string? Identifier,
	string? State);

public sealed record IssueRecord(
	string Id,
	string Identifier,
	string Title,
	string? Description,
	int? Priority,
	string State,
	string? BranchName,
	string? Url,
	IReadOnlyList<string> Labels,
	IReadOnlyList<BlockerReference> BlockedBy,
	DateTimeOffset? CreatedAt,
	DateTimeOffset? UpdatedAt);

public sealed record WorkflowDefinition(
	IReadOnlyDictionary<string, object?> Config,
	string PromptTemplate);

public sealed record WorkspaceDescriptor(
	string Path,
	string WorkspaceKey,
	bool CreatedNow);

public sealed record CodexTokenUsage(
	long InputTokens,
	long OutputTokens,
	long TotalTokens);

public sealed record CodexRuntimeEvent(
	string Event,
	DateTimeOffset Timestamp,
	int? CodexAppServerPid,
	string? Message = null,
	string? SessionId = null,
	string? ThreadId = null,
	string? TurnId = null,
	CodexTokenUsage? Usage = null,
	System.Text.Json.Nodes.JsonNode? RateLimits = null);

public enum WorkerExitReason {
	Normal,
	Failed,
	TimedOut,
	Stalled,
	CanceledByReconciliation,
	HostStopping
}

public sealed record WorkerCompletion(
	WorkerExitReason Reason,
	string WorkspacePath,
	string? Error,
	int TurnCount);
