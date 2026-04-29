using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace VantageWorkstationPlus.Services
{
    public static class SlideListReader
    {
        private static readonly Regex SlidePattern = new(@"^\d+-\d+(?:-\d+){1,3}$");
        private static readonly Regex SplitRe = new(@"[\s,;；，\t]+");
        private static int _codePagesRegistered;

        static SlideListReader()
        {
            // ExcelDataReader 读 .xls (BIFF) 时需要 windows-1252 等编码，.NET 8 默认不带
            EnsureCodePagesRegistered();
        }

        private static void EnsureCodePagesRegistered()
        {
            if (System.Threading.Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static bool IsSlideId(string s) => !string.IsNullOrEmpty(s) && SlidePattern.IsMatch(s.Trim());

        public static List<string> Read(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            List<string> raw = ext switch
            {
                ".xls" or ".xlsx" or ".xlsm" => FromExcel(path),
                ".csv" => FromCsv(path),
                ".txt" or ".tsv" or "" => FromText(path),
                _ => throw new InvalidOperationException($"不支持的格式: {ext}"),
            };
            // 去重保序
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (var s in raw)
                if (seen.Add(s)) result.Add(s);
            return result;
        }

        /// <summary>用 ExcelDataReader 同时支持 .xls (BIFF) / .xlsx / .xlsm，扫所有 sheet 的所有列。</summary>
        private static List<string> FromExcel(string path)
        {
            var ids = new List<string>();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            do
            {
                while (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string? val = reader.GetValue(i)?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(val) && IsSlideId(val))
                        {
                            ids.Add(val);
                            break;  // 一行最多一个 ID
                        }
                    }
                }
            } while (reader.NextResult());
            return ids;
        }

        private static List<string> FromCsv(string path)
        {
            var ids = new List<string>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var first = line.Split(',').FirstOrDefault()?.Trim() ?? "";
                if (IsSlideId(first)) ids.Add(first);
            }
            return ids;
        }

        private static List<string> FromText(string path)
        {
            var ids = new List<string>();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                foreach (var piece in SplitRe.Split(line.Trim()))
                    if (IsSlideId(piece)) ids.Add(piece);
            }
            return ids;
        }
    }
}
