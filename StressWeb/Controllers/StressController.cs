using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using StressAgent;
using StressWeb.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StressWeb.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StressController : ControllerBase
    {
        private readonly ILogger<StressController> _logger;
        private readonly StatefulServiceContext _serviceContext;
        private readonly IReliableStateManager _stateManager;
        private readonly TelemetryClient _telemetryClient;
        private readonly IConfiguration _configuration;
        private readonly FabricClient _fabricClient;
        private Dictionary<string, string> _traceProperties;

        private const string AgentTemplate = "{0}/agent_{1}";

        //dictionaries        
        private IReliableDictionary<Guid, StressRecord> _stressDictionary;

        public StressController(ILogger<StressController> logger,
            StatefulServiceContext serviceContext,
            IReliableStateManager stateManager,
            FabricClient fabricClient,
            TelemetryClient telemetryClient,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceContext = serviceContext;
            _stateManager = stateManager;
            _fabricClient = fabricClient;
            _telemetryClient = telemetryClient;
            _configuration = configuration;
            _stressDictionary = _stateManager.GetOrAddAsync<IReliableDictionary<Guid, StressRecord>>("StressDictionary").Result;
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteAgents(CancellationToken cancellationToken)
        {
            // Enumerate dictionary and delete all agents
            using (var tx = _stateManager.CreateTransaction())
            {
                var agentList = await _stressDictionary.CreateEnumerableAsync(tx);
                var enumerator = agentList.GetAsyncEnumerator();

                while (await enumerator.MoveNextAsync(cancellationToken))
                {
                    var agentToRemove = await _stressDictionary.TryRemoveAsync(tx, enumerator.Current.Key);
                    if (agentToRemove.HasValue)
                    {
                        var deleteServiceDescription = new DeleteServiceDescription(new Uri(string.Format(AgentTemplate, _serviceContext.CodePackageActivationContext.ApplicationName, agentToRemove.Value.AgentId.ToString())))
                        {
                            ForceDelete = true
                        };
                        await _fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription);
                    };
                }

                await tx.CommitAsync();
            }            
            return Ok();
        }

        [HttpDelete("{agentId}")]
        public async Task<IActionResult> DeleteAgents(Guid agentId, CancellationToken cancellationToken)
        {
            try
            {
                using (var tx = _stateManager.CreateTransaction())
                {
                    var agentToRemove = await _stressDictionary.TryRemoveAsync(tx, agentId);
                    if (agentToRemove.HasValue)
                    {
                        var deleteServiceDescription = new DeleteServiceDescription(new Uri(string.Format(AgentTemplate, _serviceContext.CodePackageActivationContext.ApplicationName, agentToRemove.Value.AgentId.ToString())))
                        {
                            ForceDelete = true
                        };
                        await _fabricClient.ServiceManager.DeleteServiceAsync(deleteServiceDescription);
                    };
                    await tx.CommitAsync();
                }
            }
            catch (Exception)
            {
                throw;
            }
            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> CreateAgent(StressModel stressModel, CancellationToken cancellationToken)
        {
            #region tracing
            _traceProperties = new Dictionary<string, string>{
                {"Service", "StressWeb"},
                {"Message", string.Format("Create Stress, CPU={0}, MB={1}, Active={2} ",stressModel.CpuTarget, stressModel.MemoryTarget, stressModel.IsActive) }
            };
            _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
            #endregion
            List<Guid> agentsCreated = new List<Guid>();
            try
            {
                var applicationName = _serviceContext.CodePackageActivationContext.ApplicationName;

                stressModel.CpuTarget = stressModel.CpuTarget < 0 ? 0 : stressModel.CpuTarget;
                stressModel.CpuTarget = stressModel.CpuTarget > 100 ? 100 : stressModel.CpuTarget;
                stressModel.MemoryTarget = stressModel.MemoryTarget < 0 ? 1 : stressModel.MemoryTarget;
                stressModel.MemoryTarget = stressModel.MemoryTarget >4096 ? 4096 : stressModel.MemoryTarget;

                for(int i=0; i<stressModel.NumberOfAgentsToCreate; i++)
                { 
                    var stressRecord = new StressRecord
                    {
                        AgentId = Guid.NewGuid(),
                        Model = stressModel
                    };

                    // Save agent with initial state
                    using (var tx = _stateManager.CreateTransaction())
                    {
                        var savedRecord = await _stressDictionary.AddOrUpdateAsync(tx, stressRecord.AgentId, stressRecord, (key, value) => stressRecord, TimeSpan.FromSeconds(5), cancellationToken);

                        await tx.CommitAsync();
                    }

                    // Create ServiceDescription for Agent
                    ServiceDescription serviceDescription = new StatelessServiceDescription
                    {
                        ApplicationName = new Uri(string.Format(applicationName)),
                        InstanceCount = 1,
                        ServicePackageActivationMode = ServicePackageActivationMode.ExclusiveProcess,
                        PartitionSchemeDescription = new SingletonPartitionSchemeDescription(),
                        ServiceName = new Uri(string.Format(AgentTemplate, applicationName , stressRecord.AgentId.ToString())),
                        ServiceTypeName = "StressAgentType",
                        PlacementConstraints = _configuration.GetSection("GatewaySettings").GetValue<string>("AgentPlacementConstraint")
                    };

                    // Create Load Metrics
                    StatelessServiceLoadMetricDescription loadMetric = new StatelessServiceLoadMetricDescription()
                    {
                        Name = "servicefabric:/_CpuCores",
                        DefaultLoad = 1,
                        Weight = ServiceLoadMetricWeight.High
                    };
                    serviceDescription.Metrics.Add(loadMetric);

                    // Create the service instance.  
                    try
                    {
                        var startTime = DateTime.UtcNow;
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            await _fabricClient.ServiceManager.CreateServiceAsync(serviceDescription);
                        }
                        finally
                        {
                            timer.Stop();
                            _telemetryClient.TrackDependency("FabricClient.ServiceManager", "CreateServiceAsync", serviceDescription.ServiceName.ToString(), startTime, timer.Elapsed, true);
                        }
                        #region tracing
                        _traceProperties = new Dictionary<string, string>{
                            {"Service", "StressWeb"},
                            {"Message", string.Format("Created Stress Agent {0}", serviceDescription.ServiceName) }
                        };
                        _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                        #endregion
                    }
                    catch (AggregateException ae)
                    {
                        #region tracing
                        _traceProperties = new Dictionary<string, string>{
                            {"Service", "StressWeb"},
                            {"Message", string.Format("CreateService failed, Stress Agent {0}", serviceDescription.ServiceName) }
                        };
                        int eaCount = 0;
                        foreach (Exception ex in ae.InnerExceptions)
                        {
                            _traceProperties.Add(string.Format("Exception{0}", ++eaCount), string.Format("HResult: {0} Message: {1}", ex.HResult, ex.Message));
                        }
                        _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                        _telemetryClient.TrackException(ae, _traceProperties);
                        #endregion
                    }

                    // Wait for the agent to be ready
                    ServiceStatus serviceStatus = ServiceStatus.Unknown;
                    HealthState serviceHealthState = HealthState.Unknown; 
                    Stopwatch sw = new Stopwatch();
                    sw.Start();
                    do
                    {
                        await Task.Delay(200, cancellationToken);
                        var serviceAgent = await _fabricClient.QueryManager.GetServiceListAsync(serviceDescription.ApplicationName, serviceDescription.ServiceName, TimeSpan.FromSeconds(15), cancellationToken);
                        ServiceHealth serviceHealth;
                        try
                        {
                            serviceHealth = await _fabricClient.HealthManager.GetServiceHealthAsync(serviceDescription.ServiceName, TimeSpan.FromSeconds(15), cancellationToken);
                            serviceHealthState = serviceHealth.PartitionHealthStates.FirstOrDefault().AggregatedHealthState;
                        }
                        catch (FabricException)
                        {
                            // ignoring FabricHealthEntityNotFound
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                        serviceStatus = serviceAgent.FirstOrDefault().ServiceStatus;
                        #region tracing
                        _traceProperties = new Dictionary<string, string>{
                            {"Service", "StressWeb"},
                            {"Agent", serviceDescription.ServiceName.ToString()},
                            {"Message", string.Format("Waiting for Agent to be ready, Current Status {0}", serviceStatus.ToString()) }
                        };
                        _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                        #endregion

                        if (sw.ElapsedMilliseconds > 15000) throw new TimeoutException(string.Format("{0} not healthy after 15 seconds", serviceDescription));
                    } while ((serviceStatus != ServiceStatus.Active) && (serviceHealthState == HealthState.Ok));

                    // create remoting proxy to our new instance
                    IStressYouOut stressClient = ServiceProxy.Create<IStressYouOut>(serviceDescription.ServiceName);

                    // set stress agent properties
                    int newMemoryTarget = await stressClient.SetMemoryAllocationTargetInMb(stressModel.MemoryTarget);
                    double newCpuTarget = await stressClient.SetCpuTargetPercentage(stressModel.CpuTarget);
                    bool isStressActive;
                    if (stressModel.IsActive)
                        isStressActive = await stressClient.StartStressingAsync();
                    else
                        isStressActive = await stressClient.StopStressingAsync();

                    #region tracing
                    _traceProperties = new Dictionary<string, string>{
                        {"Service", "StressWeb"},
                        {"StressActive", isStressActive.ToString()},
                        {"MemoryTarget", newMemoryTarget.ToString()},
                        {"CpuTarget", newCpuTarget.ToString() }
                    };
                    _telemetryClient.TrackEvent("ResourceGovTrace", _traceProperties);
                    #endregion

                    agentsCreated.Add(stressRecord.AgentId);
                }

                return Ok(agentsCreated);
            }
            catch (Exception ex)
            {
                #region tracing
                _traceProperties = new Dictionary<string, string>{
                    {"Service", "StressWeb"},
                    {"Message", string.Format("Exception:", ex.Message) }
                };
                _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                _telemetryClient.TrackException(ex, _traceProperties);
                #endregion
                throw;
            }
        }
    }
}
