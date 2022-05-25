using System.Threading;

namespace StressAgent.Models
{
    public class CpuThreadStateInfo
    {
        public int Percentage { get; set; }
        public int Duration { get; set; }
        public CancellationToken Token { get; set; }
    }
}
