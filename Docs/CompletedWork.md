# AiBox.DevPortal Completed Work

## Metadata

* SourceType: CompletedWork
* Status: Active
* Owner: DevPortal
* LastUpdated: 2026-06-19
* Tags: completed-work, history, project-memory

## Project Overview

AiBox.DevPortal is a local AI-assisted development environment for Blazor/Radzen projects using local Ollama models.
It supports planning, slice decomposition, patch generation, verification, safe application, rollback, auditing, dashboard metrics, and project memory for the AiBox workspace.

## 2026

### Agent Mode Profiles

Completed:

* Planner profile
* Patch Builder profile
* Reviewer profile
* Verifier profile
* Tool Runner profile
* Preferred and fallback model routing per profile

Related Files:

* Services/Agents/AgentModeProfileService.cs
* Services/AgentModelRoutingService.cs
* Models/Agents/AgentModeProfile.cs

### Run History

Completed:

* Persistent agent run history
* Agent run history UI in Coder
* Run detail inspection
* Local request, prompt, and result capture

Related Files:

* Data/agent-runs.json
* Components/Coder/CoderHistoryPanel.razor
* Components/Pages/Coder.razor

### Patch Queue

Completed:

* Patch preview queue
* Queue inspection in Coder
* Safe preview-first patch flow

Related Files:

* Services/PatchQueueService.cs
* Components/Pages/Coder.razor

### Project Knowledge Index (PKI)

Completed:

* Project Knowledge Index model
* Project Knowledge Index service
* Knowledge rebuild workflow
* Knowledge panel
* Context file suggestions

Related Files:

* Services/ProjectKnowledgeIndexService.cs
* Components/Coder/CoderKnowledgePanel.razor
* Components/Pages/Coder.razor

### Project History Index (PHI)

Completed:

* ProjectHistoryItem model
* ProjectHistoryIndex model
* ProjectHistorySummary model
* ProjectHistoryIndexService
* ProjectHistoryPanel
* History rebuild workflow
* History summary generation
* Recommendation extraction
* Indexed item viewer

Related Files:

* Models/ProjectHistoryItem.cs
* Models/ProjectHistoryIndex.cs
* Models/ProjectHistorySummary.cs
* Services/ProjectHistoryIndexService.cs
* Components/Coder/ProjectHistoryPanel.razor

### Context Suggestions

Completed:

* Context file suggestions from the Project Knowledge Index
* Selected file context support
* Multi-file context support

Related Files:

* Services/ProjectKnowledgeIndexService.cs
* Components/Coder/CoderKnowledgePanel.razor
* Components/Pages/Coder.razor

### Task Plan / Slice Workflow

Completed:

* Task planning foundations
* Canonical TaskPlanSlice model
* Task decomposition service
* Slice preview generation
* Ordered slice apply support
* Multi-slice apply flow

Related Files:

* Models/TaskPlan.cs
* Models/TaskPlanSlice.cs
* Services/TaskDecompositionService.cs
* Services/TaskPlanApplyService.cs
* Components/Coder/TaskPlanPreviewPanel.razor

### Slice Verification

Completed:

* Slice verification service
* Verify button in Coder
* Real dotnet build verification
* Verified and failed slice states

Related Files:

* Services/TaskSliceVerificationService.cs
* Components/Pages/Coder.razor

### Risk Analysis

Completed:

* Slice risk scoring
* Risk factors for file count, security, database, and service changes
* Risk summary generation

Related Files:

* Models/RiskAnalysisResult.cs
* Services/RiskAnalysisService.cs
* Models/TaskPlanSlice.cs

### Risk Gates

Completed:

* Low-risk slice applies allowed
* Medium-risk slice warnings
* High-risk approval gate
* Critical-risk apply block

Related Files:

* Services/TaskSliceApplyService.cs
* Components/Pages/Coder.razor

### Multi-Slice Apply

Completed:

* Dependency graph validation before apply
* Ordered slice apply execution
* Safe apply, audit, and rollback behavior preserved

Related Files:

* Services/TaskPlanApplyService.cs
* Services/TaskPlanDependencyGraphService.cs

### Apply Audit Log

Completed:

* Persistent slice apply audit log
* Blocked apply attempts recorded
* Apply attempt details captured for UI review

Related Files:

* Models/TaskSliceApplyAuditEntry.cs
* Services/TaskSliceApplyAuditService.cs
* Components/Pages/Coder.razor

### Patch Rollback Service

Completed:

* Rollback metadata capture
* Backup-aware restore flow
* Rollback audit recording

Related Files:

* Models/PatchRollbackEntry.cs
* Services/PatchRollbackService.cs
* Services/TaskSliceApplyService.cs

### Agent Dashboard Foundation

Completed:

* Agent dashboard summary service
* Dashboard panel in Coder
* Recent activity panels
* Summary cards for runs, applies, and rollbacks

Related Files:

* Models/AgentDashboardSummary.cs
* Services/AgentDashboardService.cs
* Components/Coder/AgentDashboardPanel.razor

### Dashboard Metrics

Completed:

* Model usage metrics
* Action metrics
* Success rate cards
* Risk distribution metrics

Related Files:

* Models/AgentDashboardSummary.cs
* Services/AgentDashboardService.cs

### Agent Model Routing

Completed:

* Preferred model selection per agent role
* Fallback model selection
* Recommendation-based routing opt-in
* Routing reason tracking
* Routing assignment persistence

Related Files:

* Models/AgentModelAssignment.cs
* Services/AgentModelRoutingService.cs
* Models/Agents/AgentModeProfile.cs
* Components/Coder/AgentProfilesPanel.razor

### Model Benchmark Runs

Completed:

* Benchmark runs per agent role
* Benchmark persistence
* Benchmark duration and output metrics

Related Files:

* Models/AgentModelBenchmarkRun.cs
* Services/AgentModelBenchmarkService.cs
* Components/Coder/AgentModelBenchmarkPanel.razor

### Model Comparison Runs

Completed:

* Multi-model comparisons per role and prompt
* Comparison run persistence
* Best-model selection from benchmark results

Related Files:

* Models/AgentModelComparisonRun.cs
* Services/AgentModelComparisonService.cs
* Components/Coder/AgentModelComparisonPanel.razor

### Automatic Model Recommendation

Completed:

* Role-based recommendation scoring
* Recommendation persistence
* Recommendation refresh UI

Related Files:

* Models/AgentModelRecommendation.cs
* Services/AgentModelRecommendationService.cs
* Components/Coder/AgentModelRecommendationPanel.razor

### Recommendation-Based Routing

Completed:

* Routing can default to the latest recommendation per role
* Explicit preferred model still wins
* Fallback model remains available when recommendation is unavailable
* Recommendation opt-in is persisted in routing assignments

Related Files:

* Models/AgentModelAssignment.cs
* Services/AgentModelRoutingService.cs
* Components/Coder/AgentProfilesPanel.razor

### Canonical AgentProfilesPanel Cleanup

Completed:

* Removed the obsolete agent profile wrapper
* Consolidated routing UI onto AgentProfilesPanel
* Updated smoke coverage to target the canonical panel

Related Files:

* Components/Coder/AgentProfilesPanel.razor
* Components/Pages/Coder.razor
* tests/AiBox.DevPortal.Tests/CoderComponentSmokeTests.cs

## 2026-06-19

### V2.1 Reliable Patch Generation

Status: Completed

Completed:

* Hardened patch preview generation
* Validated patch intent and preview targets
* Reduced malformed patch output failures

Implementation notes:

* Patch preview now fails fast when the request cannot produce a safe patch package.
* Generated patch output remains preview-only until apply workflow authorizes it.

### V2.2 Planner -> Slice Workflow

Status: Completed

Completed:

* Canonical TaskPlanSlice workflow
* Slice decomposition from planner output
* Slice preview and verification actions
* Ordered multi-slice application

Implementation notes:

* Planner output now flows through deterministic slice identities and statuses.
* Slice execution is now handled as an explicit workflow rather than ad hoc patch generation.

### V2.3 Context Intelligence

Status: Completed

Completed:

* Project Knowledge Index
* Context suggestions
* Selected file context support
* Project History Index

Implementation notes:

* The Coder page now combines search-driven context selection with history-aware guidance.
* Context selection is designed to support the next-slice workflow and model routing work.

### V2.4 Verification Loop

Status: Completed

Completed:

* Slice verification service
* Real build verification
* Verification results surfaced in the UI

Implementation notes:

* Verification is build-based and remains separate from apply.
* Failed verification keeps the slice out of the apply path.

### V2.5 Safe Apply Workflow

Status: Completed

Completed:

* Safe apply service
* Approval gates
* Audit logging
* Backup-aware rollback path

Implementation notes:

* Apply operations are now auditable and reversible.
* High-risk slices require approval and critical slices remain blocked.

### V2.6 Agent Dashboard

Status: Completed

Completed:

* Dashboard foundation
* Summary cards
* Model usage metrics
* Action metrics
* Recent activity panels

Implementation notes:

* The dashboard aggregates run history, apply audit history, and rollback history.
* This gives a single view for workflow health and model performance.

### V2.7 Multi-Model Intelligence

Status: Completed

Started:

* Agent Model Routing
* Preferred and fallback model assignments

Completed:

* Model benchmark runs
* Model comparison runs
* Automatic model recommendation
* Agent orchestration
* Autonomous execution safeguards

Implementation notes:

* Routing is now profile-aware and recommendation-driven.
* Model benchmarks, comparisons, and recommendations are persisted and surfaced in the dashboard.
* The routing layer now supports explicit preference, recommendation fallback, and explainable assignment reasons.

### V2.8 Agent Orchestration

Completed:

* Agent Orchestration Foundation
* Real Orchestration Execution
* Orchestration Apply Step
* CommitAndSync Step
* Orchestration Safety Review
* Orchestration Audit Timeline

Related Files:

* Models/AgentOrchestrationRun.cs
* Models/AgentOrchestrationStep.cs
* Models/AgentOrchestrationStatus.cs
* Models/AgentOrchestrationTimelineEvent.cs
* Services/AgentOrchestrationService.cs
* Services/AgentOrchestrationSafetyService.cs
* Services/AgentOrchestrationTimelineService.cs
* Components/Coder/AgentOrchestrationPanel.razor
* Components/Coder/AgentOrchestrationSafetyPanel.razor
* Components/Coder/AgentOrchestrationTimelinePanel.razor

Implementation notes:

* Orchestration now executes the planner, patch builder, verifier, reviewer, apply, and git sync workflow using existing services.
* Apply safety decisions are generated before apply and recorded in the run state, audit log, and timeline.
* Git sync failures and skipped runs are recorded without blocking state persistence.

### V2.8 Project History Index

Status: Completed

Completed:

* Docs/Roadmap.md
* Docs/CompletedWork.md
* Docs/Architecture.md
* ProjectHistoryItem
* ProjectHistoryIndex
* ProjectHistorySummary
* ProjectHistoryIndexService
* ProjectHistoryPanel
* Rebuild workflow
* Summary generation
* Recommendation extraction

Implementation notes:

* The index is generated from docs and runtime data under Data/.
* History summaries now reflect completed safe-apply and dashboard work while keeping multi-model work current.

## In Progress

### V2.9 Autonomous Execution Controls

Status: Planned

Dependencies:

* Agent Orchestration
* Apply Audit Log
* Safety Review

Acceptance Criteria:

* Human approval queue for risky orchestration actions.
* Pause and resume orchestration runs.
* Retry failed orchestration steps safely.
* Scheduled agent runs with controlled execution windows.
* Execution policy profiles that shape autonomy and approval behavior.

## Known Technical Decisions

* Local-first architecture
* Ollama models preferred
* Patch preview before apply
* Slice-based execution model
* Project Knowledge Index used for context selection
* AGENTS.md based instruction routing

## Next Recommended Work

1. Model Benchmark Runs
2. Model Comparison Runs
3. Automatic Model Recommendation
4. Human Approval Queue
5. Orchestration Pause / Resume
6. Retry Failed Step
7. Scheduled Agent Runs
8. Execution Policy Profiles
