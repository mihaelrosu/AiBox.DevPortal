---
name: RadzenDataGrid_def
description: Default RadzenDataGrid markup and authoring rules for AiBox.DevPortal Blazor pages.
---

# RadzenDataGrid Defaults

When creating or updating a `RadzenDataGrid` in this repository, use the
following defaults unless there is a concrete reason not to:

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

## Rules

1. Prefer these defaults on every grid used in a page or component.
2. If a component contains more than one grid, use distinct `@ref` fields per
   grid to avoid collisions. Name them `grid1`, `grid2`, `grid3`, and so on.
3. Keep paging and filtering enabled unless the data set or UX explicitly
   requires a different behavior.
4. Do not enable virtualization by default.

## Notes

- If the grid already relies on custom paging, sorting, or filtering behavior,
  preserve the behavior and only add the shared defaults that do not conflict.
- If a page has multiple grids, prefer `grid1`, `grid2`, `grid3`, etc. rather
  than reusing the same `grid` field.
