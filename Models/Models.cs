using System.Collections.Generic;
using Newtonsoft.Json;

namespace VantageWorkstationPlus.Models
{
    public class Pathologist
    {
        public bool Active { get; set; }
        public string FullName { get; set; } = "";
        public int Id { get; set; }
        public string PathologistCode { get; set; } = "";
    }

    public class Location
    {
        public int LocationId { get; set; }
        public string LocationNm { get; set; } = "";
    }

    public class CaseViewItem
    {
        public string ExtSlideId { get; set; } = "";
        public string LisSlideId { get; set; } = "";
        public string? InsertTs { get; set; }
        public int? LisPathId { get; set; }
        public string LisPathFullName { get; set; } = "";
    }

    public class ScanResponse
    {
        public string? Error { get; set; }
        public List<CaseViewItem>? caseview { get; set; }
        public Location? location { get; set; }
        public List<Pathologist>? pathologist { get; set; }
    }

    public class SignOffRequest
    {
        public int fldrId { get; set; } = -1;
        public List<string> lisSlides { get; set; } = new();
        public List<string> timeStamps { get; set; } = new();
        public int locId { get; set; } = -1;
        public int pathId { get; set; } = -1;
    }

    /// 单玻片扫描结果（UI 内部）
    public class SlideScanOutcome
    {
        public bool Ok;
        public string InputId = "";
        public string LisSlideId = "";
        public string InsertTs = "";
        public int? LisPathId;
        public string Error = "";
    }
}
