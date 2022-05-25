using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using StressAgent.Models;
using StressAgent.Services.Mandelbrot;

namespace StressAgent
{
    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class StressAgent : StatelessService, IStressYouOut
    {
        // AI Tracing
        private readonly TelemetryConfiguration _configuration;
        private readonly TelemetryClient _telemetryClient;
        private Dictionary<string, string> _traceProperties;

        private static readonly object _CPUTestLock = new object();
        private bool _stressingActive = false;
        private int _cpuTarget = 1;
        private int _memoryAllocationTarget = 1;
        private static Random random = new Random();

        public StressAgent(StatelessServiceContext context)
            : base(context)
        {
            // configuration
            var settings = this.Context.CodePackageActivationContext.GetConfigurationPackageObject("config").Settings;
            var instrumentationKey = settings.Sections["AgentSettings"].Parameters["InstrumentationKey"].Value;

            // setup AI Telemetry and Live Metrics
            _configuration = TelemetryConfiguration.CreateDefault();
            _configuration.InstrumentationKey = instrumentationKey;
            _telemetryClient = new TelemetryClient(_configuration);
        }

        public Task<int> SetCpuTargetPercentage(int cpuPercentUsageTarget)
        {
            #region tracing
            _traceProperties = new Dictionary<string, string>{
                {"Service", "StressAgent"},
                {"Message", string.Format("Entered SetCpuTargetPercentage, cpuPercentUsageTarget={0}",cpuPercentUsageTarget) }
            };
            _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
            #endregion

            try
            {
                if (cpuPercentUsageTarget < 0) cpuPercentUsageTarget = 0;
                if (cpuPercentUsageTarget > 100) cpuPercentUsageTarget = 100;

                _cpuTarget = cpuPercentUsageTarget;
                return Task.FromResult(_cpuTarget);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                throw;
            }
        }

        public Task<int> SetMemoryAllocationTargetInMb(int memoryInMbToAllocate)
        {
            #region tracing
            _traceProperties = new Dictionary<string, string>{
                                {"Service", "StressAgent"},
                                {"Message", string.Format("Entered SetMemoryAllocationTargetInMb, memoryInMbToAllocate={0}",memoryInMbToAllocate) }
                            };
            _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
            #endregion
            try
            {
                if (memoryInMbToAllocate < 0)
                    memoryInMbToAllocate = 1;
                if (memoryInMbToAllocate > 4096)
                    memoryInMbToAllocate = 4096;
                _memoryAllocationTarget = memoryInMbToAllocate;
                return Task.FromResult(_memoryAllocationTarget);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                throw;
            }

        }

        public Task<bool> StartStressingAsync()
        {
            try
            {
                #region tracing
                _traceProperties = new Dictionary<string, string>{
                    {"Service", "StressAgent"},
                    {"Message", "Entered StartStressingAsync" }
                };
                _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                #endregion
                _stressingActive = true;
                return Task.FromResult(_stressingActive);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                throw;
            }
        }

        public Task<bool> StopStressingAsync()
        {
            try
            {
                #region tracing
                _traceProperties = new Dictionary<string, string>{
                    {"Service", "StressAgent"},
                    {"Message", "Entered StopStressingAsync" }
                };
                _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
                #endregion
                _stressingActive = false;
                return Task.FromResult(_stressingActive);
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                throw;
            }
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return this.CreateServiceRemotingInstanceListeners();
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            int currentAllocation = 0;
            bool clearMemory = false;
            var contextId = this.Context.CodePackageActivationContext.ContextId;
            var currentState = "Starting";
            HealthInformation healthInfo;

            #region tracing
            ServiceEventSource.Current.ServiceMessage(this.Context, "StressResourcesService: Entered RunAsync");
            _traceProperties = new Dictionary<string, string>{
                {"Service", "StressAgent"},
                {"ContextId", contextId},
                {"Message", "StressAgent: Starting" }
            };
            _telemetryClient.TrackTrace("ResourceGovTrace", _traceProperties);
            #endregion

            StringBuilder wastedMemoryBuffer = new StringBuilder(1024 * 1024);

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    while (_stressingActive)
                    {
                        System.GC.Collect();

                        while (currentAllocation < _memoryAllocationTarget)
                        {
                            currentState = "Allocating Memory";
                            healthInfo = new HealthInformation("LoadProfile", string.Format("Cpu: {0}, Mem:{1}, State:{2}", _cpuTarget, _memoryAllocationTarget, currentState), HealthState.Ok);
                            this.Partition.ReportPartitionHealth(healthInfo);

                            Random rand = new Random();

                            cancellationToken.ThrowIfCancellationRequested();
                            wastedMemoryBuffer.Append(RandomString(350 * 1024));
                            currentAllocation++;
                        }

                        if (currentAllocation > _memoryAllocationTarget)
                        {
                            wastedMemoryBuffer.Clear();
                            currentAllocation = 0;
                        }

                        currentState = "StressCPU";
                        healthInfo = new HealthInformation("LoadProfile", string.Format("Cpu: {0}, Mem:{1}, State:{2}", _cpuTarget, _memoryAllocationTarget, currentState), HealthState.Ok);
                        this.Partition.ReportPartitionHealth(healthInfo);

                        ParallelOptions po = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Environment.ProcessorCount,
                            TaskScheduler = TaskScheduler.Current,
                            CancellationToken = cancellationToken
                        };

                        ParallelLoopResult plr = Parallel.For(0, Environment.ProcessorCount, po,
                            index =>
                            {
                                LoadCpu(_cpuTarget, 60, cancellationToken);
                            });

                        cancellationToken.ThrowIfCancellationRequested();

                        clearMemory = true;
                    }

                    if (clearMemory)
                    {
                        wastedMemoryBuffer.Clear();
                        clearMemory = false;
                    }

                    currentAllocation = 0;
                    currentState = "Stress Deactivated";
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    healthInfo = new HealthInformation("LoadProfile", string.Format("Cpu: {0}, Mem:{1}, State:{2}", _cpuTarget, _memoryAllocationTarget, currentState), HealthState.Ok);
                    this.Partition.ReportPartitionHealth(healthInfo);
                }
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackException(ex);
                throw;
            }
        }

        private double LoadCpu(int percentage, int duration, CancellationToken cancellationToken)
        {
            int computed = 0;
            Mandelbrot mb = new Mandelbrot();
            Stopwatch stopWatch = new Stopwatch();
            Stopwatch testDuration = new Stopwatch();
            stopWatch.Start();
            testDuration.Start();
            while (testDuration.Elapsed < TimeSpan.FromSeconds(duration))
            {
                // Make the loop work for first percentage, then sleep remaining 100-percentage milliseconds, 
                // So 40% utilization means work 40ms and sleep remaining 60ms
                mb.ComputeSync(.5);
                computed++;

                if (stopWatch.ElapsedMilliseconds > (percentage))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(Math.Max(1, (int)(100 - stopWatch.ElapsedMilliseconds)));
                    stopWatch.Reset();
                    stopWatch.Start();
                }
            }

            return computed;
        }

        private string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}
