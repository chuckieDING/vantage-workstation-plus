using System;
using System.Windows.Controls;
using Microsoft.Win32;
using VantageWorkstationPlus.Services;

namespace VantageWorkstationPlus.Panels
{
    /// <summary>4 个批量面板共用的工具：文件 dialog filter + 日志输出（带 UI 截断 + 落盘）。
    /// 不做基类，避免改 XAML 根类型；以静态方法方式被各 Panel 调用。</summary>
    public static class BatchPanelBase
    {
        public const string FilePickerFilter =
            "所有支持格式|*.xlsx;*.xlsm;*.xls;*.csv;*.txt;*.tsv|" +
            "Excel|*.xlsx;*.xlsm;*.xls|CSV|*.csv|文本|*.txt;*.tsv";

        /// <summary>UI 日志框最大保留行数；超过自动 Trim 头部。</summary>
        public const int MaxLogLines = 5000;

        /// <summary>追加一行日志到 UI 框 + 落盘 ./logs/{date}.log；自动截断防内存膨胀。</summary>
        public static void Log(TextBox logBox, ScrollViewer logScroll, string source, string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {msg}";
            logBox.AppendText(line + "\r\n");
            AppLog.Info(source + ": " + msg);

            int lines = logBox.LineCount;
            if (lines > MaxLogLines)
            {
                int firstChar = logBox.GetCharacterIndexFromLineIndex(lines - MaxLogLines);
                if (firstChar > 0)
                {
                    logBox.Text = logBox.Text.Substring(firstChar);
                    logBox.CaretIndex = logBox.Text.Length;
                }
            }

            logScroll.ScrollToBottom();
        }

        /// <summary>选文件 dialog 简化包装；返回 null 表示用户取消。</summary>
        public static string? PickFile(string title)
        {
            var dlg = new OpenFileDialog { Filter = FilePickerFilter, Title = title };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }
}
