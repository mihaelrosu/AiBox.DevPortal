using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AiBox.DevPortal.Services
{
    public class PatchBackupService
    {
        public async Task<string> CreateBackupAsync(string projectPath, IEnumerable<string> filePaths)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backupFolderPath = Path.Combine("Data/patch-backups", timestamp);
            Directory.CreateDirectory(backupFolderPath);

            foreach (var filePath in filePaths)
            {
                var relativePath = Path.GetRelativePath(projectPath, filePath);
                var destinationPath = Path.Combine(backupFolderPath, relativePath);

                if (File.Exists(filePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    File.Copy(filePath, destinationPath, true);
                }
            }

            return backupFolderPath;
        }
    }
}