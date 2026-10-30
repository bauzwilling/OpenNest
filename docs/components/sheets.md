# Sheets

Defines the sheets to nest onto (with optional holes) from closed polylines; supports gap, rows and copies.

## Example

**Files to download:**

- [⬇ sheets.ghx](files/sheets/sheets.ghx)

![sheets — sheets](img/sheets/sheets.png)

![sheets — sheets_screenshot](img/sheets/sheets_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Polylines** | Curve | tree | Closed sheet polylines, with optional holes. |
| **Gap** | Number | list | Gap between sheets. _Default: 0.1._ |
| **Rows** | Number | list | Number of sheets per row before partitioning. |
| **Copies** | Number | item | Number of copies of the same sheet. |
| **Offset** | Number | item | Inward MARGIN for nesting (model units; 0 = OFF, fast). Parts keep this setback from the sheet edge; any sheet holes grow by it. _Default: 0._ |
| **Quantity** | Number | list | Optional, one per sheet set (the last value repeats): how many of that sheet exist. Negative or empty = unlimited. A set is never used more often than its laid-out copies. See [sheet selection](../sheet_selection.md). |
| **Price** | Number | list | Optional, one per sheet set (the last value repeats): the price of ONE whole sheet, scrap included. Needed for the OpenNest2 option **Sheet Select = Min Price**. |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Sheets** | Generic | OpenNest nest_sheets data type. |
| **Polylines** | Curve | Generated sheet outline polylines. |
