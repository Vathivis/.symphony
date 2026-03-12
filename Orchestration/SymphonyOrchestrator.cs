using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Symphony.Configuration;
using Symphony.Domain;
using Symphony.Tracker;
using Symphony.Workspace;

namespace Symphony.Orchestration;

public sealed class SymphonyOrchestrator : BackgroundService {
	private readonly object _gate = new();
	private readonly WorkflowRuntime _workflowRuntime;
	private readonly IIssueTrackerClient _issueTrackerClient;
	private readonly WorkspaceManager _workspaceManager;
	private readonly AgentWorkerRunner _workerRunner;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<SymphonyOrchestrator> _logger;
	private readonly SemaphoreSlim _refreshSignal = new(0, 1);

	private readonly Dictionary<string, RunningEntry> _running = new(StringComparer.Ordinal);
	private readonly Dictionary<string, RetryEntry> _retryAttempts = new(StringComparer.Ordinal);
	private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
	private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TrackedIssueState> _tracked = new(StringComparer.Ordinal);
	private double _endedRuntimeSeconds;
	private long _aggregateInputTokens;
	private long _aggregateOutputTokens;
	private long _aggregateTotalTokens;
	private JsonNode? _latestRateLimits;
	private int _refreshQueued;
	private CancellationToken _serviceStoppingToken;

	public SymphonyOrchestrator(
		WorkflowRuntime workflowRuntime,
		IIssueTrackerClient issueTrackerClient,
		WorkspaceManager workspaceManager,
		AgentWorkerRunner workerRunner,
		TimeProvider timeProvider,
		ILogger<SymphonyOrchestrator> logger) {
		_workflowRuntime = workflowRuntime;
		_issueTrackerClient = issueTrackerClient;
		_workspaceManager = workspaceManager;
		_workerRunner = workerRunner;
		_timeProvider = timeProvider;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		_serviceStoppingToken = stoppingToken;
		await StartupCleanupAsync(stoppingToken).ConfigureAwait(false);

		while (!stoppingToken.IsCancellationRequested) {
			await _workflowRuntime.EnsureFreshAsync(stoppingToken).ConfigureAwait(false);
			await ExecuteTickAsync(stoppingToken).ConfigureAwait(false);

			var delay = TimeSpan.FromMilliseconds(_workflowRuntime.Current.Config.Polling.IntervalMs);
			await WaitForNextTickAsync(delay, stoppingToken).ConfigureAwait(false);
		}
	}

	public RuntimeSnapshot GetSnapshot() {
		lock (_gate) {
			var now = _timeProvider.GetUtcNow();
			var activeRuntimeSeconds = _running.Values.Sum(entry => (now - entry.StartedAt).TotalSeconds);
			var running = _running.Values
				.OrderBy(entry => entry.Issue.Identifier, StringComparer.Ordinal)
				.Select(ToRunningSnapshot)
				.ToArray();
			var retrying = _retryAttempts.Values
				.OrderBy(entry => entry.DueAt)
				.Select(entry => new RetryIssueSnapshot(
					entry.IssueId,
					entry.Identifier,
					entry.Attempt,
					entry.DueAt,
					entry.Error))
				.ToArray();

			return new RuntimeSnapshot(
				now,
				new RuntimeCounts(_running.Count, _retryAttempts.Count),
				running,
				retrying,
				new RuntimeTotals(
					_aggregateInputTokens,
					_aggregateOutputTokens,
					_aggregateTotalTokens,
					_endedRuntimeSeconds + activeRuntimeSeconds),
				_latestRateLimits?.DeepClone(),
				_workflowRuntime.Current.Path,
				_workflowRuntime.Current.LoadedAt,
				_workflowRuntime.LastReloadError);
		}
	}

	public IssueRuntimeSnapshot? GetIssueSnapshot(string issueIdentifier) {
		lock (_gate) {
			var trackedIssue = _tracked.Values.FirstOrDefault(entry =>
				string.Equals(entry.IssueIdentifier, issueIdentifier, StringComparison.OrdinalIgnoreCase));
			if (trackedIssue is null) {
				return null;
			}

			_running.TryGetValue(trackedIssue.IssueId, out var runningEntry);
			_retryAttempts.TryGetValue(trackedIssue.IssueId, out var retryEntry);
			return new IssueRuntimeSnapshot(
				trackedIssue.IssueIdentifier,
				trackedIssue.IssueId,
				runningEntry is not null ? "running" : retryEntry is not null ? "retrying" : "released",
				trackedIssue.WorkspacePath ?? string.Empty,
				trackedIssue.RestartCount,
				runningEntry?.RetryAttempt ?? retryEntry?.Attempt,
				runningEntry is null ? null : ToRunningSnapshot(runningEntry),
				retryEntry is null ? null : new RetryIssueSnapshot(
					retryEntry.IssueId,
					retryEntry.Identifier,
					retryEntry.Attempt,
					retryEntry.DueAt,
					retryEntry.Error),
				string.IsNullOrWhiteSpace(trackedIssue.LatestLogPath) ? [] : [trackedIssue.LatestLogPath],
				trackedIssue.RecentEvents.ToArray(),
				trackedIssue.LastError,
				new JsonObject());
		}
	}

	public RefreshRequestResult RequestRefresh() {
		var now = _timeProvider.GetUtcNow();
		var coalesced = Interlocked.Exchange(ref _refreshQueued, 1) == 1;
		if (!coalesced) {
			try {
				_refreshSignal.Release();
			} catch (SemaphoreFullException) {
				coalesced = true;
			}
		}

		return new RefreshRequestResult(true, coalesced, now, ["poll", "reconcile"]);
	}

	public static IReadOnlyList<IssueRecord> SortIssuesForDispatch(IEnumerable<IssueRecord> issues) {
		return issues
			.OrderBy(issue => issue.Priority ?? int.MaxValue)
			.ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
			.ThenBy(issue => issue.Identifier, StringComparer.Ordinal)
			.ToArray();
	}

	private async Task ExecuteTickAsync(CancellationToken cancellationToken) {
		await ReconcileRunningIssuesAsync(cancellationToken).ConfigureAwait(false);

		var workflow = _workflowRuntime.Current;
		if (!workflow.Validation.IsValid) {
			_logger.LogError(
				"action=dispatch_preflight outcome=failed error_code={ErrorCode} message={Message}",
				workflow.Validation.ErrorCode,
				workflow.Validation.Message);
			return;
		}

		IReadOnlyList<IssueRecord> candidateIssues;
		try {
			candidateIssues = await _issueTrackerClient.FetchCandidateIssuesAsync(cancellationToken).ConfigureAwait(false);
		} catch (TrackerException exception) {
			_logger.LogError(
				exception,
				"action=fetch_candidate_issues outcome=failed error_code={ErrorCode} message={Message}",
				exception.Code,
				exception.Message);
			return;
		}

		foreach (var issue in SortIssuesForDispatch(candidateIssues)) {
			if (!TryReserveDispatch(issue, attempt: null)) {
				continue;
			}

			DispatchIssue(issue, attempt: null);
		}
	}

	private bool TryReserveDispatch(IssueRecord issue, int? attempt) {
		lock (_gate) {
			if (!IsCandidateEligibleLocked(issue, attempt, ignoreClaimForIssueId: null)) {
				return false;
			}

			_claimed.Add(issue.Id);
			_tracked[issue.Id] = _tracked.TryGetValue(issue.Id, out var tracked)
				? tracked with { IssueIdentifier = issue.Identifier }
				: new TrackedIssueState(issue.Id, issue.Identifier);
			return true;
		}
	}

	private void DispatchIssue(IssueRecord issue, int? attempt) {
		var logPath = Path.Combine(
			AppContext.BaseDirectory,
			"logs",
			"codex",
			WorkspaceManager.SanitizeWorkspaceKey(issue.Identifier),
			"latest.log");
		var workspacePath = Path.GetFullPath(Path.Combine(
			_workflowRuntime.Current.Config.Workspace.Root,
			WorkspaceManager.SanitizeWorkspaceKey(issue.Identifier)));

		var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken);
		var entry = new RunningEntry(
			issue,
			issue.Identifier,
			cancellationTokenSource,
			_timeProvider.GetUtcNow(),
			attempt,
			logPath);

		lock (_gate) {
			_running[issue.Id] = entry;
			_retryAttempts.Remove(issue.Id);
			if (_tracked.TryGetValue(issue.Id, out var tracked)) {
				_tracked[issue.Id] = tracked with {
					IssueIdentifier = issue.Identifier,
					IssueId = issue.Id,
					WorkspacePath = workspacePath,
					LatestLogPath = logPath
				};
			}
		}

		_logger.LogInformation(
			"action=dispatch outcome=started issue_id={IssueId} issue_identifier={IssueIdentifier} attempt={Attempt}",
			issue.Id,
			issue.Identifier,
			attempt);

		entry.WorkerTask = Task.Run(async () => {
			var completion = await _workerRunner.RunAsync(
				issue,
				attempt,
				logPath,
				codexEvent => HandleCodexEventAsync(issue.Id, codexEvent),
				cancellationTokenSource.Token).ConfigureAwait(false);
			await HandleWorkerExitAsync(issue.Id, completion).ConfigureAwait(false);
		});
	}

	private async Task HandleCodexEventAsync(string issueId, CodexRuntimeEvent codexEvent) {
		lock (_gate) {
			if (!_running.TryGetValue(issueId, out var entry)) {
				return;
			}

			entry.LastCodexEvent = codexEvent.Event;
			entry.LastCodexTimestamp = codexEvent.Timestamp;
			entry.LastCodexMessage = codexEvent.Message;

			if (!string.IsNullOrWhiteSpace(codexEvent.SessionId)) {
				entry.SessionId = codexEvent.SessionId;
				entry.ThreadId = codexEvent.ThreadId;
				entry.TurnId = codexEvent.TurnId;
				entry.TurnCount++;
			}

			if (codexEvent.Usage is not null) {
				var inputDelta = Math.Max(0, codexEvent.Usage.InputTokens - entry.LastReportedInputTokens);
				var outputDelta = Math.Max(0, codexEvent.Usage.OutputTokens - entry.LastReportedOutputTokens);
				var totalDelta = Math.Max(0, codexEvent.Usage.TotalTokens - entry.LastReportedTotalTokens);

				_aggregateInputTokens += inputDelta;
				_aggregateOutputTokens += outputDelta;
				_aggregateTotalTokens += totalDelta;

				entry.CodexInputTokens = codexEvent.Usage.InputTokens;
				entry.CodexOutputTokens = codexEvent.Usage.OutputTokens;
				entry.CodexTotalTokens = codexEvent.Usage.TotalTokens;
				entry.LastReportedInputTokens = codexEvent.Usage.InputTokens;
				entry.LastReportedOutputTokens = codexEvent.Usage.OutputTokens;
				entry.LastReportedTotalTokens = codexEvent.Usage.TotalTokens;
			}

			if (codexEvent.RateLimits is not null) {
				_latestRateLimits = codexEvent.RateLimits.DeepClone();
			}

			AddRecentEvent(issueId, codexEvent.Timestamp, codexEvent.Event, codexEvent.Message);
		}

		await Task.CompletedTask.ConfigureAwait(false);
	}

	private async Task HandleWorkerExitAsync(string issueId, WorkerCompletion completion) {
		RetryPlan? retryPlan = null;
		string? workspaceToCleanup = null;
		string? issueIdentifier = null;

		lock (_gate) {
			if (!_running.TryGetValue(issueId, out var entry)) {
				return;
			}

			_running.Remove(issueId);
			issueIdentifier = entry.Identifier;
			_endedRuntimeSeconds += (_timeProvider.GetUtcNow() - entry.StartedAt).TotalSeconds;

			if (_tracked.TryGetValue(issueId, out var tracked)) {
				_tracked[issueId] = tracked with {
					WorkspacePath = string.IsNullOrWhiteSpace(completion.WorkspacePath) ? tracked.WorkspacePath : completion.WorkspacePath,
					LastError = completion.Error ?? tracked.LastError
				};
			}

			switch (entry.PendingStopReason) {
				case StopReason.Terminal:
					_claimed.Remove(issueId);
					_completed.Add(issueId);
					workspaceToCleanup = completion.WorkspacePath;
					break;

				case StopReason.Inactive:
					_claimed.Remove(issueId);
					_completed.Add(issueId);
					break;

				case StopReason.Stalled:
					retryPlan = new RetryPlan(issueId, entry.Identifier, NextRetryAttempt(entry), completion.Error ?? "stalled", Continuation: false);
					break;

				case StopReason.HostStopping:
					_claimed.Remove(issueId);
					break;

				default:
					if (completion.Reason == WorkerExitReason.Normal) {
						_completed.Add(issueId);
						retryPlan = new RetryPlan(issueId, entry.Identifier, 1, null, Continuation: true);
					} else if (completion.Reason == WorkerExitReason.HostStopping) {
						_claimed.Remove(issueId);
					} else {
						retryPlan = new RetryPlan(issueId, entry.Identifier, NextRetryAttempt(entry), completion.Error, Continuation: false);
					}
					break;
			}

			if (_tracked.TryGetValue(issueId, out var updatedTracked)) {
				_tracked[issueId] = updatedTracked with {
					LastError = completion.Error,
					RestartCount = retryPlan is null ? updatedTracked.RestartCount : updatedTracked.RestartCount + 1
				};
			}
		}

		if (workspaceToCleanup is not null) {
			try {
				await _workspaceManager.RemoveWorkspaceAsync(issueIdentifier!, _workflowRuntime.Current.Config, CancellationToken.None)
					.ConfigureAwait(false);
			} catch (Exception exception) {
				_logger.LogWarning(
					exception,
					"action=workspace_cleanup outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} message={Message}",
					issueId,
					issueIdentifier,
					exception.Message);
			}
		}

		if (retryPlan is not null) {
			ScheduleRetry(retryPlan);
		}
	}

	private async Task ReconcileRunningIssuesAsync(CancellationToken cancellationToken) {
		IReadOnlyList<RunningEntry> runningEntries;
		var workflow = _workflowRuntime.Current;
		lock (_gate) {
			runningEntries = _running.Values.ToArray();
		}

		if (workflow.Config.Codex.StallTimeoutMs > 0) {
			var now = _timeProvider.GetUtcNow();
			foreach (var entry in runningEntries) {
				var lastSeenAt = entry.LastCodexTimestamp ?? entry.StartedAt;
				if ((now - lastSeenAt).TotalMilliseconds <= workflow.Config.Codex.StallTimeoutMs) {
					continue;
				}

				lock (_gate) {
					if (_running.TryGetValue(entry.Issue.Id, out var liveEntry) && liveEntry.PendingStopReason is null) {
						liveEntry.PendingStopReason = StopReason.Stalled;
						liveEntry.CancellationTokenSource.Cancel();
					}
				}
			}
		}

		lock (_gate) {
			runningEntries = _running.Values.ToArray();
		}
		if (runningEntries.Count == 0) {
			return;
		}

		IReadOnlyList<IssueRecord> refreshedIssues;
		try {
			refreshedIssues = await _issueTrackerClient.FetchIssueStatesByIdsAsync(
				runningEntries.Select(entry => entry.Issue.Id).ToArray(),
				cancellationToken).ConfigureAwait(false);
		} catch (TrackerException exception) {
			_logger.LogWarning(
				exception,
				"action=reconcile outcome=failed error_code={ErrorCode} message={Message}",
				exception.Code,
				exception.Message);
			return;
		}

		var refreshedById = refreshedIssues.ToDictionary(issue => issue.Id, StringComparer.Ordinal);
		foreach (var entry in runningEntries) {
			if (!refreshedById.TryGetValue(entry.Issue.Id, out var refreshedIssue)) {
				continue;
			}

			var normalizedState = WorkflowConfigParser.NormalizeState(refreshedIssue.State);
			lock (_gate) {
				if (!_running.TryGetValue(entry.Issue.Id, out var liveEntry)) {
					continue;
				}

				if (workflow.Config.Tracker.TerminalStatesNormalized.Contains(normalizedState)) {
					liveEntry.PendingStopReason = StopReason.Terminal;
					liveEntry.CancellationTokenSource.Cancel();
				} else if (workflow.Config.Tracker.ActiveStatesNormalized.Contains(normalizedState)) {
					liveEntry.Issue = refreshedIssue;
					UpdateTrackedIssueLocked(refreshedIssue);
				} else {
					liveEntry.PendingStopReason = StopReason.Inactive;
					liveEntry.CancellationTokenSource.Cancel();
				}
			}
		}
	}

	private async Task StartupCleanupAsync(CancellationToken cancellationToken) {
		try {
			var terminalIssues = await _issueTrackerClient.FetchIssuesByStatesAsync(
				_workflowRuntime.Current.Config.Tracker.TerminalStates,
				cancellationToken).ConfigureAwait(false);
			foreach (var issue in terminalIssues) {
				try {
					await _workspaceManager.RemoveWorkspaceAsync(
						issue.Identifier,
						_workflowRuntime.Current.Config,
						cancellationToken).ConfigureAwait(false);
				} catch (Exception exception) {
					_logger.LogWarning(
						exception,
						"action=startup_cleanup outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} message={Message}",
						issue.Id,
						issue.Identifier,
						exception.Message);
				}
			}
		} catch (Exception exception) {
			_logger.LogWarning(
				exception,
				"action=startup_cleanup outcome=failed message={Message}",
				exception.Message);
		}
	}

	private void ScheduleRetry(RetryPlan plan) {
		Timer? oldTimer = null;
		var now = _timeProvider.GetUtcNow();
		var delayMs = plan.Continuation
			? 1_000
			: Math.Min(10_000 * Math.Pow(2, Math.Max(plan.Attempt - 1, 0)), _workflowRuntime.Current.Config.Agent.MaxRetryBackoffMs);
		var dueAt = now.AddMilliseconds(delayMs);

		lock (_gate) {
			if (_retryAttempts.TryGetValue(plan.IssueId, out var existingRetry)) {
				oldTimer = existingRetry.Timer;
			}

			Timer? timer = null;
			timer = new Timer(_ => {
				try {
					timer?.Dispose();
				} catch {
					// Ignore.
				}
				_ = HandleRetryDueAsync(plan.IssueId);
			}, null, TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);

			_retryAttempts[plan.IssueId] = new RetryEntry(
				plan.IssueId,
				plan.Identifier,
				plan.Attempt,
				dueAt,
				plan.Error,
				timer);
			_claimed.Add(plan.IssueId);
			AddRecentEvent(plan.IssueId, now, "retrying", plan.Error);
		}

		oldTimer?.Dispose();
	}

	private async Task HandleRetryDueAsync(string issueId) {
		RetryEntry? retryEntry;
		lock (_gate) {
			if (!_retryAttempts.TryGetValue(issueId, out retryEntry)) {
				return;
			}

			_retryAttempts.Remove(issueId);
		}

		IReadOnlyList<IssueRecord> candidates;
		try {
			candidates = await _issueTrackerClient.FetchCandidateIssuesAsync(CancellationToken.None).ConfigureAwait(false);
		} catch (TrackerException exception) {
			ScheduleRetry(new RetryPlan(
				issueId,
				retryEntry.Identifier,
				retryEntry.Attempt + 1,
				"retry poll failed",
				Continuation: false));
			_logger.LogWarning(
				exception,
				"action=retry outcome=failed issue_id={IssueId} issue_identifier={IssueIdentifier} error_code={ErrorCode} message={Message}",
				issueId,
				retryEntry.Identifier,
				exception.Code,
				exception.Message);
			return;
		}

		var issue = candidates.FirstOrDefault(candidate => string.Equals(candidate.Id, issueId, StringComparison.Ordinal));
		if (issue is null) {
			lock (_gate) {
				_claimed.Remove(issueId);
			}
			return;
		}

		lock (_gate) {
			if (!IsCandidateEligibleLocked(issue, retryEntry.Attempt, ignoreClaimForIssueId: issueId)) {
				if (AvailableGlobalSlotsLocked() == 0 || !HasPerStateSlotLocked(issue.State)) {
					ScheduleRetry(new RetryPlan(
						issue.Id,
						issue.Identifier,
						retryEntry.Attempt + 1,
						"no available orchestrator slots",
						Continuation: false));
				} else {
					_claimed.Remove(issueId);
				}

				return;
			}
		}

		DispatchIssue(issue, retryEntry.Attempt);
	}

	private async Task WaitForNextTickAsync(TimeSpan delay, CancellationToken cancellationToken) {
		try {
			var delayTask = Task.Delay(delay, cancellationToken);
			var refreshTask = _refreshSignal.WaitAsync(cancellationToken);
			await Task.WhenAny(delayTask, refreshTask).ConfigureAwait(false);
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// Normal shutdown.
		} finally {
			Interlocked.Exchange(ref _refreshQueued, 0);
		}
	}

	private bool IsCandidateEligibleLocked(IssueRecord issue, int? attempt, string? ignoreClaimForIssueId) {
		if (string.IsNullOrWhiteSpace(issue.Id) ||
		    string.IsNullOrWhiteSpace(issue.Identifier) ||
		    string.IsNullOrWhiteSpace(issue.Title) ||
		    string.IsNullOrWhiteSpace(issue.State)) {
			return false;
		}

		var config = _workflowRuntime.Current.Config;
		var normalizedState = WorkflowConfigParser.NormalizeState(issue.State);
		if (!config.Tracker.ActiveStatesNormalized.Contains(normalizedState) ||
		    config.Tracker.TerminalStatesNormalized.Contains(normalizedState)) {
			return false;
		}

		if (_running.ContainsKey(issue.Id)) {
			return false;
		}

		if (_claimed.Contains(issue.Id) && !string.Equals(ignoreClaimForIssueId, issue.Id, StringComparison.Ordinal)) {
			return false;
		}

		if (AvailableGlobalSlotsLocked() <= 0) {
			return false;
		}

		if (!HasPerStateSlotLocked(issue.State)) {
			return false;
		}

		if (string.Equals(normalizedState, "todo", StringComparison.Ordinal) &&
		    issue.BlockedBy.Any(blocker => blocker.State is { } blockerState &&
		                                   !config.Tracker.TerminalStatesNormalized.Contains(WorkflowConfigParser.NormalizeState(blockerState)))) {
			return false;
		}

		return true;
	}

	private int AvailableGlobalSlotsLocked() {
		return Math.Max(_workflowRuntime.Current.Config.Agent.MaxConcurrentAgents - _running.Count, 0);
	}

	private bool HasPerStateSlotLocked(string state) {
		var normalizedState = WorkflowConfigParser.NormalizeState(state);
		var config = _workflowRuntime.Current.Config;
		var limit = config.Agent.MaxConcurrentAgentsByState.TryGetValue(normalizedState, out var perStateLimit)
			? perStateLimit
			: config.Agent.MaxConcurrentAgents;
		var runningCount = _running.Values.Count(entry =>
			string.Equals(
				WorkflowConfigParser.NormalizeState(entry.Issue.State),
				normalizedState,
				StringComparison.Ordinal));
		return runningCount < limit;
	}

	private int NextRetryAttempt(RunningEntry entry) {
		return entry.RetryAttempt is null or < 1 ? 1 : entry.RetryAttempt.Value + 1;
	}

	private RunningIssueSnapshot ToRunningSnapshot(RunningEntry entry) {
		return new RunningIssueSnapshot(
			entry.Issue.Id,
			entry.Issue.Identifier,
			entry.Issue.State,
			entry.SessionId,
			entry.TurnCount,
			entry.LastCodexEvent,
			entry.LastCodexMessage,
			entry.StartedAt,
			entry.LastCodexTimestamp,
			new CodexTokenUsage(entry.CodexInputTokens, entry.CodexOutputTokens, entry.CodexTotalTokens));
	}

	private void UpdateTrackedIssueLocked(IssueRecord issue) {
		if (_tracked.TryGetValue(issue.Id, out var tracked)) {
			_tracked[issue.Id] = tracked with {
				IssueIdentifier = issue.Identifier,
				IssueId = issue.Id
			};
			return;
		}

		_tracked[issue.Id] = new TrackedIssueState(issue.Id, issue.Identifier);
	}

	private void AddRecentEvent(string issueId, DateTimeOffset at, string eventName, string? message) {
		if (!_tracked.TryGetValue(issueId, out var tracked)) {
			return;
		}

		var events = tracked.RecentEvents.ToList();
		events.Add(new RuntimeEventRecord(at, eventName, message));
		if (events.Count > 25) {
			events.RemoveRange(0, events.Count - 25);
		}

		_tracked[issueId] = tracked with { RecentEvents = events };
	}

	private sealed class RunningEntry(
		IssueRecord issue,
		string identifier,
		CancellationTokenSource cancellationTokenSource,
		DateTimeOffset startedAt,
		int? retryAttempt,
		string latestLogPath) {
		public IssueRecord Issue { get; set; } = issue;
		public string Identifier { get; } = identifier;
		public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;
		public DateTimeOffset StartedAt { get; } = startedAt;
		public int? RetryAttempt { get; } = retryAttempt;
		public string LatestLogPath { get; } = latestLogPath;
		public Task? WorkerTask { get; set; }
		public string? SessionId { get; set; }
		public string? ThreadId { get; set; }
		public string? TurnId { get; set; }
		public string? LastCodexEvent { get; set; }
		public DateTimeOffset? LastCodexTimestamp { get; set; }
		public string? LastCodexMessage { get; set; }
		public long CodexInputTokens { get; set; }
		public long CodexOutputTokens { get; set; }
		public long CodexTotalTokens { get; set; }
		public long LastReportedInputTokens { get; set; }
		public long LastReportedOutputTokens { get; set; }
		public long LastReportedTotalTokens { get; set; }
		public int TurnCount { get; set; }
		public StopReason? PendingStopReason { get; set; }
	}

	private sealed record RetryEntry(
		string IssueId,
		string Identifier,
		int Attempt,
		DateTimeOffset DueAt,
		string? Error,
		Timer Timer);

	private sealed record RetryPlan(
		string IssueId,
		string Identifier,
		int Attempt,
		string? Error,
		bool Continuation);

	private sealed record TrackedIssueState(
		string IssueId,
		string IssueIdentifier,
		string? WorkspacePath = null,
		int RestartCount = 0,
		string? LatestLogPath = null,
		string? LastError = null,
		IReadOnlyList<RuntimeEventRecord>? RecentEvents = null) {
		public IReadOnlyList<RuntimeEventRecord> RecentEvents { get; init; } = RecentEvents ?? [];
	}

	private enum StopReason {
		Terminal,
		Inactive,
		Stalled,
		HostStopping
	}
}

public sealed record RefreshRequestResult(
	bool Queued,
	bool Coalesced,
	DateTimeOffset RequestedAt,
	IReadOnlyList<string> Operations);
