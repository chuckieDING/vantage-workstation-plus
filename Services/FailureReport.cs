using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace VantageWorkstationPlus.Services
{
    /// <summary>批量失败明细导出为 xlsx：方便用户修正后重跑。</summary>
    public static class FailureReport
    {
        public record FailedItem(string Id, string Category, string Reason);

        public static void WriteXlsx(string filePath, IEnumerable<FailedItem> items)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var doc = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook);
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();

            sheetData.Append(Row(new[] { "ID", "类别", "失败原因" }));
            foreach (var it in items)
                sheetData.Append(Row(new[] { it.Id, it.Category, it.Reason }));

            wsPart.Worksheet = new Worksheet(sheetData);
            wbPart.Workbook.Append(new Sheets(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = 1,
                Name = "失败明细",
            }));
            wbPart.Workbook.Save();
        }

        private static Row Row(IEnumerable<string> cells)
        {
            var row = new Row();
            foreach (var v in cells)
                row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(v ?? "") });
            return row;
        }
    }
}
