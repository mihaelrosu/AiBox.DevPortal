---
name: deploy-aibox-devportal
description: Validate and deploy changes to the AiBox.DevPortal ASP.NET Blazor application served by the aibox-devportal Docker Compose service. Use after modifying Razor components, C# source, static assets, configuration copied into the image, or when the browser still shows an older version of the codebase.
---

# Deploy AiBox DevPortal

Run commands from the repository root containing `AiBox.DevPortal.csproj` and
`docker-compose.yml`.

## UI Grid Defaults

When touching Razor pages that use `RadzenDataGrid`, apply the shared grid
defaults from `RadzenDataGrid_def`:

```razor
<RadzenDataGrid @ref="grid"
                FilterMode="FilterMode.Simple"
                AllowVirtualization="false"
                AllowPaging="true"
                AllowSorting="true"
                ShowPagingSummary="true"
                AllowColumnResize="true"
                AllowFiltering="true"
                PagerPosition="PagerPosition.TopAndBottom">
```

If a page contains more than one grid, use `grid1`, `grid2`, `grid3`, and so
on so the component still compiles.

## Workflow

1. Inspect the changed files and preserve unrelated user changes.
2. Validate the application:

   ```bash
   dotnet build --no-restore
   ```

   If restore is required, run `dotnet build` instead.

3. Rebuild the image and recreate the application container:

   ```bash
   docker compose up -d --build aibox-devportal
   ```

   Source files are copied into the image by `Dockerfile`; restarting the old
   container alone does not deploy source changes.

4. Verify startup and HTTP availability:

   ```bash
   docker ps --filter name=aibox-devportal --format '{{.Names}} {{.Status}}'
   docker logs --tail 50 aibox-devportal
   curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://localhost:14000/
   ```

5. Report build errors, container errors, and the HTTP status. A successful
   deployment should show the container running and return HTTP 200.

## Mounted Files

The Compose service bind-mounts `Data` and external ComfyUI folders. Changes in
those mounted folders are immediately visible to the container and usually do
not require an image rebuild. Changes to `Components`, `Services`, `Models`,
`wwwroot`, `Program.cs`, project files, or application configuration do require
the rebuild workflow above.

## Razor Image Uploads

Use Blazor's `InputFile` component for image selection:

```razor
<InputFile OnChange="@SelectImage" style="margin: 1rem;" />
```

Back it with an `IBrowserFile` field and change handler:

```csharp
private IBrowserFile? image;
private void SelectImage(InputFileChangeEventArgs args) => image = args.File;
```

## Browser Troubleshooting

After a container recreation, old ASP.NET antiforgery cookies may reference a
data-protection key that no longer exists. If the browser shows antiforgery or
token-decryption errors, clear site cookies for the DevPortal and refresh.
