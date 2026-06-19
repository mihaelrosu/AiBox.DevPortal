# AiBox.DevPortal Roadmap

## Metadata

* SourceType: Roadmap
* Status: Active
* Owner: DevPortal
* LastUpdated: 2026-06-19
* Tags: roadmap, planning, project-history-index

## Vision

AiBox.DevPortal is a local agentic development environment for the AiBox workspace.
It combines local Ollama models, Blazor, and Radzen UI with planning, patch generation,
verification, review, safe apply, rollback, dashboard metrics, and project memory so development
work can be tracked and repeated safely.

## Current Status

Completed:

* Agent Mode Profiles
* Run History
* Patch Queue
* Project Knowledge Index
* Project History Index
* Context Suggestions
* Task Plan / Slice Workflow
* Slice Verification
* Risk Analysis
* Risk Gates
* Multi-Slice Apply
* Apply Audit Log
* Patch Rollback Service
* Agent Dashboard
* Agent Model Routing

Current:

* Multi-Model Intelligence

## Related Files

* Models/ProjectHistoryItem.cs
* Models/ProjectHistoryIndex.cs
* Models/ProjectHistorySummary.cs
* Services/ProjectHistoryIndexService.cs
* Components/Coder/ProjectHistoryPanel.razor

## Acceptance Criteria

* Major phases use stable IDs.
* Completed items are easy to index from bullet lists.
* Planned items are grouped under canonical feature names.

## V2.1 Reliable Patch Generation

* Id: v2-1-reliable-patch-generation
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: patch-builder, validation, repair

Dependencies:

* Patch Builder
* Validation pipeline

Implementation notes:

* Patch preview generation is now hardened against malformed intent and missing safe targets.
* Preview-only generation remains separate from apply authorization.

## V2.2 Planner -> Slice Workflow

* Id: v2-2-planner-slice-workflow
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: planner, task-plan, task-slice

Dependencies:

* Task Plan
* Task Slice

Implementation notes:

* Task planning now flows through canonical slices with preview, verification, and apply steps.
* Multi-slice apply now respects dependency order instead of the original list order.

## V2.3 Context Intelligence

* Id: v2-3-context-intelligence
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: context, knowledge-index, agents

Dependencies:

* Project Knowledge Index
* AGENTS.md routing

Implementation notes:

* Context suggestions now combine knowledge index results and selected file context.
* History-aware guidance is surfaced in the Coder workflow.

## V2.4 Verification Loop

* Id: v2-4-verification-loop
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: verification, reviewer, recovery

Dependencies:

* Verifier profile
* Reviewer profile

Implementation notes:

* Verification is build-based and runs before any apply step.
* Failed verification blocks the workflow from advancing into safe apply.

## V2.5 Safe Apply Workflow

* Id: v2-5-safe-apply-workflow
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: apply, backup, rollback

Dependencies:

* Patch Queue
* Backup system

Implementation notes:

* Apply is now risk-gated, audited, and rollback-aware.
* Rollback metadata is captured before apply and preserved for recovery.

## V2.6 Agent Dashboard

* Id: v2-6-agent-dashboard
* Status: Completed
* Priority: Medium
* SourceType: Roadmap
* Tags: metrics, dashboard, analysis

Dependencies:

* Run History

Implementation notes:

* The dashboard now summarizes agent runs, apply attempts, and rollbacks.
* Model usage and action metrics are displayed alongside recent activity.

## V2.7 Multi-Model Intelligence

* Id: v2-7-multi-model-intelligence
* Status: In Progress
* Priority: Medium
* SourceType: Roadmap
* Tags: models, planner, reviewer

Dependencies:

* Planner
* Patch Builder
* Reviewer

Current milestones:

* Agent Model Routing
* Model Benchmark Runs
* Model Comparison Runs
* Automatic Model Recommendation
* Agent Orchestration
* Autonomous Execution Safeguards

Implementation notes:

* Routing has started with preferred and fallback model assignments per profile.
* The next step is to make routing data-driven rather than purely rule-based.

## V2.8 Project History Index

* Id: v2-8-project-history-index
* Status: Completed
* Priority: High
* SourceType: Roadmap
* Tags: project-history-index, memory, indexing

Dependencies:

* Project Knowledge Index
* Run History
* Patch History
* Verification History

Implementation notes:

* The index is rebuilt from docs and runtime data.
* Summary output now reflects completed safe apply work, completed dashboard work, and in-progress multi-model work.

Acceptance Criteria:

* Items can be rebuilt from docs and runtime data.
* Summary output shows completed, pending, failed, and recommended work.
* Items retain relative file paths and stable IDs.

Data Sources:

* Agent Runs
* Task Plans
* Patch History
* Verification History
* Docs/Roadmap.md
* Docs/CompletedWork.md
* Docs/Architecture.md
* AGENTS.md
* Project Knowledge Index

Future:

* Git log indexing
* Commit clustering
* Automatic changelog generation

## V3 Vision

* Id: v3-vision
* Status: Future
* Priority: Medium
* SourceType: Roadmap
* Tags: autonomy, workflow, project-history-index

User Request
-> Planner
-> Task Plan
-> Slice Selection
-> Context Intelligence
-> Patch Builder
-> Verifier
-> Reviewer
-> Approval
-> Apply
-> Project History Update
-> Rollback
-> Next Slice Recommendation
