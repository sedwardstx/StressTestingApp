using Microsoft.ServiceFabric.Services.Remoting;
using Microsoft.ServiceFabric.Services.Remoting.FabricTransport;
using System.Threading.Tasks;

[assembly: FabricTransportServiceRemotingProvider(RemotingListenerVersion = RemotingListenerVersion.V2, RemotingClientVersion = RemotingClientVersion.V2)]
namespace StressAgent
{
	public interface IStressYouOut : IService
	{
		Task<int> SetMemoryAllocationTargetInMb(int memoryInMbToAllocate);
		Task<int> SetCpuTargetPercentage(int cpuPercentUsageTarget);
		Task<bool> StartStressingAsync();
		Task<bool> StopStressingAsync();
	}
}
