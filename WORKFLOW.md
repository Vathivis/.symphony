---
tracker:
  kind: linear
  endpoint: https://api.linear.app/graphql
  api_key: $LINEAR_API_KEY
  project_slug: b1916cca5a7a
  active_states:
    - Todo
    - In Progress
  terminal_states:
    - Closed
    - Cancelled
    - Canceled
    - Duplicate
    - Done
polling:
  interval_ms: 30000
workspace:
  root: ./workspaces
hooks:
  timeout_ms: 60000
agent:
  max_concurrent_agents: 10
  max_turns: 20
  max_retry_backoff_ms: 300000
codex:
  command: codex app-server
  approval_policy: never
  thread_sandbox: danger-full-access
  turn_sandbox_policy:
    type: dangerFullAccess
server:
  port: 0
---
# Symphony Workflow

You are working on Linear issue `{{ issue.identifier }}`: `{{ issue.title }}`.

Repository workflow contract:

- Work only inside the assigned workspace.
- Inspect the issue description and current repository state before making changes.
- Run relevant validation before finishing.
- Stop when the issue is no longer in an active state or when the work is ready for the workflow-defined handoff.

Issue description:

{{ issue.description }}
