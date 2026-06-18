using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiBox.DevPortal.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace AiBox.DevPortal.Services
{
    public class PatchBackupService : IPatchBackupService
    {
        private readonly ILogger<PatchBackupService> _logger;
        private readonly IWebHostEnvironment _environment;
        public PatchBackupService(
    IWebHostEnvironment environment,
    ILogger<PatchBackupService> logger)
        {
            _environment = environment;
            _logger = logger;
        }
        public async Task<PatchBackupResult> CreateBackupAsync(
    string projectPath,
    IEnumerable<string> filePaths,
    CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
            ArgumentNullException.ThrowIfNull(filePaths);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var baseBackupDir = Path.Combine(_environment.ContentRootPath, "Data", "patch-backups");
            var backupFolderPath = Path.Combine(baseBackupDir, timestamp);

            Directory.CreateDirectory(backupFolderPath);

            var fileCount = 0;

            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = Path.IsPathRooted(filePath)
                    ? filePath
                    : Path.Combine(projectPath, filePath);

                if (!File.Exists(sourcePath))
                {
                    _logger.LogWarning("File {FilePath} does not exist and was skipped.", sourcePath);
                    continue;
                }

                var relativePath = Path.GetRelativePath(projectPath, sourcePath);

                if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                {
                    _logger.LogWarning("File {FilePath} is outside project path and was skipped.", sourcePath);
                    continue;
                }

                var destinationPath = Path.Combine(backupFolderPath, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                await using var sourceStream = File.OpenRead(sourcePath);
                await using var destinationStream = File.Create(destinationPath);
                await sourceStream.CopyToAsync(destinationStream, cancellationToken);

                fileCount++;
            }

            return new PatchBackupResult
            {
                BackupFolderPath = backupFolderPath,
                BackedUpFilesCount = fileCount
            };
        }
    }
}