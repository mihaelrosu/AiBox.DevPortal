using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public static class AgentActionProfiles
{
    public const string CreatePlanActionKey = "create-plan";
    public const string GeneratePatchPreviewActionKey = "generate-patch-preview";
    public const string ApplyPatchActionKey = "apply-patch";
    public const string RollbackPatchActionKey = "rollback-patch";
    public const string RunCommandActionKey = "run-command";
    public const string VerifyProjectActionKey = "verify-project";
    public const string CommitChangesActionKey = "commit-changes";
    public const string BuildDeployActionKey = "build-deploy";

    public static AgentMode ForCreatePlan() => AgentMode.Planner;
    public static AgentMode ForGeneratePatchPreview() => AgentMode.PatchBuilder;
    public static AgentMode ForVerifyProject() => AgentMode.Verifier;
    public static AgentMode ForReviewDiff() => AgentMode.Reviewer;
    public static AgentMode ForApplyPatch() => AgentMode.ToolRunner;
    public static AgentMode ForRollbackPatch() => AgentMode.ToolRunner;
    public static AgentMode ForRunCommand() => AgentMode.ToolRunner;
    public static AgentMode ForCommitChanges() => AgentMode.ToolRunner;
    public static AgentMode ForBuildDeploy() => AgentMode.ToolRunner;
}
