using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiBox.DevPortal.Services
{
    public interface IPatchBackupService
    {
        Task<PatchBackupResult> CreateBackupAsync(
            string projectPath,
            IEnumerable<string> filePaths,
            CancellationToken cancellationToken = default);
    }
}