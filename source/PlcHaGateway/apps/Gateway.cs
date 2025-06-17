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
public class PlcHaGatewayApp : IAsyncInitializable, IDisposable
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

    public IEnumerable<IMapping> Mappings => DeviceMappings
        .SelectMany(d => d.Mappings)
        .Concat(SymbolMappings);
    public IReadOnlyCollection<VirtualDevice> DeviceMappings;
    public IReadOnlyCollection<IMapping> SymbolMappings;
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
            this.SymbolMappings = MappingFactory.CreateMappings(plc.MappedSymbols);
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

        LogEvent.Hass.LogInformation("Binding static mappings...");
        // (BETA) ... hass-mappings
        // berücksichtigen dass es auch mappings gibt, die direkt auf entities im `IHaContext` context gemapped werden!
        /*
        var entityId = mapping.EntityId;
        if (!entityId.Contains('.'))
            entityId = $"{mapping.Info.EntityTypeName}.{entityId}";
        */

        LogEvent.Mqtt.LogInformation("Binding MQTT mappings...");
        foreach (var mapping in Mappings)
        {
            try
            {
                await mapping.CreateMqttEntity(entityManager).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEvent.Mqtt.LogError(ex, "Failed to create MQTT entity '{0}'.", mapping);
            }
        }

        LogEvent.Ads.LogInformation($"Start read job for cyclic update.");
        this.cyclicMappings = plc.CreateSymbolReadCommand(Mappings);

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
        double factor = 1.0;

        // Step 1) Update (Gateway -> PLC) dirty mappings:
        var updated = await UpdateMappingsAsync(AutomationContext.Plc, cancellationToken)
            .DetermineUpdateFactor(ref factor)
            .ConfigureAwait(false);

        // Step 2) Refresh actual PLC values (Gateway <- PLC):
        var cmd = cyclicMappings;
        if (!updated.IsEmpty())
            // Create temporary sum command for all mappings, excluding the updated ones:
            cmd = plc.CreateSymbolReadCommand(Mappings.Except(updated));

        var changes = await cmd.ReadMappingsAsync(cancellationToken).ConfigureAwait(false);
        if (changes > 0)
            LogEvent.Ads.LogTrace($"Refreshed {changes} [GW <- {AutomationContext.Plc}] mapping(s) in cyclic update.");

        // Step 3) Repeat update (Gateway -> HASS) dirty mappings due to the latest changes:
        if (changes > 0)
            await UpdateMappingsAsync(AutomationContext.Hass, cancellationToken)
                .DetermineUpdateFactor(ref factor)
                .ConfigureAwait(false);

        // Schedule next update:
        var delay = (config.GetValue<double>("CyclicUpdate") * factor);
        scheduler.ScheduleAsync(TimeSpan.FromSeconds(delay), CyclicUpdateMappingsAsync);
    }
    #endregion


    private IConfiguration config;

    private Plc plc;
    private SumSymbolRead cyclicMappings;

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