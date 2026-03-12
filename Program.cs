using Symphony.Cli;
using Symphony.Components;
using Symphony.Configuration;
using Symphony.Orchestration;
using Symphony.Tracker;
using Symphony.Web;
using Symphony.Workspace;

var parseResult = CommandLineOptionsParser.Parse(args, Environment.CurrentDirectory);
if (!parseResult.Success || parseResult.Options is null) {
	Console.Error.WriteLine(parseResult.Error);
	Environment.ExitCode = 1;
	return;
}

var workflowLoader = new WorkflowLoader();
EffectiveWorkflow initialWorkflow;
try {
	initialWorkflow = workflowLoader.Load(parseResult.Options.WorkflowPath, TimeProvider.System.GetUtcNow());
} catch (WorkflowLoadException exception) {
	Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
	Environment.ExitCode = 1;
	return;
}

if (!initialWorkflow.Validation.IsValid) {
	Console.Error.WriteLine($"{initialWorkflow.Validation.ErrorCode}: {initialWorkflow.Validation.Message}");
	Environment.ExitCode = 1;
	return;
}

var builder = WebApplication.CreateBuilder([]);
var port = parseResult.Options.Port ?? initialWorkflow.Config.Server.Port;
if (port is not null) {
	builder.WebHost.UseUrls($"http://127.0.0.1:{port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}

builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();
builder.Services.AddHttpClient(nameof(LinearIssueTrackerClient), client => {
	client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(workflowLoader);
builder.Services.AddSingleton(initialWorkflow);
builder.Services.AddSingleton(parseResult.Options);
builder.Services.AddSingleton<WorkflowTemplateRenderer>();
builder.Services.AddSingleton(sp => new WorkflowRuntime(
	parseResult.Options.WorkflowPath,
	initialWorkflow,
	sp.GetRequiredService<WorkflowLoader>(),
	sp.GetRequiredService<TimeProvider>(),
	sp.GetRequiredService<ILogger<WorkflowRuntime>>()));
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<IIssueTrackerClient, LinearIssueTrackerClient>();
builder.Services.AddSingleton<AgentWorkerRunner>();
builder.Services.AddSingleton<SymphonyOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SymphonyOrchestrator>());

var app = builder.Build();
app.Services.GetRequiredService<WorkflowRuntime>().StartWatching();

if (!app.Environment.IsDevelopment()) {
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapSymphonyApi();
app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.Run();
