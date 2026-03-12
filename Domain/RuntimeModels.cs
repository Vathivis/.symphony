using System.Text.Json.Nodes;

namespace Symphony.Domain;

public sealed record RuntimeEventRecord(
	DateTimeOffset At,
	string Event,
	string? Message);

public sealed record RuntimeCounts(
	int Running,
	int Retrying);

public sealed record RuntimeTotals(
	long InputTokens,
	long OutputTokens,
	long TotalTokens,
	double SecondsRunning);

public sealed record RunningIssueSnapshot(
	string IssueId,
	string IssueIdentifier,
	string State,
	string? SessionId,
	int TurnCount,
	string? LastEvent,
	string? LastMessage,
	DateTimeOffset StartedAt,
	DateTimeOffset? LastEventAt,
	CodexTokenUsage Tokens);

public sealed record RetryIssueSnapshot(
	string IssueId,
	string IssueIdentifier,
	int Attempt,
	DateTimeOffset DueAt,
	string? Error);

public sealed record RuntimeSnapshot(
	DateTimeOffset GeneratedAt,
	RuntimeCounts Counts,
	IReadOnlyList<RunningIssueSnapshot> Running,
	IReadOnlyList<RetryIssueSnapshot> Retrying,
	RuntimeTotals CodexTotals,
	JsonNode? RateLimits,
	string WorkflowPath,
	DateTimeOffset WorkflowLoadedAt,
	string? LastReloadError);

public sealed record IssueRuntimeSnapshot(
	string IssueIdentifier,
	string IssueId,
	string Status,
	string WorkspacePath,
	int RestartCount,
	int? CurrentRetryAttempt,
	RunningIssueSnapshot? Running,
	RetryIssueSnapshot? Retry,
	IReadOnlyList<string> CodexSessionLogs,
	IReadOnlyList<RuntimeEventRecord> RecentEvents,
	string? LastError,
	JsonObject Tracked);
