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
* Model Benchmark Runs
* Model Comparison Runs
* Automatic Model Recommendation
* Recommendation-Based Routing
* Canonical AgentProfilesPanel cleanup

Current:

* V2.9 Autonomous Execution Controls

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
* Status: Completed
* Priority: Medium
* SourceType: Roadmap
* Tags: models, planner, reviewer

Dependencies:

* Planner
* Patch Builder
* Reviewer

Completed milestones:

* Agent Orchestration
* Autonomous Execution Safeguards

Implementation notes:

* Routing now supports preferred, recommendation-driven, and fallback model selection.
* Benchmarks, comparisons, and recommendations are persisted and surfaced in the dashboard.
* The recommendation flow is now wired into the routing UI.

## V2.8 Agent Orchestration

* Id: v2-8-agent-orchestration
* Status: Completed
* Priority: Medium
* SourceType: Roadmap
* Tags: orchestration, workflow, coordination

Dependencies:

* Agent Model Routing
* Automatic Model Recommendation
* Run History
* Apply Audit Log

Implementation notes:

* Orchestration now executes the existing planner, patch builder, verifier, reviewer, apply, and git sync services.
* Safety reviews are generated before apply and recorded in the timeline and audit log.
* The orchestration panel now shows run state, apply decisions, git sync results, and audit timeline events.

## V2.9 Autonomous Execution Controls

* Id: v2-9-autonomous-execution-controls
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: autonomy, orchestration, safeguards

Dependencies:

* Agent Orchestration
* Apply Audit Log
* Orchestration Safety Review

Implementation notes:

* This phase will add human approval queues, pause/resume controls, retry controls, and scheduled runs.
* Execution policy profiles will define when orchestration can run automatically.

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
