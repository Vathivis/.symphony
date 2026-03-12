using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Symphony.Configuration;

public static class WorkflowConfigParser {
	private static readonly string[] DefaultActiveStates = ["Todo", "In Progress"];
	private static readonly string[] DefaultTerminalStates = ["Closed", "Cancelled", "Canceled", "Duplicate", "Done"];

	public static SymphonyRuntimeConfig Parse(IReadOnlyDictionary<string, object?> config) {
		ArgumentNullException.ThrowIfNull(config);

		var tracker = GetSection(config, "tracker");
		var polling = GetSection(config, "polling");
		var workspace = GetSection(config, "workspace");
		var hooks = GetSection(config, "hooks");
		var agent = GetSection(config, "agent");
		var codex = GetSection(config, "codex");
		var server = GetSection(config, "server");

		var trackerKind = GetString(tracker, "kind") ?? "linear";
		var endpoint = GetString(tracker, "endpoint") ?? "https://api.linear.app/graphql";
		var apiKey = ResolveConfiguredValue(GetString(tracker, "api_key"), "LINEAR_API_KEY");
		var projectSlug = ResolveConfiguredValue(GetString(tracker, "project_slug"), "LINEAR_PROJECT_SLUG");
		var activeStates = ReadStringList(tracker, "active_states", DefaultActiveStates);
		var terminalStates = ReadStringList(tracker, "terminal_states", DefaultTerminalStates);

		var workspaceRoot = NormalizeWorkspaceRoot(GetString(workspace, "root"));
		var timeoutMs = ReadInt(hooks, "timeout_ms") is { } parsedTimeout && parsedTimeout > 0
			? parsedTimeout
			: 60_000;

		var maxConcurrentAgents = ReadInt(agent, "max_concurrent_agents") is { } parsedConcurrency && parsedConcurrency > 0
			? parsedConcurrency
			: 10;
		var maxTurns = ReadInt(agent, "max_turns") is { } parsedMaxTurns && parsedMaxTurns > 0
			? parsedMaxTurns
			: 20;
		var maxRetryBackoffMs = ReadInt(agent, "max_retry_backoff_ms") is { } parsedBackoff && parsedBackoff > 0
			? parsedBackoff
			: 300_000;

		return new SymphonyRuntimeConfig(
			new TrackerRuntimeConfig(
				trackerKind,
				endpoint,
				apiKey,
				projectSlug,
				activeStates,
				terminalStates,
				new HashSet<string>(activeStates.Select(NormalizeState), StringComparer.Ordinal),
				new HashSet<string>(terminalStates.Select(NormalizeState), StringComparer.Ordinal)),
			new PollingRuntimeConfig(
				ReadInt(polling, "interval_ms") is { } parsedPolling && parsedPolling > 0 ? parsedPolling : 30_000),
			new WorkspaceRuntimeConfig(workspaceRoot),
			new HooksRuntimeConfig(
				GetString(hooks, "after_create"),
				GetString(hooks, "before_run"),
				GetString(hooks, "after_run"),
				GetString(hooks, "before_remove"),
				timeoutMs),
			new AgentRuntimeConfig(
				maxConcurrentAgents,
				maxTurns,
				maxRetryBackoffMs,
				ReadStateConcurrencyMap(agent, "max_concurrent_agents_by_state")),
			new CodexRuntimeConfig(
				GetString(codex, "command") ?? "codex app-server",
				WorkflowLoader.ToJsonNode(GetValue(codex, "approval_policy")) ?? JsonValue.Create("never"),
				GetString(codex, "thread_sandbox") ?? "danger-full-access",
				WorkflowLoader.ToJsonNode(GetValue(codex, "turn_sandbox_policy")) ?? new JsonObject {
					["type"] = "dangerFullAccess"
				},
				ReadInt(codex, "turn_timeout_ms") is { } turnTimeout && turnTimeout > 0 ? turnTimeout : 3_600_000,
				ReadInt(codex, "read_timeout_ms") is { } readTimeout && readTimeout > 0 ? readTimeout : 5_000,
				ReadInt(codex, "stall_timeout_ms") ?? 300_000),
			new ServerRuntimeConfig(
				ReadInt(server, "port")));
	}

	public static DispatchValidationResult ValidateDispatch(SymphonyRuntimeConfig config) {
		if (string.IsNullOrWhiteSpace(config.Tracker.Kind)) {
			return Invalid("unsupported_tracker_kind", "tracker.kind is required.");
		}

		if (!string.Equals(config.Tracker.Kind, "linear", StringComparison.OrdinalIgnoreCase)) {
			return Invalid("unsupported_tracker_kind", $"Unsupported tracker.kind '{config.Tracker.Kind}'.");
		}

		if (string.IsNullOrWhiteSpace(config.Tracker.ApiKey)) {
			return Invalid("missing_tracker_api_key", "tracker.api_key is required for Linear dispatch.");
		}

		if (string.IsNullOrWhiteSpace(config.Tracker.ProjectSlug)) {
			return Invalid("missing_tracker_project_slug", "tracker.project_slug is required for Linear dispatch.");
		}

		if (string.IsNullOrWhiteSpace(config.Codex.Command)) {
			return Invalid("missing_codex_command", "codex.command must be present and non-empty.");
		}

		return new DispatchValidationResult(true, null, null);
	}

	public static string NormalizeState(string state) => state.Trim().ToLowerInvariant();

	private static DispatchValidationResult Invalid(string code, string message) => new(false, code, message);

	private static IReadOnlyDictionary<string, object?> GetSection(IReadOnlyDictionary<string, object?> root, string key) {
		if (root.TryGetValue(key, out var section) && section is IReadOnlyDictionary<string, object?> typed) {
			return typed;
		}

		if (root.TryGetValue(key, out var dictionary) && dictionary is Dictionary<string, object?> mutable) {
			return new ReadOnlyDictionary<string, object?>(mutable);
		}

		return new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
	}

	private static string? GetString(IReadOnlyDictionary<string, object?> section, string key) {
		var value = GetValue(section, key);
		return value?.ToString();
	}

	private static object? GetValue(IReadOnlyDictionary<string, object?> section, string key) {
		return section.TryGetValue(key, out var value) ? value : null;
	}

	private static int? ReadInt(IReadOnlyDictionary<string, object?> section, string key) {
		var value = GetValue(section, key);
		return value switch {
			null => null,
			int intValue => intValue,
			long longValue when longValue is <= int.MaxValue and >= int.MinValue => (int)longValue,
			string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
			_ => null
		};
	}

	private static IReadOnlyList<string> ReadStringList(
		IReadOnlyDictionary<string, object?> section,
		string key,
		IReadOnlyList<string> defaults) {
		var value = GetValue(section, key);
		if (value is null) {
			return defaults.ToArray();
		}

		if (value is string stringValue) {
			return stringValue
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(entry => !string.IsNullOrWhiteSpace(entry))
				.ToArray();
		}

		if (value is IEnumerable<object?> objectValues) {
			return objectValues
				.Select(item => item?.ToString())
				.Where(item => !string.IsNullOrWhiteSpace(item))
				.Select(item => item!)
				.ToArray();
		}

		return defaults.ToArray();
	}

	private static IReadOnlyDictionary<string, int> ReadStateConcurrencyMap(
		IReadOnlyDictionary<string, object?> section,
		string key) {
		IReadOnlyDictionary<string, object?>? typedSection = null;
		Dictionary<string, object?>? mutableSection = null;
		var sectionValue = GetValue(section, key);
		if (sectionValue is IReadOnlyDictionary<string, object?> readOnlyDictionary) {
			typedSection = readOnlyDictionary;
		} else if (sectionValue is Dictionary<string, object?> dictionary) {
			mutableSection = dictionary;
		}

		if (typedSection is null && mutableSection is null) {
			return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));
		}

		var source = typedSection ?? mutableSection!;
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var (state, rawValue) in source) {
			var parsed = rawValue switch {
				int intValue => intValue,
				long longValue when longValue is <= int.MaxValue and >= int.MinValue => (int)longValue,
				string stringValue when int.TryParse(stringValue, out var stringParsed) => stringParsed,
				_ => (int?)null
			};

			if (parsed is > 0) {
				result[NormalizeState(state)] = parsed.Value;
			}
		}

		return new ReadOnlyDictionary<string, int>(result);
	}

	private static string NormalizeWorkspaceRoot(string? value) {
		var resolved = ResolvePathLike(value);
		if (!string.IsNullOrWhiteSpace(resolved)) {
			return resolved;
		}

		return Path.Combine(Path.GetTempPath(), "symphony_workspaces");
	}

	private static string? ResolveConfiguredValue(string? configuredValue, string canonicalEnvironmentVariable) {
		if (string.IsNullOrWhiteSpace(configuredValue)) {
			return EmptyAsNull(Environment.GetEnvironmentVariable(canonicalEnvironmentVariable));
		}

		if (configuredValue.StartsWith("$", StringComparison.Ordinal)) {
			return EmptyAsNull(Environment.GetEnvironmentVariable(configuredValue[1..]));
		}

		return configuredValue;
	}

	private static string? ResolvePathLike(string? value) {
		if (string.IsNullOrWhiteSpace(value)) {
			return null;
		}

		var expanded = value.StartsWith("$", StringComparison.Ordinal)
			? Environment.GetEnvironmentVariable(value[1..]) ?? string.Empty
			: value;

		if (string.IsNullOrWhiteSpace(expanded)) {
			return null;
		}

		if (expanded.StartsWith("~", StringComparison.Ordinal)) {
			var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			expanded = Path.Combine(homePath, expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		}

		return expanded;
	}

	private static string? EmptyAsNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
