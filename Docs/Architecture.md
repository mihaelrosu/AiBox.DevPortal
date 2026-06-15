# AiBox.DevPortal Architecture

## Metadata

* SourceType: Architecture
* Status: Active
* Owner: DevPortal
* LastUpdated: 2026-06-15
* Tags: architecture, components, project-history-index

## Vision

DevPortal is a local-first agentic development environment focused on Blazor and Radzen projects using local Ollama models.

Goals:

* Planning
* Context selection
* Patch generation
* Verification
* Review
* Safe apply
* Rollback
* Project memory

## Related Files

* Components/Pages/Coder.razor
* Components/Coder/CoderKnowledgePanel.razor
* Components/Coder/CoderHistoryPanel.razor
* Components/Coder/ProjectHistoryPanel.razor
* Services/ProjectKnowledgeIndexService.cs
* Services/ProjectHistoryIndexService.cs

## High-Level Architecture

User
-> Planner
-> Task Plan
-> Slice Execution
-> Context Selection
-> Patch Builder
-> Verification
-> Review
-> Approval
-> Apply
-> Project History

## Agent Profiles

### Planner

Responsibilities:

* Feature decomposition
* Task plans
* Task Slice generation

Dependencies:

* Project Knowledge Index

### Patch Builder

Responsibilities:

* Generate patch previews
* Create files
* Modify files
* Repair invalid patch structures

Related Files:

* Services/PatchEditOperationService.cs
* Services/PatchPreviewRepairService.cs

### Reviewer

Responsibilities:

* Review generated patches
* Assess risk
* Recommend improvements

Known Issues:

* Reviewer workflows are still mostly embedded in the Coder page history experience.

### Verifier

Responsibilities:

* Build verification
* Test execution
* Validation

Dependencies:

* Verification profiles
* Patch queue

### Tool Runner

Responsibilities:

* Controlled command execution
* Project inspection

## Core Components

### Coder UI

Responsibilities:

* User interaction
* Context selection
* Patch management
* Execution history

Related Files:

* Components/Pages/Coder.razor
* Components/Coder/CoderPromptPanel.razor
* Components/Coder/CoderExecutionPanel.razor

### Project Knowledge Index

Responsibilities:

* Index project files
* Suggest relevant files
* Support context selection

Related Files:

* Models/ProjectKnowledgeIndex.cs
* Services/ProjectKnowledgeIndexService.cs
* Components/Coder/CoderKnowledgePanel.razor

### Project History Index

Responsibilities:

* Index completed work
* Index task plans
* Index patch history
* Recommend next work

Dependencies:

* Docs/Roadmap.md
* Docs/CompletedWork.md
* Docs/Architecture.md
* Runtime data files

Related Files:

* Models/ProjectHistoryItem.cs
* Models/ProjectHistoryIndex.cs
* Models/ProjectHistorySummary.cs
* Services/ProjectHistoryIndexService.cs
* Components/Coder/ProjectHistoryPanel.razor

### Patch Queue

Responsibilities:

* Store generated patches
* Manage approval workflow

Related Files:

* Services/PatchPackageService.cs
* Components/Coder/CoderPatchQueuePanel.razor

## Data Sources

### Documentation

* Docs/Roadmap.md
* Docs/CompletedWork.md
* Docs/Architecture.md

### Runtime Data

* Agent Runs
* Task Plans
* Patch History
* Verification Results

### Future Data Sources

* Git History
* Commit Messages
* Release Notes

## Acceptance Criteria

* Headings remain stable enough for simple parsing.
* Each major component has related file references.
* Indexable sections use canonical names consistently.

## Design Principles

* Local-first
* Model-independent
* Slice-based execution
* Patch preview before apply
* Safe rollback
* Incremental verification
* Explainable actions

## Future Direction

* Autonomous slice execution
* Verification-driven repair
* Project memory
* Multi-model orchestration
* Automated release preparation
