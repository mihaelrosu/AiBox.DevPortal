using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace AiBox.DevPortal.Services
{
    public class PatchBackupService : IPatchBackupService
    {
        private readonly ILogger<PatchBackupService> _logger;

        public PatchBackupService(ILogger<PatchBackupService> logger)
        {
            _logger = logger;
        }

        public async Task<PatchBackupResult> CreateBackupAsync(
            string projectPath,
            IEnumerable<string> filePaths,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Create timestamped folder
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var backupFolderPath = Path.Combine("Data", "patch-backups", timestamp);
                Directory.CreateDirectory(backupFolderPath);

                var backedUpFiles = new List<string>();
                var fileCount = 0;

                // Process each file path
                foreach (var filePath in filePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skip if file doesn't exist
                    if (!File.Exists(filePath))
                    {
                        _logger.LogWarning($"File {filePath} does not exist and was skipped.");
                        continue;
                    }

                    // Get relative path
                    var relativePath = Path.GetRelativePath(projectPath, filePath);

                    // Create directory structure in backup
                    var backupFilePath = Path.Combine(backupFolderPath, relativePath);
                    var directory = Path.GetDirectoryName(backupFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Copy file
                    var destinationPath = Path.Combine(backupFolderPath, relativePath);
                    File.Copy(filePath, destinationPath, true);

                    backedUpFiles.Add(relativePath);
                    fileCount++;
                }

                return new PatchBackupResult
                {
                    BackupFolderPath = backupFolderPath,
                    BackedUpFilesCount = fileCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating patch backup.");
                throw;
            }
        }
    }
}