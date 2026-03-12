using System.Globalization;

namespace Symphony.Cli;

public sealed record CommandLineOptions(string WorkflowPath, int? Port);

public sealed record CommandLineParseResult(bool Success, CommandLineOptions? Options, string? Error);

public static class CommandLineOptionsParser {
	public static CommandLineParseResult Parse(IReadOnlyList<string> args, string currentDirectory) {
		ArgumentNullException.ThrowIfNull(args);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

		int? port = null;
		string? workflowPath = null;

		for (var index = 0; index < args.Count; index++) {
			var arg = args[index];
			if (string.Equals(arg, "--port", StringComparison.Ordinal)) {
				if (index + 1 >= args.Count) {
					return Error("Missing value for --port.");
				}

				index++;
				if (!TryParsePort(args[index], out var parsedPort)) {
					return Error($"Invalid --port value '{args[index]}'.");
				}

				port = parsedPort;
				continue;
			}

			if (arg.StartsWith("--port=", StringComparison.Ordinal)) {
				var value = arg["--port=".Length..];
				if (!TryParsePort(value, out var parsedPort)) {
					return Error($"Invalid --port value '{value}'.");
				}

				port = parsedPort;
				continue;
			}

			if (arg.StartsWith("-", StringComparison.Ordinal)) {
				return Error($"Unknown option '{arg}'.");
			}

			if (workflowPath is not null) {
				return Error("Only one positional WORKFLOW.md path is supported.");
			}

			workflowPath = arg;
		}

		var resolvedWorkflowPath = workflowPath is null
			? Path.GetFullPath(Path.Combine(currentDirectory, "WORKFLOW.md"))
			: Path.GetFullPath(workflowPath, currentDirectory);

		return new CommandLineParseResult(
			true,
			new CommandLineOptions(resolvedWorkflowPath, port),
			null);
	}

	private static bool TryParsePort(string value, out int port) {
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)) {
			return false;
		}

		return port >= 0;
	}

	private static CommandLineParseResult Error(string message) => new(false, null, message);
}
