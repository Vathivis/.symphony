using Symphony.Domain;

namespace Symphony.Configuration;

public sealed class WorkflowRuntime : IDisposable {
	private readonly object _gate = new();
	private readonly WorkflowLoader _loader;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<WorkflowRuntime> _logger;
	private readonly string _workflowPath;
	private FileSystemWatcher? _watcher;
	private EffectiveWorkflow _current;
	private string? _lastReloadError;
	private DateTimeOffset _lastObservedWriteTimeUtc;
	private int _reloadQueued;

	public WorkflowRuntime(
		string workflowPath,
		EffectiveWorkflow initialWorkflow,
		WorkflowLoader loader,
		TimeProvider timeProvider,
		ILogger<WorkflowRuntime> logger) {
		_workflowPath = workflowPath;
		_current = initialWorkflow;
		_loader = loader;
		_timeProvider = timeProvider;
		_logger = logger;
		_lastObservedWriteTimeUtc = initialWorkflow.LastWriteTimeUtc;
	}

	public event Action<EffectiveWorkflow>? Changed;

	public EffectiveWorkflow Current {
		get {
			lock (_gate) {
				return _current;
			}
		}
	}

	public string? LastReloadError {
		get {
			lock (_gate) {
				return _lastReloadError;
			}
		}
	}

	public void StartWatching() {
		lock (_gate) {
			if (_watcher is not null) {
				return;
			}

			var directory = Path.GetDirectoryName(_workflowPath);
			var fileName = Path.GetFileName(_workflowPath);
			if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)) {
				return;
			}

			_watcher = new FileSystemWatcher(directory, fileName) {
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
				EnableRaisingEvents = true,
				IncludeSubdirectories = false
			};
			_watcher.Changed += OnWorkflowChanged;
			_watcher.Created += OnWorkflowChanged;
			_watcher.Renamed += OnWorkflowChanged;
			_watcher.Deleted += OnWorkflowChanged;
		}
	}

	public async Task EnsureFreshAsync(CancellationToken cancellationToken) {
		var exists = File.Exists(_workflowPath);
		var lastWrite = exists
			? new DateTimeOffset(File.GetLastWriteTimeUtc(_workflowPath), TimeSpan.Zero)
			: DateTimeOffset.MinValue;

		lock (_gate) {
			if (lastWrite <= _lastObservedWriteTimeUtc && exists) {
				return;
			}
		}

		await TryReloadAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<bool> TryReloadAsync(CancellationToken cancellationToken) {
		try {
			var updated = _loader.Load(_workflowPath, _timeProvider.GetUtcNow());
			if (!updated.Validation.IsValid) {
				lock (_gate) {
					_lastReloadError = $"{updated.Validation.ErrorCode}: {updated.Validation.Message}";
				}

				_logger.LogError(
					"action=workflow_reload outcome=failed error_code={ErrorCode} message={Message}",
					updated.Validation.ErrorCode,
					updated.Validation.Message);
				return false;
			}

			lock (_gate) {
				_current = updated;
				_lastObservedWriteTimeUtc = updated.LastWriteTimeUtc;
				_lastReloadError = null;
			}

			_logger.LogInformation(
				"action=workflow_reload outcome=completed workflow_path={WorkflowPath} loaded_at={LoadedAt}",
				updated.Path,
				updated.LoadedAt);
			Changed?.Invoke(updated);
			return true;
		} catch (WorkflowLoadException exception) {
			lock (_gate) {
				_lastReloadError = $"{exception.Code}: {exception.Message}";
			}

			_logger.LogError(
				"action=workflow_reload outcome=failed error_code={ErrorCode} message={Message}",
				exception.Code,
				exception.Message);
			return false;
		} finally {
			Interlocked.Exchange(ref _reloadQueued, 0);
		}
	}

	private void OnWorkflowChanged(object sender, FileSystemEventArgs eventArgs) {
		if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) {
			return;
		}

		_ = Task.Run(async () => {
			try {
				await Task.Delay(250).ConfigureAwait(false);
				await TryReloadAsync(CancellationToken.None).ConfigureAwait(false);
			} catch (Exception exception) {
				_logger.LogError(
					exception,
					"action=workflow_reload outcome=failed message={Message}",
					exception.Message);
			}
		});
	}

	public void Dispose() {
		lock (_gate) {
			if (_watcher is null) {
				return;
			}

			_watcher.EnableRaisingEvents = false;
			_watcher.Changed -= OnWorkflowChanged;
			_watcher.Created -= OnWorkflowChanged;
			_watcher.Renamed -= OnWorkflowChanged;
			_watcher.Deleted -= OnWorkflowChanged;
			_watcher.Dispose();
			_watcher = null;
		}
	}
}
