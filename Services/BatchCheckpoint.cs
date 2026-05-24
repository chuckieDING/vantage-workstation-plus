using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace VantageWorkstationPlus.Services
{
    /// <summary>批量任务断点续传：把已成功的 ID 写到 ./logs/batch-{module}-{guid}.checkpoint.json。
    /// 启动 / 重启时调 ListPending() 找未完成 checkpoint，问用户是否续传。</summary>
    public class BatchCheckpoint
    {
        public string Module { get; set; } = "";          // 例 "dehydration"
        public string Description { get; set; } = "";     // 例 "BasketId=15, 50 items"
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public List<string> AllItems { get; set; } = new();
        public HashSet<string> CompletedItems { get; set; } = new();
        public HashSet<string> FailedItems { get; set; } = new();

        [JsonIgnore]
        public string FilePath { get; private set; } = "";

        public IEnumerable<string> PendingItems =>
            AllItems.Where(x => !CompletedItems.Contains(x) && !FailedItems.Contains(x));

        public bool IsFinished => !PendingItems.Any();

        public static string CheckpointDir =>
            Path.Combine(AppContext.BaseDirectory, "logs");

        public static BatchCheckpoint Create(string module, string description, IEnumerable<string> items)
        {
            Directory.CreateDirectory(CheckpointDir);
            var cp = new BatchCheckpoint
            {
                Module = module,
                Description = description,
                AllItems = items.ToList(),
            };
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            cp.FilePath = Path.Combine(CheckpointDir, $"batch-{module}-{id}.checkpoint.json");
            cp.Save();
            return cp;
        }

        public void MarkSuccess(string item)
        {
            CompletedItems.Add(item);
            FailedItems.Remove(item);
            Save();
        }

        public void MarkFailed(string item)
        {
            FailedItems.Add(item);
            CompletedItems.Remove(item);
            Save();
        }

        public void Save()
        {
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>批次完成后删 checkpoint 文件。</summary>
        public void Complete()
        {
            try { File.Delete(FilePath); } catch { /* tolerable */ }
        }

        /// <summary>启动时调，列出仍有 pending 项的 checkpoint。</summary>
        public static List<BatchCheckpoint> ListPending(string? moduleFilter = null)
        {
            if (!Directory.Exists(CheckpointDir)) return new List<BatchCheckpoint>();
            var result = new List<BatchCheckpoint>();
            foreach (var file in Directory.GetFiles(CheckpointDir, "batch-*.checkpoint.json"))
            {
                try
                {
                    var cp = JsonConvert.DeserializeObject<BatchCheckpoint>(File.ReadAllText(file));
                    if (cp == null) continue;
                    cp.FilePath = file;
                    if (moduleFilter != null && cp.Module != moduleFilter) continue;
                    if (!cp.IsFinished) result.Add(cp);
                }
                catch { /* 损坏的 checkpoint 跳过 */ }
            }
            return result.OrderByDescending(c => c.StartedAt).ToList();
        }
    }
}
