using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Configuration;
using Symphony.Domain;

namespace Symphony.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable {
	private const int MaxLineLength = 10 * 1024 * 1024;

	private readonly SymphonyRuntimeConfig _config;
	private readonly string _workspacePath;
	private readonly string _sessionLogPath;
	private readonly Func<CodexRuntimeEvent, Task> _eventSink;
	private readonly ILogger<CodexAppServerClient> _logger;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pendingResponses = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly StreamWriter _logWriter;

	private Process? _process;
	private StreamWriter? _stdin;
	private StreamReader? _stdout;
	private StreamReader? _stderr;
	private Task? _stdoutPump;
	private Task? _stderrPump;
	private readonly TaskCompletionSource<object?> _processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private TaskCompletionSource<CodexTurnResult>? _activeTurnCompletion;
	private string? _activeTurnId;
	private int _nextRequestId;

	public CodexAppServerClient(
		SymphonyRuntimeConfig config,
		string workspacePath,
		string sessionLogPath,
		Func<CodexRuntimeEvent, Task> eventSink,
		ILogger<CodexAppServerClient> logger) {
		_config = config;
		_workspacePath = workspacePath;
		_sessionLogPath = sessionLogPath;
		_eventSink = eventSink;
		_logger = logger;

		Directory.CreateDirectory(Path.GetDirectoryName(sessionLogPath)!);
		_logWriter = new StreamWriter(
			new FileStream(sessionLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
			Encoding.UTF8) {
			AutoFlush = true
		};
	}

	public string? ThreadId { get; private set; }

	public int? ProcessId => _process?.Id;

	public async Task StartAsync(CancellationToken cancellationToken) {
		Workspace.WorkspaceManager.EnsureWithinRoot(Path.GetDirectoryName(_workspacePath) ?? _workspacePath, _workspacePath);
		_process = CreateProcess();

		try {
			_process.Start();
		} catch (Exception exception) {
			throw new CodexClientException(
				"codex_not_found",
				$"Unable to start Codex app-server: {exception.Message}",
				exception);
		}

		_stdin = _process.StandardInput;
		_stdout = _process.StandardOutput;
		_stderr = _process.StandardError;

		_stdoutPump = Task.Run(() => PumpStdoutAsync(_stdout, cancellationToken));
		_stderrPump = Task.Run(() => PumpStderrAsync(_stderr, cancellationToken));
		_ = WaitForProcessExitAsync(_process);

		await SendRequestAsync(
			"initialize",
			new JsonObject {
				["clientInfo"] = new JsonObject {
					["name"] = "symphony",
					["version"] = "1.0"
				},
				["capabilities"] = new JsonObject()
			},
			_config.Codex.ReadTimeoutMs,
			cancellationToken).ConfigureAwait(false);

		await SendNotificationAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

		var threadStartResponse = await SendRequestAsync(
			"thread/start",
			new JsonObject {
				["approvalPolicy"] = CloneNode(_config.Codex.ApprovalPolicy),
				["sandbox"] = _config.Codex.ThreadSandbox,
				["cwd"] = _workspacePath,
				["personality"] = "pragmatic",
				["serviceName"] = "symphony"
			},
			_config.Codex.ReadTimeoutMs,
			cancellationToken).ConfigureAwait(false);

		ThreadId = threadStartResponse?["thread"]?["id"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(ThreadId)) {
			throw new CodexClientException(
				"response_error",
				"thread/start response did not include result.thread.id.");
		}
	}

	public async Task<CodexTurnResult> RunTurnAsync(
		string prompt,
		string title,
		CancellationToken cancellationToken) {
		if (_process is null || _stdin is null || ThreadId is null) {
			throw new InvalidOperationException("Codex session has not been started.");
		}

		var turnStartResponse = await SendRequestAsync(
			"turn/start",
			new JsonObject {
				["threadId"] = ThreadId,
				["input"] = new JsonArray {
					new JsonObject {
						["type"] = "text",
						["text"] = prompt
					}
				},
				["cwd"] = _workspacePath,
				["title"] = title,
				["approvalPolicy"] = CloneNode(_config.Codex.ApprovalPolicy),
				["sandboxPolicy"] = CloneNode(_config.Codex.TurnSandboxPolicy)
			},
			_config.Codex.ReadTimeoutMs,
			cancellationToken).ConfigureAwait(false);

		var turnId = turnStartResponse?["turn"]?["id"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(turnId)) {
			throw new CodexClientException(
				"response_error",
				"turn/start response did not include result.turn.id.");
		}

		var completion = new TaskCompletionSource<CodexTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_activeTurnCompletion = completion;
		_activeTurnId = turnId;

		await EmitAsync(new CodexRuntimeEvent(
			"session_started",
			DateTimeOffset.UtcNow,
			ProcessId,
			SessionId: $"{ThreadId}-{turnId}",
			ThreadId: ThreadId,
			TurnId: turnId)).ConfigureAwait(false);

		if (turnStartResponse?["turn"]?["status"]?.GetValue<string>() is { } immediateStatus &&
		    !string.Equals(immediateStatus, "inProgress", StringComparison.OrdinalIgnoreCase)) {
			CompleteTurnFromStatus(turnId, immediateStatus, turnStartResponse?["turn"]?["error"]?["message"]?.GetValue<string>());
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_config.Codex.TurnTimeoutMs));

		var completedTask = await Task.WhenAny(
			completion.Task,
			_processExited.Task,
			Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);

		if (completedTask == completion.Task) {
			return await completion.Task.ConfigureAwait(false);
		}

		if (completedTask == _processExited.Task) {
			throw new CodexClientException(
				"port_exit",
				"Codex app-server exited before the turn completed.");
		}

		try {
			if (_process is { HasExited: false }) {
				_process.Kill(entireProcessTree: true);
			}
		} catch {
			// Best effort kill only.
		}

		throw new CodexClientException(
			"turn_timeout",
			$"Codex turn timed out after {_config.Codex.TurnTimeoutMs} ms.");
	}

	public async ValueTask DisposeAsync() {
		try {
			if (_process is { HasExited: false }) {
				_process.Kill(entireProcessTree: true);
				await _process.WaitForExitAsync().ConfigureAwait(false);
			}
		} catch {
			// Ignore disposal cleanup issues.
		}

		if (_stdoutPump is not null) {
			try {
				await _stdoutPump.ConfigureAwait(false);
			} catch {
				// Ignore.
			}
		}

		if (_stderrPump is not null) {
			try {
				await _stderrPump.ConfigureAwait(false);
			} catch {
				// Ignore.
			}
		}

		_logWriter.Dispose();
		_writeLock.Dispose();
		_process?.Dispose();
	}

	private Process CreateProcess() {
		var startInfo = new ProcessStartInfo {
			FileName = "bash",
			WorkingDirectory = _workspacePath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("-lc");
		startInfo.ArgumentList.Add(_config.Codex.Command);

		return new Process {
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
	}

	private async Task<JsonNode?> SendRequestAsync(
		string method,
		JsonNode paramsNode,
		int timeoutMs,
		CancellationToken cancellationToken) {
		var requestId = Interlocked.Increment(ref _nextRequestId).ToString();
		var responseTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_pendingResponses.TryAdd(requestId, responseTcs)) {
			throw new InvalidOperationException($"Duplicate request id '{requestId}'.");
		}

		try {
			await WriteJsonAsync(new JsonObject {
				["id"] = requestId,
				["method"] = method,
				["params"] = paramsNode
			}, cancellationToken).ConfigureAwait(false);

			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

			try {
				return await responseTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
			} catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) {
				throw new CodexClientException(
					"response_timeout",
					$"Timed out waiting for '{method}' response after {timeoutMs} ms.",
					exception);
			}
		} finally {
			_pendingResponses.TryRemove(requestId, out _);
		}
	}

	private Task SendNotificationAsync(string method, JsonNode paramsNode, CancellationToken cancellationToken) {
		return WriteJsonAsync(new JsonObject {
			["method"] = method,
			["params"] = paramsNode
		}, cancellationToken);
	}

	private async Task WriteJsonAsync(JsonNode node, CancellationToken cancellationToken) {
		if (_stdin is null) {
			throw new InvalidOperationException("Codex stdin is not available.");
		}

		var payload = node.ToJsonString();
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			await _stdin.WriteLineAsync(payload).WaitAsync(cancellationToken).ConfigureAwait(false);
			await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
			await _logWriter.WriteLineAsync($"stdin {payload}").ConfigureAwait(false);
		} finally {
			_writeLock.Release();
		}
	}

	private async Task PumpStdoutAsync(StreamReader stdout, CancellationToken cancellationToken) {
		while (true) {
			string? line;
			try {
				line = await stdout.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (line is null) {
				break;
			}

			await _logWriter.WriteLineAsync($"stdout {line}").ConfigureAwait(false);
			if (line.Length > MaxLineLength) {
				await EmitAsync(new CodexRuntimeEvent(
					"malformed",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: $"stdout line exceeded {MaxLineLength} bytes")).ConfigureAwait(false);
				continue;
			}

			JsonNode? node;
			try {
				node = JsonNode.Parse(line);
			} catch (Exception exception) {
				await EmitAsync(new CodexRuntimeEvent(
					"malformed",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: exception.Message)).ConfigureAwait(false);
				continue;
			}

			if (node is not JsonObject messageObject) {
				continue;
			}

			if (messageObject["method"] is JsonValue && messageObject["id"] is not null) {
				await HandleServerRequestAsync(messageObject, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (messageObject["method"] is JsonValue) {
				await HandleNotificationAsync(messageObject).ConfigureAwait(false);
				continue;
			}

			if (messageObject["id"] is not null) {
				HandleResponse(messageObject);
			}
		}
	}

	private async Task PumpStderrAsync(StreamReader stderr, CancellationToken cancellationToken) {
		while (true) {
			string? line;
			try {
				line = await stderr.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (line is null) {
				break;
			}

			await _logWriter.WriteLineAsync($"stderr {line}").ConfigureAwait(false);
			await EmitAsync(new CodexRuntimeEvent(
				"notification",
				DateTimeOffset.UtcNow,
				ProcessId,
				Message: line)).ConfigureAwait(false);
		}
	}

	private void HandleResponse(JsonObject response) {
		var requestId = response["id"]?.ToString();
		if (string.IsNullOrWhiteSpace(requestId) || !_pendingResponses.TryGetValue(requestId, out var completion)) {
			return;
		}

		if (response["error"] is not null) {
			completion.TrySetException(new CodexClientException(
				"response_error",
				response["error"]!.ToJsonString()));
			return;
		}

		completion.TrySetResult(CloneNode(response["result"]));
	}

	private async Task HandleServerRequestAsync(JsonObject request, CancellationToken cancellationToken) {
		var requestId = request["id"]?.ToString();
		var method = request["method"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(method)) {
			return;
		}

		switch (method) {
			case "item/commandExecution/requestApproval":
				await EmitAsync(new CodexRuntimeEvent(
					"approval_auto_approved",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: "command execution")).ConfigureAwait(false);
				await WriteJsonAsync(new JsonObject {
					["id"] = requestId,
					["result"] = new JsonObject {
						["decision"] = "acceptForSession"
					}
				}, cancellationToken).ConfigureAwait(false);
				break;

			case "item/fileChange/requestApproval":
				await EmitAsync(new CodexRuntimeEvent(
					"approval_auto_approved",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: "file change")).ConfigureAwait(false);
				await WriteJsonAsync(new JsonObject {
					["id"] = requestId,
					["result"] = new JsonObject {
						["decision"] = "acceptForSession"
					}
				}, cancellationToken).ConfigureAwait(false);
				break;

			case "item/tool/requestUserInput":
				await EmitAsync(new CodexRuntimeEvent(
					"turn_input_required",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: request["params"]?["itemId"]?.ToString(),
					ThreadId: request["params"]?["threadId"]?.ToString(),
					TurnId: request["params"]?["turnId"]?.ToString())).ConfigureAwait(false);

				_activeTurnCompletion?.TrySetResult(new CodexTurnResult(
					ThreadId ?? string.Empty,
					_activeTurnId ?? string.Empty,
					CodexTurnOutcome.InputRequired,
					"Codex requested user input."));

				await WriteJsonAsync(new JsonObject {
					["id"] = requestId,
					["result"] = new JsonObject {
						["answers"] = new JsonObject()
					}
				}, cancellationToken).ConfigureAwait(false);
				break;

			case "item/tool/call":
				await EmitAsync(new CodexRuntimeEvent(
					"unsupported_tool_call",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: request["params"]?["tool"]?.ToString(),
					ThreadId: request["params"]?["threadId"]?.ToString(),
					TurnId: request["params"]?["turnId"]?.ToString())).ConfigureAwait(false);

				await WriteJsonAsync(new JsonObject {
					["id"] = requestId,
					["result"] = new JsonObject {
						["success"] = false,
						["contentItems"] = new JsonArray {
							new JsonObject {
								["type"] = "inputText",
								["text"] = """{"error":"unsupported_tool_call"}"""
							}
						}
					}
				}, cancellationToken).ConfigureAwait(false);
				break;
		}
	}

	private async Task HandleNotificationAsync(JsonObject notification) {
		var method = notification["method"]?.GetValue<string>();
		var parameters = notification["params"] as JsonObject;
		if (string.IsNullOrWhiteSpace(method)) {
			return;
		}

		switch (method) {
			case "thread/tokenUsage/updated":
				var total = parameters?["tokenUsage"]?["total"] as JsonObject;
				if (total is not null) {
					var usage = new CodexTokenUsage(
						ReadInt64(total["inputTokens"]),
						ReadInt64(total["outputTokens"]),
						ReadInt64(total["totalTokens"]));
					await EmitAsync(new CodexRuntimeEvent(
						"notification",
						DateTimeOffset.UtcNow,
						ProcessId,
						Message: "token usage updated",
						ThreadId: parameters?["threadId"]?.ToString(),
						TurnId: parameters?["turnId"]?.ToString(),
						Usage: usage)).ConfigureAwait(false);
				}

				break;

			case "account/rateLimits/updated":
				await EmitAsync(new CodexRuntimeEvent(
					"notification",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: "rate limits updated",
					RateLimits: CloneNode(parameters?["rateLimits"]))).ConfigureAwait(false);
				break;

			case "item/agentMessage/delta":
				await EmitAsync(new CodexRuntimeEvent(
					"notification",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: parameters?["delta"]?.ToString(),
					ThreadId: parameters?["threadId"]?.ToString(),
					TurnId: parameters?["turnId"]?.ToString())).ConfigureAwait(false);
				break;

			case "item/started":
			case "item/completed":
				var itemType = parameters?["item"]?["type"]?.ToString();
				var itemMessage = parameters?["item"]?["text"]?.ToString()
					?? parameters?["item"]?["tool"]?.ToString()
					?? itemType;
				await EmitAsync(new CodexRuntimeEvent(
					"other_message",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: itemMessage,
					ThreadId: parameters?["threadId"]?.ToString(),
					TurnId: parameters?["turnId"]?.ToString())).ConfigureAwait(false);
				break;

			case "error":
				await EmitAsync(new CodexRuntimeEvent(
					"turn_ended_with_error",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: parameters?["message"]?.ToString())).ConfigureAwait(false);
				break;

			case "turn/completed":
				var turn = parameters?["turn"] as JsonObject;
				var turnId = turn?["id"]?.ToString();
				var status = turn?["status"]?.ToString();
				var errorMessage = turn?["error"]?["message"]?.ToString();
				CompleteTurnFromStatus(turnId, status, errorMessage);
				break;

			default:
				await EmitAsync(new CodexRuntimeEvent(
					"other_message",
					DateTimeOffset.UtcNow,
					ProcessId,
					Message: method,
					ThreadId: parameters?["threadId"]?.ToString(),
					TurnId: parameters?["turnId"]?.ToString())).ConfigureAwait(false);
				break;
		}
	}

	private void CompleteTurnFromStatus(string? turnId, string? status, string? errorMessage) {
		if (_activeTurnCompletion is null ||
		    string.IsNullOrWhiteSpace(turnId) ||
		    !string.Equals(turnId, _activeTurnId, StringComparison.Ordinal)) {
			return;
		}

		var outcome = status switch {
			"completed" => CodexTurnOutcome.Completed,
			"interrupted" => CodexTurnOutcome.Cancelled,
			"failed" => CodexTurnOutcome.Failed,
			_ => CodexTurnOutcome.Failed
		};

		var runtimeEvent = outcome switch {
			CodexTurnOutcome.Completed => "turn_completed",
			CodexTurnOutcome.Cancelled => "turn_cancelled",
			_ => "turn_failed"
		};

		_ = EmitAsync(new CodexRuntimeEvent(
			runtimeEvent,
			DateTimeOffset.UtcNow,
			ProcessId,
			Message: errorMessage,
			SessionId: ThreadId is null ? null : $"{ThreadId}-{turnId}",
			ThreadId: ThreadId,
			TurnId: turnId));

		_activeTurnCompletion.TrySetResult(new CodexTurnResult(
			ThreadId ?? string.Empty,
			turnId,
			outcome,
			errorMessage));
	}

	private async Task WaitForProcessExitAsync(Process process) {
		try {
			await process.WaitForExitAsync().ConfigureAwait(false);
		} finally {
			_processExited.TrySetResult(null);
		}
	}

	private Task EmitAsync(CodexRuntimeEvent runtimeEvent) => _eventSink(runtimeEvent);

	private static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();

	private static long ReadInt64(JsonNode? value) {
		return value switch {
			JsonValue jsonValue when jsonValue.TryGetValue<long>(out var longValue) => longValue,
			JsonValue jsonValue when jsonValue.TryGetValue<int>(out var intValue) => intValue,
			_ => 0
		};
	}
}
