using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.ServiceFabric.Data;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.QuickPulse;

namespace StressWeb
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class StressWeb : StatefulService
    {
        private readonly FabricClient _fabricClient;
        private readonly TelemetryConfiguration _configuration;
        private readonly TelemetryClient _telemetryClient;

        public StressWeb(StatefulServiceContext context)
            : base(context)
        {
            var clientSettings = new FabricClientSettings()
            {
                HealthOperationTimeout = TimeSpan.FromSeconds(120),
                HealthReportSendInterval = TimeSpan.FromSeconds(0),
                HealthReportRetrySendInterval = TimeSpan.FromSeconds(40),
            };
            _fabricClient = new FabricClient(clientSettings);

            // configuration
            var settings = this.Context.CodePackageActivationContext.GetConfigurationPackageObject("config").Settings;
            var instrumentationKey = settings.Sections["GatewaySettings"].Parameters["InstrumentationKey"].Value;
            var apiKey = settings.Sections["GatewaySettings"].Parameters["LiveTelemetryApiKey"].Value;

            // setup AI Telemetry and Live Metrics
            _configuration = TelemetryConfiguration.CreateDefault();
            _configuration.InstrumentationKey = instrumentationKey;
            QuickPulseTelemetryProcessor quickPulseProcessor = null;
            _configuration.DefaultTelemetrySink.TelemetryProcessorChainBuilder
                .Use((next) =>
                {
                    quickPulseProcessor = new QuickPulseTelemetryProcessor(next);
                    return quickPulseProcessor;
                })
                .Build();

            var quickPulseModule = new QuickPulseTelemetryModule
            {
                // Secure the control channel.
                AuthenticationApiKey = apiKey
            };
            quickPulseModule.Initialize(_configuration);
            quickPulseModule.RegisterTelemetryProcessor(quickPulseProcessor);

            _telemetryClient = new TelemetryClient(_configuration);
        }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new ServiceReplicaListener[]
            {
                new ServiceReplicaListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        // Get AI configuration
						var gatewaySettings = this.Context.CodePackageActivationContext.GetConfigurationPackageObject("Config").Settings.Sections["GatewaySettings"];
                        var appInsightsConnectionString = gatewaySettings.Parameters["ApplicationInsightsConnectionString"].Value;

                        return new WebHostBuilder()
                                    .UseKestrel()
                                    .ConfigureServices(
                                        services => services
                                            .AddSingleton<StatefulServiceContext>(serviceContext)
                                            .AddSingleton<IReliableStateManager>(this.StateManager)
                                            .AddSingleton<FabricClient>(_fabricClient)
                                            .AddSingleton<TelemetryClient>(_telemetryClient)
                                            .AddApplicationInsightsTelemetry(appInsightsConnectionString)
                                            )
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseStartup<Startup>()
                                    .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
                                    .UseUrls(url)
                                    .Build();
                    }))
            };
        }
    }
}
