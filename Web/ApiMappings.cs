using Symphony.Orchestration;

namespace Symphony.Web;

public static class ApiMappings {
	public static IEndpointRouteBuilder MapSymphonyApi(this IEndpointRouteBuilder endpoints) {
		var group = endpoints.MapGroup("/api/v1");

		group.MapGet("/state", (SymphonyOrchestrator orchestrator) => Results.Ok(orchestrator.GetSnapshot()));

		group.MapGet("/{issueIdentifier}", (string issueIdentifier, SymphonyOrchestrator orchestrator) => {
			var snapshot = orchestrator.GetIssueSnapshot(issueIdentifier);
			return snapshot is null
				? Results.NotFound(new {
					error = new {
						code = "issue_not_found",
						message = $"Issue '{issueIdentifier}' is not tracked in the current runtime state."
					}
				})
				: Results.Ok(snapshot);
		});

		group.MapPost("/refresh", (SymphonyOrchestrator orchestrator) => Results.Accepted(
			value: orchestrator.RequestRefresh()));

		return endpoints;
	}
}
