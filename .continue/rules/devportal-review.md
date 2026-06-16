# DevPortal Review Rules

When reviewing code for AiBox.DevPortal, verify the following:

## Architecture

* Changes follow the Planner → Slice → Verify → Apply workflow.
* Business logic remains in Services.
* Razor components remain thin.
* Existing services are reused before creating new services.

## Models

* Request and Result objects are explicit.
* Nullable reference types are respected.
* Models are placed in the Models folder.

## Services

* Services are registered in Program.cs.
* Async methods are used for I/O operations.
* Service responsibilities remain focused.

## UI

* Prefer Radzen components.
* Avoid business logic in Razor files.
* Keep pages and dialogs small and reusable.

## Local Coder

* Changes integrate with TaskPlan and TaskPlanSlice workflows when applicable.
* Changes consider ProjectKnowledgeIndex and ProjectHistoryIndex impacts.
* Slice status transitions remain valid.

## Quality

* dotnet build succeeds.
* No new warnings are introduced.
* No dead code is added.
* No duplicate functionality is introduced.

## Safe Apply Readiness

* Changes are compatible with future Safe Apply Workflow.
* Generated patches remain reviewable.
* Verification can be performed before application.
