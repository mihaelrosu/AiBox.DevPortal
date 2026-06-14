using System.Diagnostics;
using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class PatchVerificationService(
    IAgentRunHistoryService agentRunHistoryService)
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet build",
        "dotnet test",
        "dotnet format --verify-no-changes"
    };

    private static readonly HashSet<string> BlockedFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm",
        "sudo",
        "chmod",
        "chown",
        "mv",
        "cp",
        "docker",
        "git reset",
        "git clean"
    };

    public async Task<PatchVerificationResult> VerifyAsync(
        string projectPath,
        IReadOnlyList<string>? commands = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var normalizedProjectPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(normalizedProjectPath))
        {
            throw new DirectoryNotFoundException($"Project path was not found: {normalizedProjectPath}");
        }

        var verificationCommands = NormalizeCommands(commands);
        var commandResults = new List<PatchVerificationCommandResult>();
        foreach (var command in verificationCommands)
        {
            PatchVerificationCommandResult result;
            try
            {
                result = await RunCommandAsync(normalizedProjectPath, command, cancellationToken);
            }
            catch (Exception exception)
            {
                var now = DateTimeOffset.UtcNow;
                result = new PatchVerificationCommandResult
                {
                    Command = command,
                    ExitCode = -1,
                    Error = exception.Message,
                    StartedAt = now,
                    FinishedAt = now
                };
            }

            commandResults.Add(result);

            if (!result.Passed)
            {
                break;
            }
        }

        var verificationResult = new PatchVerificationResult
        {
            ProjectPath = normalizedProjectPath,
            Commands = commandResults,
            VerifiedAt = DateTimeOffset.UtcNow
        };
        verificationResult.Summary = BuildSummary(verificationResult);
        verificationResult.Details = BuildDetails(verificationResult);

        await agentRunHistoryService.AddAsync(new AgentRunRecord
        {
            ActionKey = "patch-verification",
            ProfileMode = AgentMode.ToolRunner,
            Model = string.Empty,
            UserRequest = $"Patch verification for {normalizedProjectPath}",
            PromptSent = string.Join(Environment.NewLine, verificationCommands),
            ResultText = verificationResult.Details,
            Success = verificationResult.Passed,
            ErrorMessage = verificationResult.Passed ? string.Empty : GetFailureMessage(verificationResult)
        }, cancellationToken);

        return verificationResult;
    }

    private static IReadOnlyList<string> NormalizeCommands(IReadOnlyList<string>? commands)
    {
        var normalized = (commands is { Count: > 0 } ? commands : ["dotnet build"])
            .Select(NormalizeCommand)
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            normalized = ["dotnet build"];
        }

        foreach (var command in normalized)
        {
            ValidateCommand(command);
        }

        return normalized;
    }

    private static string NormalizeCommand(string command)
    {
        return string.Join(
            ' ',
            (command ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void ValidateCommand(string command)
    {
        if (BlockedFragments.Any(fragment => command.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Blocked verification command: {command}");
        }

        if (!AllowedCommands.Contains(command))
        {
            throw new InvalidOperationException($"Unsupported verification command: {command}");
        }
    }

    private static async Task<PatchVerificationCommandResult> RunCommandAsync(
        string projectPath,
        string command,
        CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("Verification command cannot be empty.");
        }

        var start = DateTimeOffset.UtcNow;
        var processStartInfo = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = string.Join(' ', parts.Skip(1)),
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Failed to start verification command: {command}");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var finished = DateTimeOffset.UtcNow;

        return new PatchVerificationCommandResult
        {
            Command = command,
            ExitCode = process.ExitCode,
            Output = output.TrimEnd(),
            Error = error.TrimEnd(),
            StartedAt = start,
            FinishedAt = finished
        };
    }

    private static string BuildSummary(PatchVerificationResult result)
    {
        if (result.Commands.Count == 0)
        {
            return "Verification failed: no commands were run.";
        }

        if (result.Passed)
        {
            return $"Verification:\n{string.Join(Environment.NewLine, result.Commands.Select(command => $"✓ {command.Command} passed"))}";
        }

        var failed = result.Commands.First(command => !command.Passed);
        return
            $"Verification failed:{Environment.NewLine}✗ {failed.Command}{Environment.NewLine}{Environment.NewLine}Error:{Environment.NewLine}{GetCommandError(failed)}";
    }

    private static string BuildDetails(PatchVerificationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project: {result.ProjectPath}");
        builder.AppendLine($"Verified: {result.VerifiedAt:O}");
        builder.AppendLine($"Passed: {result.Passed}");
        builder.AppendLine();

        foreach (var command in result.Commands)
        {
            builder.AppendLine($"Command: {command.Command}");
            builder.AppendLine($"Exit code: {command.ExitCode}");
            builder.AppendLine("Output:");
            builder.AppendLine(string.IsNullOrWhiteSpace(command.Output) ? "(none)" : command.Output);
            builder.AppendLine("Error:");
            builder.AppendLine(string.IsNullOrWhiteSpace(command.Error) ? "(none)" : command.Error);
            builder.AppendLine();
        }

        builder.AppendLine(result.Summary);
        return builder.ToString().TrimEnd();
    }

    private static string GetFailureMessage(PatchVerificationResult result)
    {
        var failed = result.Commands.FirstOrDefault(command => !command.Passed);
        return failed is null
            ? result.Summary
            : $"{failed.Command} failed with exit code {failed.ExitCode}. {GetCommandError(failed)}";
    }

    private static string GetCommandError(PatchVerificationCommandResult command)
    {
        if (!string.IsNullOrWhiteSpace(command.Error))
        {
            return command.Error;
        }

        return string.IsNullOrWhiteSpace(command.Output) ? $"Exit code {command.ExitCode}." : command.Output;
    }
}
