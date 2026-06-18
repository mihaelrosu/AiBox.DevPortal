using System;

namespace AiBox.DevPortal.Models
{
    public class PatchBackupResult
    {
        public string BackupFolderPath { get; set; }= string.Empty;
        public int BackedUpFilesCount { get; set; }
    }
}