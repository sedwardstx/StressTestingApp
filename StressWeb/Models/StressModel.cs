using System.Runtime.Serialization;

namespace StressWeb.Models
{
	[DataContract]
	public class StressModel
	{
		[DataMember]
		public bool IsActive { get; set; }
		[DataMember]
		public int MemoryTarget { get; set; }
		[DataMember]
		public int CpuTarget { get; set; }
		[DataMember]
		public int NumberOfAgentsToCreate { get; set; }
	}
}
