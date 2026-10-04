## vexp - Context-Aware AI Coding <!-- vexp v3.3.2 -->

### Context strategy: call run_pipeline ONCE at task start
If the task already names the files/symbols to touch, SKIP vexp. Otherwise one
`run_pipeline({ "task": "..." })` returns ranked pivot files with line ranges and
blast radius. Do NOT open files one by one to find your way around - every extra
tool call costs a turn. Call it again ONLY when the task moves to a new area.
`get_skeleton` for files to understand, not edit. `verify_done` before calling a
multi-file task complete, then RUN the tests it names.

### Query shape (do this)
Anchor the task on real identifiers (ClassName, functionName) or file paths:
`run_pipeline({ "task": "fix JWT expiry in AuthService.validateToken" })`

vexp runs entirely on this machine, index in `.vexp/`;
`run_pipeline` transmits nothing to any external service.
On `status: "degraded"` or 0 pivots the index is still building - use your own tools.
For literal string sweeps use your native search - do NOT route text sweeps through vexp.
Repo SOURCE only: logs, dist/, node_modules/ and files outside the repo are NOT indexed.
<!-- /vexp -->
## Agent team
Project subagents live in `.claude/agents/`. All six have the vexp MCP tools
(`run_pipeline`, `get_skeleton`, `verify_done`) and follow the vexp rules above.

| Agent | Role | Output |
|---|---|---|
| `planner` | Designs the change and checks it against `StartupController/PRD.MD` | `docs/plans/<date>-<slug>.md` |
| `developer` | Implements the plan in C#/WinForms and keeps `dotnet build` green | Code changes + handoff notes |
| `tester` | Owns `StartupController.Tests` (xUnit), reviews the diff, runs `dotnet test` | Tests + pass/fail report |
| `documenter` | Updates README, user README, PRD, CHANGELOG, help text | Docs changes |
| `code-inspector` | Read-only code review: bugs, leaks, UI threading, duplication, style, `dotnet format` | Must/Should/Nitpick report |
| `security-analyser` | Read-only security review: process launching, registry trust, elevation, logging, installer, NuGet CVEs | Severity-ranked findings |

Default flow for a feature or non-trivial fix: **planner → developer →
tester + code-inspector + security-analyser (in parallel) → developer fixes
findings → documenter**. Always run `security-analyser` for changes touching the
registry, process launching, elevation, logging, or the installer, and before a release. Pass each agent the previous
agent's report and the plan path. Small, well-defined changes can skip the planner.

Hard rule for every agent: never modify this machine's real startup
configuration (`HKCU\...\Run`, `StartupApproved`, `HKCU\Software\StartupController`)
or launch real startup programs while developing or testing.
