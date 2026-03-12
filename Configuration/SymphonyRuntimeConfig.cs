using System.Text.Json.Nodes;
using Symphony.Domain;

namespace Symphony.Configuration;

public sealed record SymphonyRuntimeConfig(
	TrackerRuntimeConfig Tracker,
	PollingRuntimeConfig Polling,
	WorkspaceRuntimeConfig Workspace,
	HooksRuntimeConfig Hooks,
	AgentRuntimeConfig Agent,
	CodexRuntimeConfig Codex,
	ServerRuntimeConfig Server);

public sealed record TrackerRuntimeConfig(
	string Kind,
	string Endpoint,
	string? ApiKey,
	string? ProjectSlug,
	IReadOnlyList<string> ActiveStates,
	IReadOnlyList<string> TerminalStates,
	IReadOnlySet<string> ActiveStatesNormalized,
	IReadOnlySet<string> TerminalStatesNormalized);

public sealed record PollingRuntimeConfig(
	int IntervalMs);

public sealed record WorkspaceRuntimeConfig(
	string Root);

public sealed record HooksRuntimeConfig(
	string? AfterCreate,
	string? BeforeRun,
	string? AfterRun,
	string? BeforeRemove,
	int TimeoutMs);

public sealed record AgentRuntimeConfig(
	int MaxConcurrentAgents,
	int MaxTurns,
	int MaxRetryBackoffMs,
	IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState);

public sealed record CodexRuntimeConfig(
	string Command,
	JsonNode? ApprovalPolicy,
	string ThreadSandbox,
	JsonNode? TurnSandboxPolicy,
	int TurnTimeoutMs,
	int ReadTimeoutMs,
	int StallTimeoutMs);

public sealed record ServerRuntimeConfig(
	int? Port);

public sealed record DispatchValidationResult(
	bool IsValid,
	string? ErrorCode,
	string? Message);

public sealed record EffectiveWorkflow(
	string Path,
	WorkflowDefinition Definition,
	SymphonyRuntimeConfig Config,
	DateTimeOffset LoadedAt,
	DateTimeOffset LastWriteTimeUtc,
	DispatchValidationResult Validation);
