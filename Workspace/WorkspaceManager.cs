using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Symphony.Configuration;
using Symphony.Domain;

namespace Symphony.Workspace;

public sealed class WorkspaceManager {
	private readonly ILogger<WorkspaceManager> _logger;

	public WorkspaceManager(ILogger<WorkspaceManager> logger) {
		_logger = logger;
	}

	public async Task<WorkspaceDescriptor> CreateOrReuseAsync(
		string issueIdentifier,
		SymphonyRuntimeConfig config,
		CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrWhiteSpace(issueIdentifier);
		ArgumentNullException.ThrowIfNull(config);

		var workspaceRoot = Path.GetFullPath(config.Workspace.Root);
		Directory.CreateDirectory(workspaceRoot);

		var workspaceKey = SanitizeWorkspaceKey(issueIdentifier);
		var workspacePath = Path.GetFullPath(Path.Combine(workspaceRoot, workspaceKey));
		EnsureWithinRoot(workspaceRoot, workspacePath);

		if (File.Exists(workspacePath) && !Directory.Exists(workspacePath)) {
			throw new IOException($"Workspace path '{workspacePath}' exists but is not a directory.");
		}

		var createdNow = !Directory.Exists(workspacePath);
		if (createdNow) {
			Directory.CreateDirectory(workspacePath);
		}

		try {
			if (createdNow && !string.IsNullOrWhiteSpace(config.Hooks.AfterCreate)) {
				await RunHookAsync(
					"after_create",
					config.Hooks.AfterCreate,
					workspacePath,
					config.Hooks.TimeoutMs,
					cancellationToken,
					failureIsFatal: true).ConfigureAwait(false);
			}
		} catch {
			if (createdNow && Directory.Exists(workspacePath)) {
				try {
					Directory.Delete(workspacePath, recursive: true);
				} catch {
					// Best effort cleanup only.
				}
			}

			throw;
		}

		return new WorkspaceDescriptor(workspacePath, workspaceKey, createdNow);
	}

	public Task PrepareForRunAsync(string workspacePath, CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
		cancellationToken.ThrowIfCancellationRequested();

		DeleteTemporaryArtifact(Path.Combine(workspacePath, "tmp"));
		DeleteTemporaryArtifact(Path.Combine(workspacePath, ".elixir_ls"));
		return Task.CompletedTask;
	}

	public async Task RunBeforeRunHookAsync(
		string workspacePath,
		SymphonyRuntimeConfig config,
		CancellationToken cancellationToken) {
		if (!string.IsNullOrWhiteSpace(config.Hooks.BeforeRun)) {
			await RunHookAsync(
				"before_run",
				config.Hooks.BeforeRun,
				workspacePath,
				config.Hooks.TimeoutMs,
				cancellationToken,
				failureIsFatal: true).ConfigureAwait(false);
		}
	}

	public async Task RunAfterRunHookAsync(
		string workspacePath,
		SymphonyRuntimeConfig config,
		CancellationToken cancellationToken) {
		if (!string.IsNullOrWhiteSpace(config.Hooks.AfterRun)) {
			await RunHookAsync(
				"after_run",
				config.Hooks.AfterRun,
				workspacePath,
				config.Hooks.TimeoutMs,
				cancellationToken,
				failureIsFatal: false).ConfigureAwait(false);
		}
	}

	public async Task RemoveWorkspaceAsync(
		string issueIdentifier,
		SymphonyRuntimeConfig config,
		CancellationToken cancellationToken) {
		ArgumentException.ThrowIfNullOrWhiteSpace(issueIdentifier);
		ArgumentNullException.ThrowIfNull(config);

		var workspaceRoot = Path.GetFullPath(config.Workspace.Root);
		var workspaceKey = SanitizeWorkspaceKey(issueIdentifier);
		var workspacePath = Path.GetFullPath(Path.Combine(workspaceRoot, workspaceKey));
		EnsureWithinRoot(workspaceRoot, workspacePath);

		if (!Directory.Exists(workspacePath)) {
			return;
		}

		if (!string.IsNullOrWhiteSpace(config.Hooks.BeforeRemove)) {
			await RunHookAsync(
				"before_remove",
				config.Hooks.BeforeRemove,
				workspacePath,
				config.Hooks.TimeoutMs,
				cancellationToken,
				failureIsFatal: false).ConfigureAwait(false);
		}

		Directory.Delete(workspacePath, recursive: true);
	}

	public static string SanitizeWorkspaceKey(string issueIdentifier) {
		var builder = new StringBuilder(issueIdentifier.Length);
		foreach (var character in issueIdentifier) {
			builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
				? character
				: '_');
		}

		return builder.ToString();
	}

	public static void EnsureWithinRoot(string workspaceRoot, string workspacePath) {
		var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		var normalizedWorkspace = Path.GetFullPath(workspacePath);

		if (string.Equals(normalizedRoot, normalizedWorkspace, StringComparison.OrdinalIgnoreCase)) {
			return;
		}

		var prefix = normalizedRoot + Path.DirectorySeparatorChar;
		if (!normalizedWorkspace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
			throw new InvalidOperationException(
				$"Workspace path '{normalizedWorkspace}' escapes workspace root '{normalizedRoot}'.");
		}
	}

	private void DeleteTemporaryArtifact(string path) {
		try {
			if (Directory.Exists(path)) {
				Directory.Delete(path, recursive: true);
			}
		} catch (Exception exception) {
			_logger.LogWarning(
				exception,
				"action=workspace_prepare outcome=warning path={Path} message={Message}",
				path,
				exception.Message);
		}
	}

	private async Task RunHookAsync(
		string hookName,
		string script,
		string workspacePath,
		int timeoutMs,
		CancellationToken cancellationToken,
		bool failureIsFatal) {
		_logger.LogInformation(
			"action=workspace_hook outcome=started hook={HookName} workspace={WorkspacePath}",
			hookName,
			workspacePath);

		using var process = CreateHookProcess(script, workspacePath);
		try {
			process.Start();
		} catch (Exception exception) {
			if (failureIsFatal) {
				throw new WorkspaceHookException(
					hookName,
					$"Unable to start hook '{hookName}': {exception.Message}",
					exception);
			}

			_logger.LogWarning(
				exception,
				"action=workspace_hook outcome=failed hook={HookName} message={Message}",
				hookName,
				exception.Message);
			return;
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

		try {
			await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
		} catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) {
			try {
				if (!process.HasExited) {
					process.Kill(entireProcessTree: true);
				}
			} catch {
				// Ignore best effort kill failures.
			}

			if (failureIsFatal) {
				throw new WorkspaceHookException(
					hookName,
					$"Hook '{hookName}' timed out after {timeoutMs} ms.",
					exception);
			}

			_logger.LogWarning(
				exception,
				"action=workspace_hook outcome=failed hook={HookName} message={Message}",
				hookName,
				$"timed out after {timeoutMs} ms");
			return;
		}

		var stdout = await stdoutTask.ConfigureAwait(false);
		var stderr = await stderrTask.ConfigureAwait(false);
		if (process.ExitCode == 0) {
			_logger.LogInformation(
				"action=workspace_hook outcome=completed hook={HookName} workspace={WorkspacePath}",
				hookName,
				workspacePath);
			return;
		}

		var message = TruncateForLogs(string.Join(
			Environment.NewLine,
			new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text))));

		if (failureIsFatal) {
			throw new WorkspaceHookException(
				hookName,
				$"Hook '{hookName}' failed with exit code {process.ExitCode}. Output: {message}");
		}

		_logger.LogWarning(
			"action=workspace_hook outcome=failed hook={HookName} exit_code={ExitCode} message={Message}",
			hookName,
			process.ExitCode,
			message);
	}

	private static Process CreateHookProcess(string script, string workspacePath) {
		var startInfo = new ProcessStartInfo {
			WorkingDirectory = workspacePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			startInfo.FileName = "pwsh";
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add(script);
		} else {
			startInfo.FileName = "bash";
			startInfo.ArgumentList.Add("-lc");
			startInfo.ArgumentList.Add(script);
		}

		return new Process {
			StartInfo = startInfo
		};
	}

	private static string TruncateForLogs(string text) {
		if (text.Length <= 2048) {
			return text;
		}

		return text[..2048] + "...";
	}
}
