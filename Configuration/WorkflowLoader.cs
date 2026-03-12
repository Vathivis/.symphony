using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Symphony.Domain;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Symphony.Configuration;

public sealed class WorkflowLoader {
	private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

	public EffectiveWorkflow Load(string workflowPath, DateTimeOffset nowUtc) {
		ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);

		if (!File.Exists(workflowPath)) {
			throw new WorkflowLoadException(
				"missing_workflow_file",
				$"Workflow file '{workflowPath}' does not exist.");
		}

		string contents;
		try {
			contents = File.ReadAllText(workflowPath);
		} catch (Exception exception) {
			throw new WorkflowLoadException(
				"missing_workflow_file",
				$"Unable to read workflow file '{workflowPath}': {exception.Message}",
				exception);
		}

		var (frontMatter, promptBody) = SplitFrontMatter(contents);
		var configMap = ParseFrontMatter(frontMatter);
		var definition = new WorkflowDefinition(configMap, promptBody.Trim());
		var config = WorkflowConfigParser.Parse(configMap);
		var validation = WorkflowConfigParser.ValidateDispatch(config);
		var lastWrite = File.GetLastWriteTimeUtc(workflowPath);

		return new EffectiveWorkflow(
			Path.GetFullPath(workflowPath),
			definition,
			config,
			nowUtc,
			new DateTimeOffset(lastWrite, TimeSpan.Zero),
			validation);
	}

	private static (string? FrontMatter, string PromptBody) SplitFrontMatter(string contents) {
		using var reader = new StringReader(contents);
		var firstLine = reader.ReadLine();
		if (!string.Equals(firstLine, "---", StringComparison.Ordinal)) {
			return (null, contents);
		}

		var yamlLines = new List<string>();
		string? line;
		while ((line = reader.ReadLine()) is not null) {
			if (string.Equals(line, "---", StringComparison.Ordinal)) {
				var remainder = reader.ReadToEnd() ?? string.Empty;
				return (string.Join(Environment.NewLine, yamlLines), remainder);
			}

			yamlLines.Add(line);
		}

		throw new WorkflowLoadException(
			"workflow_parse_error",
			"Workflow front matter started with '---' but did not contain a closing '---'.");
	}

	private static IReadOnlyDictionary<string, object?> ParseFrontMatter(string? frontMatter) {
		if (string.IsNullOrWhiteSpace(frontMatter)) {
			return new Dictionary<string, object?>(StringComparer.Ordinal);
		}

		object? yamlRoot;
		try {
			yamlRoot = YamlDeserializer.Deserialize(new StringReader(frontMatter));
		} catch (YamlException exception) {
			throw new WorkflowLoadException(
				"workflow_parse_error",
				$"Unable to parse workflow YAML front matter: {exception.Message}",
				exception);
		}

		if (yamlRoot is null) {
			return new Dictionary<string, object?>(StringComparer.Ordinal);
		}

		if (yamlRoot is not IDictionary dictionary) {
			throw new WorkflowLoadException(
				"workflow_front_matter_not_a_map",
				"Workflow front matter must decode to a map/object.");
		}

		return NormalizeDictionary(dictionary);
	}

	private static IReadOnlyDictionary<string, object?> NormalizeDictionary(IDictionary source) {
		var result = new Dictionary<string, object?>(StringComparer.Ordinal);
		foreach (DictionaryEntry entry in source) {
			var key = entry.Key?.ToString();
			if (string.IsNullOrWhiteSpace(key)) {
				continue;
			}

			result[key] = NormalizeValue(entry.Value);
		}

		return result;
	}

	private static object? NormalizeValue(object? value) {
		switch (value) {
			case null:
				return null;
			case IDictionary dictionary:
				return NormalizeDictionary(dictionary);
			case IList list:
				var values = new List<object?>(list.Count);
				foreach (var item in list) {
					values.Add(NormalizeValue(item));
				}

				return values;
			default:
				return value;
		}
	}

	public static JsonNode? ToJsonNode(object? value) {
		return value switch {
			null => null,
			JsonNode node => node.DeepClone(),
			_ => JsonSerializer.SerializeToNode(value)
		};
	}
}
