using System.Diagnostics;
using System.Text;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class LocalCoderBuildService(IConfiguration configuration) : ILocalCoderBuildService
{
    private const int MaxOutputCharacters = 512 * 1024;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(120);
    private static readonly HashSet<string> AllowedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build",
        "test",
        "restore"
    };
    private static readonly HashSet<string> BlockedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo",
        "rm",
        "mv",
        "cp",
        "chmod",
        "chown",
        "docker",
        "curl",
        "wget",
        "bash",
        "sh",
        "powershell"
    };

    public async Task<LocalCoderBuildResult> RunBuildAsync(
        LocalCoderTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var startedAt = DateTime.UtcNow;
        var result = new LocalCoderBuildResult
        {
            StartedAt = startedAt,
            CompletedAt = startedAt,
            Command = task.BuildCommand
        };

        try
        {
            var arguments = ValidateAndParseBuildCommand(task.BuildCommand);
            var repositoryPath = Path.GetFullPath(task.RepositoryPath.Trim());

            if (!Directory.Exists(repositoryPath))
            {
                throw new DirectoryNotFoundException($"Repository path '{repositoryPath}' was not found.");
            }

            if (!IsAllowedRepository(repositoryPath))
            {
                throw new InvalidOperationException($"Repository path '{repositoryPath}' is not configured for Local Coder builds.");
            }

            if (File.GetAttributes(repositoryPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Repository path cannot be a symbolic link.");
            }

            var artifactsPath = Path.Combine(Path.GetTempPath(), "aibox-local-coder-build", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(artifactsPath);

            using var process = new Process
            {
                StartInfo = CreateStartInfo(repositoryPath, arguments, artifactsPath),
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("The safe build process could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BuildTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                result.ErrorMessage = $"Command timed out after {BuildTimeout.TotalSeconds:0} seconds.";
            }

            var output = await stdoutTask;
            var error = await stderrTask;
            result.ExitCode = process.HasExited ? process.ExitCode : null;
            result.Output = LimitOutput(output);
            result.ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) ? LimitOutput(error) : result.ErrorMessage;
            result.Success = result.ExitCode == 0;
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.ErrorMessage = exception.Message;
        }

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    private bool IsAllowedRepository(string repositoryPath)
    {
        var configuredRoots = configuration
            .GetSection("AiBox:LocalCoder:AllowedRepositoryRoots")
            .Get<string[]>() ?? [];

        return configuredRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar))
            .Any(root => repositoryPath.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ValidateAndParseBuildCommand(string command)
    {
        var tokens = Tokenize(command);

        if (tokens.Count < 2 || !tokens[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only 'dotnet build', 'dotnet test', and 'dotnet restore' are allowed.");
        }

        if (!AllowedVerbs.Contains(tokens[1]))
        {
            throw new InvalidOperationException($"The dotnet command '{tokens[1]}' is not allowed. Use build, test, or restore.");
        }

        var verb = tokens[1].ToLowerInvariant();
        var arguments = new List<string> { verb };

        for (var index = 2; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (BlockedCommands.Contains(token))
            {
                throw new InvalidOperationException($"Blocked command token '{token}' is not allowed.");
            }

            if (token == "--nologo")
            {
                arguments.Add(token);
                continue;
            }

            if (verb is "build" or "test" && token == "--no-restore"
                || verb == "test" && token == "--no-build")
            {
                arguments.Add(token);
                continue;
            }

            if (verb is "build" or "test" && token is "-c" or "--configuration")
            {
                if (++index >= tokens.Count || tokens[index] is not ("Debug" or "Release"))
                {
                    throw new InvalidOperationException("Configuration must be Debug or Release.");
                }

                arguments.Add(token);
                arguments.Add(tokens[index]);
                continue;
            }

            if (token is "-v" or "--verbosity")
            {
                if (++index >= tokens.Count || tokens[index] is not ("quiet" or "minimal" or "normal" or "detailed" or "diagnostic"))
                {
                    throw new InvalidOperationException("Unsupported command verbosity.");
                }

                arguments.Add(token);
                arguments.Add(tokens[index]);
                continue;
            }

            throw new InvalidOperationException($"Argument '{token}' is not allowed for 'dotnet {verb}'.");
        }

        return arguments;
    }

    private static List<string> Tokenize(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("A build command is required.");
        }

        if (command.IndexOfAny([';', '&', '|', '>', '<', '`', '\n', '\r']) >= 0
            || command.Contains("$(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Shell chaining and redirection are not allowed.");
        }

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (tokens.Any(BlockedCommands.Contains))
        {
            throw new InvalidOperationException("The command contains a blocked executable or command token.");
        }

        return tokens;
    }

    private static ProcessStartInfo CreateStartInfo(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        string artifactsPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--artifacts-path");
        startInfo.ArgumentList.Add(artifactsPath);
        return startInfo;
    }

    private static string LimitOutput(string output)
    {
        return output.Length <= MaxOutputCharacters
            ? output
            : $"{output[..MaxOutputCharacters]}{Environment.NewLine}[output truncated]";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the timeout and kill attempt.
        }
    }
}
