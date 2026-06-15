# AiBox.DevPortal Roadmap

## Metadata

* SourceType: Roadmap
* Status: Active
* Owner: DevPortal
* LastUpdated: 2026-06-15
* Tags: roadmap, planning, project-history-index

## Vision

AiBox.DevPortal is a local agentic development environment for the AiBox workspace.
It combines local Ollama models, Blazor, and Radzen UI with planning, patch generation,
verification, review, safe apply, rollback, and project memory so development work can be
tracked and repeated safely.

## Current Status

Completed:

* Agent Mode Profiles
* Planner
* Patch Builder
* Run History
* Patch Queue
* Project Knowledge Index
* Context File Selection
* Suggest Context Files

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
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: patch-builder, validation, repair

Dependencies:

* Patch Builder
* Validation pipeline

* Create workflow hardening
* Patch JSON robustness
* XML documentation mode

## V2.2 Planner -> Task Slice Workflow

* Id: v2-2-planner-task-slice-workflow
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: planner, task-plan, task-slice

Dependencies:

* Task Plan
* Task Slice

* Task Plan
* Task Slice
* TaskSliceExecutionRequest
* TaskSliceExecutionResult
* Slice states

## V2.3 Context Intelligence

* Id: v2-3-context-intelligence
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: context, knowledge-index, agents

Dependencies:

* Project Knowledge Index
* AGENTS.md routing

* Project Knowledge Index integration
* Context suggestions
* AGENTS.md routing
* Token budget panel

## V2.4 Verification Loop

* Id: v2-4-verification-loop
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: verification, reviewer, recovery

Dependencies:

* Verifier profile
* Reviewer profile

* Verifier profile
* Reviewer profile
* Risk assessment
* Recovery mode

## V2.5 Safe Apply Workflow

* Id: v2-5-safe-apply-workflow
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: apply, backup, rollback

Dependencies:

* Patch Queue
* Backup system

* Approval workflow
* Backup system
* Rollback

## V2.6 Agent Dashboard

* Id: v2-6-agent-dashboard
* Status: Planned
* Priority: Medium
* SourceType: Roadmap
* Tags: metrics, dashboard, analysis

Dependencies:

* Run History

* Run History
* Metrics
* Failure analysis

## V2.7 Multi-Model Intelligence

* Id: v2-7-multi-model-intelligence
* Status: Planned
* Priority: Medium
* SourceType: Roadmap
* Tags: models, planner, reviewer

Dependencies:

* Planner
* Patch Builder
* Reviewer

* Planner model
* Patch Builder model
* Reviewer model
* Comparison metrics

## V2.8 Project History Index

* Id: v2-8-project-history-index
* Status: Planned
* Priority: High
* SourceType: Roadmap
* Tags: project-history-index, memory, indexing

Dependencies:

* Project Knowledge Index
* Run History
* Patch History
* Verification History

Acceptance Criteria:

* Items can be rebuilt from docs and runtime data.
* Summary output shows completed, pending, failed, and recommended work.
* Items retain relative file paths and stable IDs.

* ProjectHistoryItem
* ProjectHistoryIndex
* ProjectHistorySummary
* ProjectHistoryIndexService

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
