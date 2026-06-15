# AiBox.DevPortal Completed Work

## Metadata

* SourceType: CompletedWork
* Status: Active
* Owner: DevPortal
* LastUpdated: 2026-06-15
* Tags: completed-work, history, project-memory

## Project Overview

AiBox.DevPortal is a local AI-assisted development environment for Blazor/Radzen projects using local Ollama models.
It supports planning, patch generation, verification, safe application, rollback, and project memory for the AiBox workspace.

## 2026

### Agent Infrastructure

Completed:

* Agent Mode Profiles
* Planner profile
* Patch Builder profile
* Reviewer profile
* Verifier profile
* Tool Runner profile

Related Files:

* Services/Agents/AgentModeProfileService.cs
* Services/Agents/AgentRegistryService.cs
* Services/Agents/AgentModeRunner.cs

Notes:

* Different models can be assigned per profile.

### Coder UI

Completed:

* Coder page
* Context file selection
* Multi-file context support
* Patch queue
* Patch preview workflow
* Run history UI

Related Files:

* Components/Pages/Coder.razor
* Components/Coder/CoderKnowledgePanel.razor
* Components/Coder/CoderHistoryPanel.razor
* Components/Coder/ProjectHistoryPanel.razor

### Patch Management

Completed:

* Patch preview generation
* Validation pipeline
* Patch queue
* Backup support foundations

Related Files:

* Services/PatchEditOperationService.cs
* Services/PatchPackageService.cs
* Services/PatchApprovalGateService.cs
* Services/PatchApplyService.cs

### Project Knowledge

Completed:

* Project Knowledge Index model
* Project Knowledge Index Service
* Knowledge rebuild workflow
* Knowledge panel
* Context file suggestions

Dependencies:

* Coder page
* Context file selection

### Planning Workflow

Completed:

* Task planning foundations
* Slice-oriented roadmap direction

Known Issues:

* Task Slice execution is not yet fully implemented.

### Local AI Integration

Completed:

* Ollama integration
* Agent profile execution model
* Local model support
* Prompt orchestration

Related Files:

* Services/OllamaService.cs
* Services/OllamaLocalLlmService.cs
* Services/PromptEnhancerService.cs

## In Progress

### Planner -> Slice Execution

Status: In Progress

Dependencies:

* Task Plan
* Task Slice

Acceptance Criteria:

* Planner output can be split into actionable slices.
* Slice execution records are indexable.

### Verification Loop

Status: Planned

Dependencies:

* Verifier profile
* Reviewer profile

### Safe Apply Workflow

Status: Planned

Dependencies:

* Patch Queue
* Backup support foundations

### Project History Index

Status: Planned

Dependencies:

* Docs/Roadmap.md
* Docs/CompletedWork.md
* Docs/Architecture.md
* Runtime history sources

## Known Technical Decisions

* Local-first architecture
* Ollama models preferred
* Patch preview before apply
* Slice-based execution model
* Project Knowledge Index used for context selection
* AGENTS.md based instruction routing

## Next Recommended Work

1. Project History Index foundation
2. Task Slice execution
3. Verification loop
4. Safe Apply workflow
5. Agent dashboard metrics
