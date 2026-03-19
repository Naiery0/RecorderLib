using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RecorderLib
{

    public class BenchmarkResult
    {
        public int TotalCycles { get; set; }
        public int CompletedCycles { get; set; }
        public long MemoryDeltaBytes { get; set; }
        public int HandleDelta { get; set; }
        public TimeSpan Elapsed { get; set; }
        public long PeakMemoryBytes { get; set; }
        public int PeakHandleCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public string Summary
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine("═══ 벤치마크 결과 ═══");
                sb.AppendLine($"사이클:     {CompletedCycles} / {TotalCycles}");
                sb.AppendLine($"소요 시간:  {Elapsed:mm\\:ss\\.f}");
                sb.AppendLine($"메모리 변동: {MemoryDeltaBytes / 1024.0 / 1024.0:+0.0;-0.0;0.0} MB");
                sb.AppendLine($"피크 메모리: {PeakMemoryBytes / 1024.0 / 1024.0:F0} MB");
                sb.AppendLine($"핸들 변동:  {HandleDelta:+0;-0;0}");
                sb.AppendLine($"피크 핸들:  {PeakHandleCount}");
                sb.AppendLine($"오류:       {Errors.Count}건");
                if (Errors.Count > 0)
                {
                    sb.AppendLine("─── 오류 목록 ───");
                    int show = Math.Min(Errors.Count, 10);
                    for (int i = 0; i < show; i++)
                        sb.AppendLine($"  • {Errors[i]}");
                    if (Errors.Count > show)
                        sb.AppendLine($"  … 외 {Errors.Count - show}건");
                }
                return sb.ToString();
            }
        }
    }
}
