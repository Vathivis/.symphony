using Symphony.Configuration;
using Symphony.Codex;
using Symphony.Domain;
using Symphony.Tracker;
using Symphony.Workspace;

namespace Symphony.Orchestration;

public sealed class AgentWorkerRunner {
	private readonly WorkflowRuntime _workflowRuntime;
	private readonly WorkflowTemplateRenderer _templateRenderer;
	private readonly WorkspaceManager _workspaceManager;
	private readonly IIssueTrackerClient _issueTrackerClient;
	private readonly ILoggerFactory _loggerFactory;

	public AgentWorkerRunner(
		WorkflowRuntime workflowRuntime,
		WorkflowTemplateRenderer templateRenderer,
		WorkspaceManager workspaceManager,
		IIssueTrackerClient issueTrackerClient,
		ILoggerFactory loggerFactory) {
		_workflowRuntime = workflowRuntime;
		_templateRenderer = templateRenderer;
		_workspaceManager = workspaceManager;
		_issueTrackerClient = issueTrackerClient;
		_loggerFactory = loggerFactory;
	}

	public async Task<WorkerCompletion> RunAsync(
		IssueRecord issue,
		int? attempt,
		string sessionLogPath,
		Func<CodexRuntimeEvent, Task> eventSink,
		CancellationToken cancellationToken) {
		WorkspaceDescriptor? workspace = null;
		var workspacePath = string.Empty;
		var turnCount = 0;
		var config = _workflowRuntime.Current.Config;

		try {
			workspace = await _workspaceManager.CreateOrReuseAsync(
				issue.Identifier,
				config,
				cancellationToken).ConfigureAwait(false);
			workspacePath = workspace.Path;

			await _workspaceManager.PrepareForRunAsync(workspacePath, cancellationToken).ConfigureAwait(false);
			await _workspaceManager.RunBeforeRunHookAsync(workspacePath, config, cancellationToken).ConfigureAwait(false);

			await using var codexClient = new CodexAppServerClient(
				config,
				workspacePath,
				sessionLogPath,
				eventSink,
				_loggerFactory.CreateLogger<CodexAppServerClient>());

			await codexClient.StartAsync(cancellationToken).ConfigureAwait(false);

			var currentIssue = issue;
			while (true) {
				turnCount++;
				var prompt = turnCount == 1
					? _templateRenderer.Render(_workflowRuntime.Current.Definition, currentIssue, attempt)
					: BuildContinuationPrompt(currentIssue, turnCount, config.Agent.MaxTurns);

				CodexTurnResult turnResult;
				try {
					turnResult = await codexClient.RunTurnAsync(
						prompt,
						$"{currentIssue.Identifier}: {currentIssue.Title}",
						cancellationToken).ConfigureAwait(false);
				} catch (CodexClientException exception) when (exception.Code == "turn_timeout") {
					return new WorkerCompletion(
						WorkerExitReason.TimedOut,
						workspacePath,
						exception.Message,
						turnCount);
				} catch (CodexClientException exception) {
					return new WorkerCompletion(
						WorkerExitReason.Failed,
						workspacePath,
						exception.Message,
						turnCount);
				}

				if (turnResult.Outcome == CodexTurnOutcome.InputRequired) {
					return new WorkerCompletion(
						WorkerExitReason.Failed,
						workspacePath,
						"Codex requested user input.",
						turnCount);
				}

				if (turnResult.Outcome is CodexTurnOutcome.Failed or CodexTurnOutcome.Cancelled) {
					return new WorkerCompletion(
						WorkerExitReason.Failed,
						workspacePath,
						turnResult.ErrorMessage ?? $"Turn ended with status {turnResult.Outcome}.",
						turnCount);
				}

				IReadOnlyList<IssueRecord> refreshedIssues;
				try {
					refreshedIssues = await _issueTrackerClient.FetchIssueStatesByIdsAsync(
						[currentIssue.Id],
						cancellationToken).ConfigureAwait(false);
				} catch (Exception exception) {
					return new WorkerCompletion(
						WorkerExitReason.Failed,
						workspacePath,
						$"Unable to refresh issue state after turn completion: {exception.Message}",
						turnCount);
				}

				currentIssue = refreshedIssues.FirstOrDefault() ?? currentIssue;
				var normalizedState = WorkflowConfigParser.NormalizeState(currentIssue.State);
				if (!config.Tracker.ActiveStatesNormalized.Contains(normalizedState)) {
					break;
				}

				if (turnCount >= config.Agent.MaxTurns) {
					break;
				}
			}

			return new WorkerCompletion(
				WorkerExitReason.Normal,
				workspacePath,
				null,
				turnCount);
		} catch (WorkspaceHookException exception) {
			return new WorkerCompletion(
				WorkerExitReason.Failed,
				workspacePath,
				exception.Message,
				turnCount);
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			return new WorkerCompletion(
				WorkerExitReason.HostStopping,
				workspacePath,
				"Worker cancelled.",
				turnCount);
		} catch (Exception exception) {
			return new WorkerCompletion(
				WorkerExitReason.Failed,
				workspacePath,
				exception.Message,
				turnCount);
		} finally {
			if (!string.IsNullOrWhiteSpace(workspacePath)) {
				try {
					await _workspaceManager.RunAfterRunHookAsync(
						workspacePath,
						config,
						CancellationToken.None).ConfigureAwait(false);
				} catch {
					// after_run failures are logged inside WorkspaceManager and ignored here.
				}
			}
		}
	}

	private static string BuildContinuationPrompt(IssueRecord issue, int turnNumber, int maxTurns) {
		return $"""
			Continue working on issue {issue.Identifier}: {issue.Title}.
			The original task prompt is already present in the thread history.
			Do not restate the original instructions.
			Continue from the current workspace state and thread history.
			This is continuation turn {turnNumber} of at most {maxTurns} in the current worker session.
			""";
	}
}
