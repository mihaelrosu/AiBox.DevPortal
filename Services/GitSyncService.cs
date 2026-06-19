using System.Diagnostics;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class GitSyncService
{
    public async Task<GitSyncResult> SyncAsync(string projectPath, string commitMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path is required.", nameof(projectPath));
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            throw new ArgumentException("Commit message is required.", nameof(commitMessage));
        }

        var rootPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Project path '{rootPath}' was not found.");
        }

        if (!Directory.Exists(Path.Combine(rootPath, ".git")))
        {
            throw new InvalidOperationException($"Git repository was not found at '{rootPath}'.");
        }

        var result = new GitSyncResult
        {
            TimestampUtc = DateTime.UtcNow,
            CommitAttempted = true
        };

        var addResult = await RunGitAsync(rootPath, ["add", "-A"], cancellationToken);
        if (addResult.ExitCode != 0)
        {
            result.GitMessage = BuildFailureMessage("git add -A", addResult);
            return result;
        }

        var commitResult = await RunGitAsync(rootPath, ["commit", "-m", commitMessage], cancellationToken);
        if (commitResult.ExitCode != 0)
        {
            result.GitMessage = BuildFailureMessage("git commit -m <message>", commitResult);
            return result;
        }

        result.CommitSucceeded = true;
        result.CommitHash = (await RunGitAsync(rootPath, ["rev-parse", "HEAD"], cancellationToken)).Output.Trim();

        result.PushAttempted = true;
        var pushResult = await RunGitAsync(rootPath, ["push"], cancellationToken);
        if (pushResult.ExitCode != 0)
        {
            result.GitMessage = BuildFailureMessage("git push", pushResult, result.CommitHash);
            return result;
        }

        result.PushSucceeded = true;
        result.GitMessage = BuildSuccessMessage(result.CommitHash);
        return result;
    }

    private static string BuildSuccessMessage(string commitHash)
    {
        return string.IsNullOrWhiteSpace(commitHash)
            ? "Git sync completed successfully."
            : $"Git sync completed successfully at commit {commitHash}.";
    }

    private static string BuildFailureMessage(string command, CommandRunResult result, string? commitHash = null)
    {
        var parts = new List<string> { $"{command} failed with exit code {result.ExitCode}." };

        if (!string.IsNullOrWhiteSpace(commitHash))
        {
            parts.Add($"Commit hash: {commitHash}.");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            parts.Add(result.Error.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(result.Output))
        {
            parts.Add(result.Output.Trim());
        }

        return string.Join(" ", parts);
    }

    private static async Task<CommandRunResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var command = $"git {string.Join(" ", arguments.Select(EscapeArgument))}";
        var result = new CommandRunResult
        {
            Command = command,
            Started = DateTimeOffset.UtcNow
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git process.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);

        result.ExitCode = process.ExitCode;
        result.Output = await outputTask;
        result.Error = await errorTask;
        result.Finished = DateTimeOffset.UtcNow;
        return result;
    }

    private static string EscapeArgument(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "\"\"" : value.Contains(' ') ? $"\"{value}\"" : value;
    }
}
