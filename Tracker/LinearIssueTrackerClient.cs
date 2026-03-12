using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Symphony.Configuration;
using Symphony.Domain;

namespace Symphony.Tracker;

public sealed class LinearIssueTrackerClient : IIssueTrackerClient {
	internal const string CandidateIssuesQuery = """
		query FetchCandidateIssues($projectSlug: String!, $states: [String!]!, $after: String, $first: Int!) {
		  issues(
		    first: $first,
		    after: $after,
		    filter: {
		      project: { slugId: { eq: $projectSlug } }
		      state: { name: { in: $states } }
		    }
		  ) {
		    pageInfo {
		      hasNextPage
		      endCursor
		    }
		    nodes {
		      id
		      identifier
		      title
		      description
		      priority
		      branchName
		      url
		      createdAt
		      updatedAt
		      state { name }
		      labels { nodes { name } }
		      inverseRelations {
		        nodes {
		          type
		          issue {
		            id
		            identifier
		            state { name }
		          }
		          relatedIssue {
		            id
		            identifier
		            state { name }
		          }
		        }
		      }
		    }
		  }
		}
		""";

	internal const string IssuesByStatesQuery = """
		query FetchIssuesByStates($projectSlug: String!, $states: [String!]!, $after: String, $first: Int!) {
		  issues(
		    first: $first,
		    after: $after,
		    filter: {
		      project: { slugId: { eq: $projectSlug } }
		      state: { name: { in: $states } }
		    }
		  ) {
		    pageInfo {
		      hasNextPage
		      endCursor
		    }
		    nodes {
		      id
		      identifier
		      title
		      description
		      priority
		      branchName
		      url
		      createdAt
		      updatedAt
		      state { name }
		      labels { nodes { name } }
		      inverseRelations {
		        nodes {
		          type
		          issue {
		            id
		            identifier
		            state { name }
		          }
		          relatedIssue {
		            id
		            identifier
		            state { name }
		          }
		        }
		      }
		    }
		  }
		}
		""";

	internal const string IssueStatesByIdsQuery = """
		query FetchIssueStatesByIds($ids: [ID!]) {
		  issues(
		    first: 250,
		    filter: {
		      id: { in: $ids }
		    }
		  ) {
		    nodes {
		      id
		      identifier
		      title
		      description
		      priority
		      branchName
		      url
		      createdAt
		      updatedAt
		      state { name }
		      labels { nodes { name } }
		      inverseRelations {
		        nodes {
		          type
		          issue {
		            id
		            identifier
		            state { name }
		          }
		          relatedIssue {
		            id
		            identifier
		            state { name }
		          }
		        }
		      }
		    }
		  }
		}
		""";

	private const int PageSize = 50;

	private readonly WorkflowRuntime _workflowRuntime;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<LinearIssueTrackerClient> _logger;

	public LinearIssueTrackerClient(
		WorkflowRuntime workflowRuntime,
		IHttpClientFactory httpClientFactory,
		ILogger<LinearIssueTrackerClient> logger) {
		_workflowRuntime = workflowRuntime;
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	public Task<IReadOnlyList<IssueRecord>> FetchCandidateIssuesAsync(CancellationToken cancellationToken) {
		var workflow = _workflowRuntime.Current;
		return FetchPagedIssuesByStatesAsync(
			workflow.Config,
			workflow.Config.Tracker.ActiveStates,
			CandidateIssuesQuery,
			cancellationToken);
	}

	public Task<IReadOnlyList<IssueRecord>> FetchIssuesByStatesAsync(
		IReadOnlyList<string> stateNames,
		CancellationToken cancellationToken) {
		if (stateNames.Count == 0) {
			return Task.FromResult<IReadOnlyList<IssueRecord>>([]);
		}

		var workflow = _workflowRuntime.Current;
		return FetchPagedIssuesByStatesAsync(
			workflow.Config,
			stateNames,
			IssuesByStatesQuery,
			cancellationToken);
	}

	public async Task<IReadOnlyList<IssueRecord>> FetchIssueStatesByIdsAsync(
		IReadOnlyList<string> issueIds,
		CancellationToken cancellationToken) {
		if (issueIds.Count == 0) {
			return [];
		}

		var workflow = _workflowRuntime.Current;
		var payload = await ExecuteGraphQlAsync(
			workflow.Config,
			IssueStatesByIdsQuery,
			new Dictionary<string, object?> {
				["ids"] = issueIds
			},
			cancellationToken).ConfigureAwait(false);

		if (!payload.RootElement.TryGetProperty("data", out var dataElement) ||
		    !dataElement.TryGetProperty("issues", out var issuesElement) ||
		    !issuesElement.TryGetProperty("nodes", out var nodesElement) ||
		    nodesElement.ValueKind != JsonValueKind.Array) {
			throw new TrackerException(
				"linear_unknown_payload",
				"Linear issue-state payload did not contain data.issues.nodes.");
		}

		return ReadIssues(nodesElement);
	}

	private async Task<IReadOnlyList<IssueRecord>> FetchPagedIssuesByStatesAsync(
		SymphonyRuntimeConfig config,
		IReadOnlyList<string> stateNames,
		string query,
		CancellationToken cancellationToken) {
		EnsureLinearConfig(config);

		var issues = new List<IssueRecord>();
		string? after = null;

		while (true) {
			var payload = await ExecuteGraphQlAsync(
				config,
				query,
				new Dictionary<string, object?> {
					["projectSlug"] = config.Tracker.ProjectSlug,
					["states"] = stateNames,
					["after"] = after,
					["first"] = PageSize
				},
				cancellationToken).ConfigureAwait(false);

			if (!payload.RootElement.TryGetProperty("data", out var dataElement) ||
			    !dataElement.TryGetProperty("issues", out var issuesElement)) {
				throw new TrackerException(
					"linear_unknown_payload",
					"Linear issues payload did not contain data.issues.");
			}

			if (!issuesElement.TryGetProperty("nodes", out var nodesElement) ||
			    nodesElement.ValueKind != JsonValueKind.Array) {
				throw new TrackerException(
					"linear_unknown_payload",
					"Linear issues payload did not contain nodes.");
			}

			issues.AddRange(ReadIssues(nodesElement));

			if (!issuesElement.TryGetProperty("pageInfo", out var pageInfoElement)) {
				break;
			}

			var hasNextPage = pageInfoElement.TryGetProperty("hasNextPage", out var hasNextPageElement) &&
			                  hasNextPageElement.ValueKind == JsonValueKind.True;
			if (!hasNextPage) {
				break;
			}

			if (!pageInfoElement.TryGetProperty("endCursor", out var endCursorElement) ||
			    endCursorElement.ValueKind == JsonValueKind.Null ||
			    string.IsNullOrWhiteSpace(endCursorElement.GetString())) {
				throw new TrackerException(
					"linear_missing_end_cursor",
					"Linear reported hasNextPage=true without a non-empty endCursor.");
			}

			after = endCursorElement.GetString();
		}

		return issues;
	}

	private async Task<JsonDocument> ExecuteGraphQlAsync(
		SymphonyRuntimeConfig config,
		string query,
		Dictionary<string, object?> variables,
		CancellationToken cancellationToken) {
		EnsureLinearConfig(config);
		using var request = new HttpRequestMessage(HttpMethod.Post, config.Tracker.Endpoint) {
			Content = JsonContent.Create(new {
				query,
				variables
			})
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Tracker.ApiKey);

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(30_000));

		var client = _httpClientFactory.CreateClient(nameof(LinearIssueTrackerClient));
		HttpResponseMessage response;
		try {
			response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
		} catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) {
			throw new TrackerException(
				"linear_api_request",
				$"Linear request timed out after 30000 ms: {exception.Message}",
				exception);
		} catch (Exception exception) {
			throw new TrackerException(
				"linear_api_request",
				$"Linear request failed: {exception.Message}",
				exception);
		}

		await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		JsonDocument payload;
		try {
			payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
		} catch (Exception exception) {
			throw new TrackerException(
				"linear_unknown_payload",
				$"Linear response body was not valid JSON: {exception.Message}",
				exception);
		}

		if (!response.IsSuccessStatusCode) {
			throw new TrackerException(
				"linear_api_status",
				$"Linear returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
		}

		if (payload.RootElement.TryGetProperty("errors", out var errorsElement) &&
		    errorsElement.ValueKind == JsonValueKind.Array &&
		    errorsElement.GetArrayLength() > 0) {
			throw new TrackerException(
				"linear_graphql_errors",
				$"Linear GraphQL returned errors: {errorsElement}");
		}

		return payload;
	}

	private IReadOnlyList<IssueRecord> ReadIssues(JsonElement nodesElement) {
		var issues = new List<IssueRecord>();
		foreach (var node in nodesElement.EnumerateArray()) {
			var issue = ReadIssue(node);
			if (issue is not null) {
				issues.Add(issue);
			}
		}

		return issues;
	}

	private IssueRecord? ReadIssue(JsonElement node) {
		var id = ReadString(node, "id");
		var identifier = ReadString(node, "identifier");
		var title = ReadString(node, "title");
		var stateName = node.TryGetProperty("state", out var stateElement)
			? ReadString(stateElement, "name")
			: null;

		if (string.IsNullOrWhiteSpace(id) ||
		    string.IsNullOrWhiteSpace(identifier) ||
		    string.IsNullOrWhiteSpace(title) ||
		    string.IsNullOrWhiteSpace(stateName)) {
			_logger.LogWarning(
				"action=linear_normalize outcome=skipped reason=missing_required_fields");
			return null;
		}

		return new IssueRecord(
			id,
			identifier,
			title,
			ReadNullableString(node, "description"),
			ReadPriority(node),
			stateName,
			ReadNullableString(node, "branchName"),
			ReadNullableString(node, "url"),
			ReadLabels(node),
			ReadBlockedBy(node, id),
			ReadTimestamp(node, "createdAt"),
			ReadTimestamp(node, "updatedAt"));
	}

	private static IReadOnlyList<string> ReadLabels(JsonElement issueNode) {
		if (!issueNode.TryGetProperty("labels", out var labelsElement) ||
		    !labelsElement.TryGetProperty("nodes", out var nodesElement) ||
		    nodesElement.ValueKind != JsonValueKind.Array) {
			return [];
		}

		return nodesElement
			.EnumerateArray()
			.Select(labelNode => ReadString(labelNode, "name"))
			.Where(label => !string.IsNullOrWhiteSpace(label))
			.Select(label => label!.Trim().ToLowerInvariant())
			.ToArray();
	}

	private static IReadOnlyList<BlockerReference> ReadBlockedBy(JsonElement issueNode, string currentIssueId) {
		if (!issueNode.TryGetProperty("inverseRelations", out var inverseRelationsElement) ||
		    !inverseRelationsElement.TryGetProperty("nodes", out var relationNodesElement) ||
		    relationNodesElement.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var blockers = new List<BlockerReference>();
		foreach (var relationNode in relationNodesElement.EnumerateArray()) {
			var relationType = ReadString(relationNode, "type");
			if (!string.Equals(relationType, "blocks", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			var blockerNode = relationNode.TryGetProperty("issue", out var issueElement) &&
			                  !string.Equals(ReadString(issueElement, "id"), currentIssueId, StringComparison.Ordinal)
				? issueElement
				: relationNode.TryGetProperty("relatedIssue", out var relatedIssueElement)
					? relatedIssueElement
					: default;

			if (blockerNode.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) {
				blockers.Add(new BlockerReference(null, null, null));
				continue;
			}

			blockers.Add(new BlockerReference(
				ReadNullableString(blockerNode, "id"),
				ReadNullableString(blockerNode, "identifier"),
				blockerNode.TryGetProperty("state", out var blockerStateElement)
					? ReadNullableString(blockerStateElement, "name")
					: null));
		}

		return blockers;
	}

	private static int? ReadPriority(JsonElement issueNode) {
		if (!issueNode.TryGetProperty("priority", out var priorityElement)) {
			return null;
		}

		return priorityElement.ValueKind switch {
			JsonValueKind.Number when priorityElement.TryGetInt32(out var intValue) => intValue,
			JsonValueKind.String when int.TryParse(priorityElement.GetString(), out var parsed) => parsed,
			_ => null
		};
	}

	private static DateTimeOffset? ReadTimestamp(JsonElement node, string propertyName) {
		var text = ReadNullableString(node, propertyName);
		return DateTimeOffset.TryParse(text, out var timestamp) ? timestamp : null;
	}

	private static string? ReadString(JsonElement node, string propertyName) {
		return node.TryGetProperty(propertyName, out var propertyElement)
			? ReadNullableString(propertyElement)
			: null;
	}

	private static string? ReadNullableString(JsonElement node, string propertyName) {
		return node.TryGetProperty(propertyName, out var propertyElement)
			? ReadNullableString(propertyElement)
			: null;
	}

	private static string? ReadNullableString(JsonElement value) {
		return value.ValueKind switch {
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Null => null,
			_ => value.ToString()
		};
	}

	private static void EnsureLinearConfig(SymphonyRuntimeConfig config) {
		if (!string.Equals(config.Tracker.Kind, "linear", StringComparison.OrdinalIgnoreCase)) {
			throw new TrackerException(
				"unsupported_tracker_kind",
				$"Unsupported tracker.kind '{config.Tracker.Kind}'.");
		}

		if (string.IsNullOrWhiteSpace(config.Tracker.ApiKey)) {
			throw new TrackerException(
				"missing_tracker_api_key",
				"tracker.api_key is missing.");
		}

		if (string.IsNullOrWhiteSpace(config.Tracker.ProjectSlug)) {
			throw new TrackerException(
				"missing_tracker_project_slug",
				"tracker.project_slug is missing.");
		}
	}
}
