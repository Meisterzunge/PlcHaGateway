using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Mail;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using TwinCAT.Ads.SumCommand;
using Utilities.Core;

namespace HassModel;


/// <summary>
/// Manage to connect <i>Home Assistant</i> and <i>TwinCAT PLC</i>.
/// </summary>
[NetDaemonApp]
public partial class PlcHaGatewayApp : IAsyncInitializable, IDisposable
{
    public PlcHaGatewayApp(IConfiguration config, ILogger<PlcHaGatewayApp> logger, IHaContext context, IScheduler scheduler, IMqttEntityManager entityManager)
    {
        new Common(logger);

        this.config = config;
        this.plc = config.CreatePlcFromSettings();
        this.ha = context;
        this.scheduler = scheduler;
        this.entityManager = entityManager;
    }


    #region Properties.Management
    //private CancellationToken ConnectionToken => connectionToken.Token;
    //private CancellationTokenSource connectionToken = new();

    public IReadOnlyCollection<VirtualDevice> DeviceMappings;
    public IReadOnlyCollection<IMapping> SymbolMappings;
    public IEnumerable<IMapping> Mappings => DeviceMappings
        .SelectMany(d => d.Mappings)
        .Concat(SymbolMappings);
    #endregion


    #region Initialization
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        LogEvent.Ads.LogInformation("Establish connection.");
        try
        {
            await plc.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent.Ads.LogError(ex, "Failed to establish PLC connection.");
            throw;
        }

        LogEvent.Gw.LogInformation("Creating mappings...");
        try
        {
            this.DeviceMappings = MappingFactory.CreateDevices(plc.MappedDevices, plc);
            this.SymbolMappings = new IMapping[0]; // (BETA) ... Create device-less mappings 
            // this.SymbolMappings = MappingFactory.CreateMappings(plc.MappedSymbols);
            var totalMappings = Mappings.Count();

            if (totalMappings == 0)
                LogEvent.Gw.LogWarning($"No mappings found to create.");
            else
            {
                LogEvent.Gw.LogInformation($"Created {DeviceMappings.Count} virtual device(s).");
                LogEvent.Gw.LogInformation($"Created {totalMappings} mapping(s) total.");
            }
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mappings.");
            throw;
        }

        LogEvent.Gw.LogInformation("Generate export files.");
        try
        {
            await Task.WhenAll(CreateExportFilesAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to generate export file.");
        }

        LogEvent.Gw.LogInformation("Binding mappings...");
        foreach (var mapping in Mappings)
        {
            try
            {
                Task bindingTask;
                switch (mapping.Backend)
                {
                    case IntegrationType.Native: bindingTask = mapping.BindToNativeEntity(ha); break;
                    case IntegrationType.Mqtt: bindingTask = mapping.CreateMqttEntity(entityManager); break;

                    default: throw new NotSupportedException($"Failed to bind mapping of not supported backend '{mapping.Backend}'!");
                }
                await bindingTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEvent.Gw.LogError(ex, "Failed to create entity '{0}'.", mapping);
            }
        }

        LogEvent.Ads.LogInformation($"Start read job for cyclic update.");
        this.plcReadCyclic = plc.CreateSymbolReadCommand(Mappings.OfCyclicallyReadable());

        scheduler.ScheduleAsync(CyclicUpdateMappingsAsync);
    }
    public void Dispose() => plc.Dispose();
    #endregion
    #region Communication
    private async Task<IMapping[]?> UpdateMappingsAsync(AutomationContext context, CancellationToken cancellationToken)
    {
        // Update (Gateway -> ...) dirty mappings:
        var updated = await Mappings.UpdateWhereDirtyAsync(context, plc, entityManager, cancellationToken).ConfigureAwait(false);
        if (!updated.IsEmpty())
            LogEvent.Hass.LogTrace($"Updated {updated.Length} [GW -> {context}] mapping(s) in cyclic update.");

        return (updated);
    }
    private async Task CyclicUpdateMappingsAsync(IScheduler scheduler, CancellationToken cancellationToken)
    {
        double updateFactor = 1.0;

        // Step 1) Update (Gateway -> PLC) dirty mappings:
        var updated = await UpdateMappingsAsync(AutomationContext.Plc, cancellationToken)
            .DetermineUpdateFactor(ref updateFactor)
            .ConfigureAwait(false);

        // Step 2) Refresh actual PLC values (Gateway <- PLC):
        var cmd = plcReadCyclic;
        if (!updated.IsEmpty())
            // Create temporary sum command for all mappings, excluding the updated ones:
            cmd = plc.CreateSymbolReadCommand(Mappings
                .OfCyclicallyReadable()
                .Except(updated));

        var changes = await cmd.ReadMappingsAsync(cancellationToken).ConfigureAwait(false);
        if (changes > 0)
            LogEvent.Ads.LogTrace($"Refreshed {changes} [GW <- {AutomationContext.Plc}] mapping(s) in cyclic update.");

        // Step 3) Repeat update (Gateway -> HASS) dirty mappings due to the latest changes:
        if (changes > 0)
            await UpdateMappingsAsync(AutomationContext.Hass, cancellationToken)
                .DetermineUpdateFactor(ref updateFactor)
                .ConfigureAwait(false);

        // Schedule next update:
        var delay = (config.GetValue<double>("CyclicUpdate") * updateFactor);
        scheduler.ScheduleAsync(TimeSpan.FromSeconds(delay), CyclicUpdateMappingsAsync);
    }
    #endregion


    private IConfiguration config;

    private Plc plc;
    private SumSymbolRead plcReadCyclic;

    private IHaContext ha;
    private IScheduler scheduler;
    private IMqttEntityManager entityManager;
}

internal static partial class Ext
{
    #region Constants
    private static readonly TimeSpan BoostDuration = TimeSpan.FromSeconds(30);
    #endregion


    public static Task<IMapping[]?> DetermineUpdateFactor(this Task<IMapping[]?> source, ref double factor)
    {
        // Improve user experience by boosting update cycle whenever interaction is detected:
        if (source.Result.Any(m => m.FunctionBlockType.IsOperationalType()))
            stopBoost = (DateTime.Now + BoostDuration);

        if (stopBoost is null)
            ; // Don't touch updates.
        else if (DateTime.Now < stopBoost)
            factor = 0.3;
        else
            stopBoost = null;

        return (source);
    }
    private static DateTime? stopBoost = null;
}