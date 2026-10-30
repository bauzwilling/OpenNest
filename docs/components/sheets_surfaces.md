# Sheets (Surfaces)

Defines the sheets to nest onto from planar surfaces instead of polylines; holes come from the surface boundaries.

## Example

**Files to download:**

- [⬇ sheet_surface.ghx](files/sheets_surfaces/sheet_surface.ghx)

![sheets_surfaces — sheet_surface](img/sheets_surfaces/sheet_surface.png)

![sheets_surfaces — sheet_surface_screenshot](img/sheets_surfaces/sheet_surface_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Surfaces** | Brep | list | Planar surfaces with optional holes. |
| **Gap** | Number | list | Gap between sheets. _Default: 0.1._ |
| **Rows** | Number | list | Number of sheets per row; partitioned if exceeded. |
| **Count** | Number | list | Number of copies of the same sheet. |
| **Offset** | Number | item | Inward MARGIN for nesting (model units; 0 = OFF, fast). Parts keep this setback from the sheet edge; any sheet holes grow by it. _Default: 0._ |
| **Quantity** | Number | list | Optional, one per sheet set (the last value repeats): how many of that sheet exist. Negative or empty = unlimited. A set is never used more often than its laid-out copies. See [sheet selection](../sheet_selection.md). |
| **Price** | Number | list | Optional, one per sheet set (the last value repeats): the price of ONE whole sheet, scrap included. Needed for the OpenNest2 option **Sheet Select = Min Price**. |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Sheets** | Generic | OpenNest nest_sheets data type. |
| **Polylines** | Curve | Sheet boundary polylines. |
