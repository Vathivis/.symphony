# Symphony .NET

This repository is a .NET implementation of [OpenAI's Symphony](https://github.com/openai/symphony), based on the upstream [SPEC.md](https://github.com/openai/symphony/blob/main/SPEC.md).

Symphony turns project work into isolated, autonomous implementation runs. It watches a Linear project for candidate issues, creates one workspace per issue, launches Codex in app-server mode inside that workspace, and keeps the issue moving until the work is ready for handoff.

> [!WARNING]
> This is prototype orchestration software intended for trusted environments and evaluation.

## How it works

1. Polls Linear for candidate work.
2. Creates an isolated workspace per issue.
3. Launches `codex app-server` inside the workspace.
4. Renders a workflow prompt from `WORKFLOW.md`.
5. Keeps working the issue until it reaches a handoff point or leaves the active states.

If a claimed issue moves into a terminal state, Symphony stops the active worker and cleans up the matching workspace.

## Requirements

- .NET 10 SDK
- A valid `codex` installation with app-server support
- Access to a Linear workspace and project
- Git and any repository-specific tools your workflow hooks require

## How to use it

1. Make sure your repository is set up to work well with agents.
2. Create a local `WORKFLOW.md` from the checked-in example:

```powershell
Copy-Item WORKFLOW.md.example WORKFLOW.md
```

3. Edit `WORKFLOW.md` with your real Linear API key, project slug, workspace bootstrap hooks, and prompt template.
4. Start Symphony from the repository root:

```powershell
dotnet run --project .\Symphony.csproj
```

5. Optionally point Symphony at a different workflow file:

```powershell
dotnet run --project .\Symphony.csproj -- .\path\to\WORKFLOW.md
```

6. Optionally override the HTTP port from the command line:

```powershell
dotnet run --project .\Symphony.csproj -- --port 5050
```

If no workflow path is passed, Symphony defaults to `./WORKFLOW.md` in the current working directory.

## Workflow configuration

`WORKFLOW.md` uses YAML front matter for runtime settings and a Markdown body for the Codex prompt template.

The repository tracks `WORKFLOW.md.example` and ignores `WORKFLOW.md`, so you can keep real local secrets and project identifiers out of Git.

Minimal example:

```md
---
tracker:
  kind: linear
  endpoint: https://api.linear.app/graphql
  api_key: lin_api_...
  project_slug: your-linear-project-slug
workspace:
  root: ./workspaces
hooks:
  after_create: |
    git clone git@github.com:your-org/your-repo.git .
codex:
  command: codex app-server
server:
  port: 0
---
# Symphony Workflow

You are working on Linear issue `{{ issue.identifier }}`: `{{ issue.title }}`.

Issue description:

{{ issue.description }}
```

Notes:

- `tracker.kind` currently supports `linear`.
- `tracker.project_slug` maps to the Linear project `slugId`.
- `server.port` enables the optional web UI and JSON API. `0` asks the OS for an ephemeral port.
- Most changes to `WORKFLOW.md` are reloaded automatically while the service is running.
- If startup loading fails, Symphony exits with a typed configuration error.

## Web dashboard and API

When `server.port` is set in `WORKFLOW.md`, or a port is supplied with `--port`, Symphony starts a local ASP.NET Core server.

Available routes:

- `/` for the Blazor dashboard
- `/api/v1/state` for the full orchestrator snapshot
- `/api/v1/{issueIdentifier}` for one tracked issue snapshot
- `/api/v1/refresh` to trigger a refresh pass

## Project layout

- `Program.cs`: application entry point and DI setup
- `Configuration/`: workflow loading, parsing, and hot reload
- `Tracker/`: Linear integration
- `Orchestration/`: polling and worker lifecycle
- `Workspace/`: per-issue workspace management
- `Web/`: HTTP API mappings
- `Components/`: Blazor UI
- `Symphony.Tests/`: unit tests

## Testing

Run the test suite with:

```powershell
dotnet test .\Symphony.slnx
```

## Upstream references

- [OpenAI Symphony repository](https://github.com/openai/symphony)
- [OpenAI Symphony spec](https://github.com/openai/symphony/blob/main/SPEC.md)
- [OpenAI Symphony Elixir README](https://github.com/openai/symphony/blob/main/elixir/README.md)
