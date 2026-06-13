using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ProjectKnowledgeIndexServiceTests
{
    [Fact]
    public async Task BuildAsync_ScansSupportedFilesAndIgnoresBlockedFolders()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"aibox-knowledge-index-storage-{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(storageRoot, "SampleProject");
        var originalCurrentDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Controllers"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Tests"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Pages"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Coder"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "bin"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "obj"));
            Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "node_modules"));

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Models", "ProjectKnowledgeIndex.cs"),
                """
                namespace Sample.Models;

                /// <summary>
                /// Represents a sample knowledge index.
                /// </summary>
                public sealed class ProjectKnowledgeIndex {}
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Services", "ProjectKnowledgeIndexService.cs"),
                """
                namespace Sample.Services;

                public sealed class ProjectKnowledgeIndexService {}
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Controllers", "HomeController.cs"),
                """
                namespace Sample.Controllers;

                public sealed class HomeController {}
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Tests", "ProjectKnowledgeIndexServiceTests.cs"),
                """
                namespace Sample.Tests;

                public sealed class ProjectKnowledgeIndexServiceTests {}
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Components", "Pages", "Home.razor"),
                """
                @page "/"
                <h1>Home</h1>
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Components", "Coder", "KnowledgePanel.razor"),
                """
                <div>Knowledge panel</div>
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "Sample.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                </Project>
                """);

            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "appsettings.json"),
                """
                { "Logging": { "LogLevel": { "Default": "Information" } } }
                """);

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "bin", "Ignored.cs"), "ignored");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "obj", "Ignored.cs"), "ignored");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, ".git", "Ignored.cs"), "ignored");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "node_modules", "Ignored.js"), "ignored");

            Directory.SetCurrentDirectory(storageRoot);

            var service = new ProjectKnowledgeIndexService();
            var index = await service.BuildAsync(projectRoot);

            Assert.Equal(Path.GetFullPath(projectRoot), index.ProjectPath);
            Assert.True(index.RebuiltAtUtc <= DateTime.UtcNow);
            Assert.Equal(8, index.Items.Count);
            Assert.All(index.Items, item => Assert.DoesNotContain("/bin/", item.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.All(index.Items, item => Assert.DoesNotContain("/obj/", item.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.All(index.Items, item => Assert.DoesNotContain("/.git/", item.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.All(index.Items, item => Assert.DoesNotContain("/node_modules/", item.RelativePath, StringComparison.OrdinalIgnoreCase));

            var modelItem = Assert.Single(index.Items, item => item.RelativePath == "Models/ProjectKnowledgeIndex.cs");
            Assert.Equal("CSharp", modelItem.FileType);
            Assert.Equal("ProjectKnowledgeIndex", modelItem.Name);
            Assert.Equal("Sample.Models", modelItem.Namespace);
            Assert.Equal("Model", modelItem.Kind);
            Assert.Equal("Represents a sample knowledge index.", modelItem.Summary);

            var serviceItem = Assert.Single(index.Items, item => item.RelativePath == "Services/ProjectKnowledgeIndexService.cs");
            Assert.Equal("Service", serviceItem.Kind);

            var controllerItem = Assert.Single(index.Items, item => item.RelativePath == "Controllers/HomeController.cs");
            Assert.Equal("Controller", controllerItem.Kind);

            var testItem = Assert.Single(index.Items, item => item.RelativePath == "Tests/ProjectKnowledgeIndexServiceTests.cs");
            Assert.Equal("Test", testItem.Kind);

            var pageItem = Assert.Single(index.Items, item => item.RelativePath == "Components/Pages/Home.razor");
            Assert.Equal("Razor", pageItem.FileType);
            Assert.Equal("RazorPage", pageItem.Kind);

            var componentItem = Assert.Single(index.Items, item => item.RelativePath == "Components/Coder/KnowledgePanel.razor");
            Assert.Equal("RazorComponent", componentItem.Kind);

            var projectItem = Assert.Single(index.Items, item => item.RelativePath == "Sample.csproj");
            Assert.Equal("Project", projectItem.FileType);

            var configItem = Assert.Single(index.Items, item => item.RelativePath == "appsettings.json");
            Assert.Equal("Config", configItem.FileType);

            var storedIndexPath = Path.Combine(storageRoot, "Data", "project-knowledge-index.json");
            Assert.True(File.Exists(storedIndexPath));

            var storedIndex = await JsonSerializer.DeserializeAsync<ProjectKnowledgeIndex>(
                File.OpenRead(storedIndexPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(storedIndex);
            Assert.Equal(8, storedIndex!.Items.Count);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);

            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
