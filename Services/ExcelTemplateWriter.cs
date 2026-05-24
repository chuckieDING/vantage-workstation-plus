using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace VantageWorkstationPlus.Services
{
    /// <summary>批量任务模板 xlsx 生成器：4 个 Panel 通用。
    /// 输出 sheet 第一行表头、第二行示例（加 [示例] 前缀），第三行起留空给用户填，
    /// 配合 SlideListReader.Read() 直接读回。</summary>
    public static class ExcelTemplateWriter
    {
        public record Column(string Header, string? Sample = null, string? Note = null);

        /// <summary>写一个 sheet 的模板。preFilledIds 会预填到第一列（每行一个）。</summary>
        public static void Write(string filePath, string sheetName,
            IReadOnlyList<Column> columns, IEnumerable<IReadOnlyList<string>> preFilledRows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var doc = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook);
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();

            // 表头行
            sheetData.Append(BuildRow(columns.Select(c => c.Header)));
            // 示例行（如有任一列有 Sample）
            if (columns.Any(c => !string.IsNullOrEmpty(c.Sample)))
                sheetData.Append(BuildRow(columns.Select(c =>
                    string.IsNullOrEmpty(c.Sample) ? "" : "[示例] " + c.Sample)));
            // 预填数据
            foreach (var row in preFilledRows)
                sheetData.Append(BuildRow(row));

            wsPart.Worksheet = new Worksheet(sheetData);
            wbPart.Workbook.Append(new Sheets(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = 1,
                Name = TruncateSheetName(sheetName),
            }));
            wbPart.Workbook.Save();
        }

        /// <summary>多 sheet 版本（归档场景用：蜡块一个 sheet、玻片一个 sheet）。</summary>
        public static void WriteMulti(string filePath,
            IReadOnlyList<(string SheetName, IReadOnlyList<Column> Columns,
                IEnumerable<IReadOnlyList<string>> Rows)> sheets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var doc = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook);
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var bookSheets = new Sheets();
            wbPart.Workbook.Append(bookSheets);

            uint sheetId = 1;
            foreach (var (name, columns, rows) in sheets)
            {
                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                sheetData.Append(BuildRow(columns.Select(c => c.Header)));
                if (columns.Any(c => !string.IsNullOrEmpty(c.Sample)))
                    sheetData.Append(BuildRow(columns.Select(c =>
                        string.IsNullOrEmpty(c.Sample) ? "" : "[示例] " + c.Sample)));
                foreach (var row in rows) sheetData.Append(BuildRow(row));
                wsPart.Worksheet = new Worksheet(sheetData);
                bookSheets.Append(new Sheet
                {
                    Id = wbPart.GetIdOfPart(wsPart),
                    SheetId = sheetId++,
                    Name = TruncateSheetName(name),
                });
            }
            wbPart.Workbook.Save();
        }

        private static Row BuildRow(IEnumerable<string> cells)
        {
            var row = new Row();
            foreach (var v in cells)
                row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(v ?? "") });
            return row;
        }

        private static string TruncateSheetName(string name)
        {
            // Excel sheet 名最长 31 字符，不能含 \ / ? * [ ]
            string s = name;
            foreach (char c in new[] { '\\', '/', '?', '*', '[', ']' }) s = s.Replace(c, '_');
            return s.Length > 31 ? s.Substring(0, 31) : s;
        }
    }
}
