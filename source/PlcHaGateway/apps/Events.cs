using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;


public enum E_Mfr_NotifySeverity : uint
{
    Info = 0,
    Warning = 1,
    Error = 2
}


public interface IEventBinding
{
    string EntityId { get; }
    string Name { get; }
    VirtualDevice? Owner { get; }
    SymbolType SymbolType { get; }

    Task InitAsync(IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel);
    Task ProcessAsync(IHaContext ha, CancellationToken cancel);
}


internal static class EventBindingFactory
{
    public static IEventBinding? TryCreate(ISymbol symbol, VirtualDevice? owner = null)
    {
        if (!symbol.TryGetSymbolType(out var symbolType, out _))
            return null;

        try
        {
            return symbolType switch
            {
                SymbolType.Notification => new NotificationBinding(symbol, owner),
                SymbolType.Event        => new EventBinding(symbol, owner),
                _                       => null
            };
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create event binding for symbol '{0}'.", symbol.InstancePath);
            return null;
        }
    }
}


internal abstract class EventBindingBase : IEventBinding
{
    protected EventBindingBase(ISymbol symbol, VirtualDevice? owner)
    {
        var sym = (Symbol)symbol;
        this.connection = (IAdsConnection)sym.Connection!;

        this.Owner = owner;
        this.Symbol = symbol;
        this.SymbolType = symbol.GetSymbolType(out _);

        this.EntityId = NormalizeId(symbol.GetEntityInfo().Path);
        this.Name = symbol.GetEntityName(owner) ?? symbol.InstanceName;

        this.bBusySymbol = symbol.SubSymbols["bBusy"];
        this.busyReadCmd  = CreateSumRead(bBusySymbol);
        this.busyWriteCmd = new SumSymbolWrite(connection, new[] { bBusySymbol }.ToList());
    }

    #region Properties
    public string EntityId { get; }
    public string Name { get; }
    public VirtualDevice? Owner { get; }
    public SymbolType SymbolType { get; }
    protected ISymbol Symbol { get; }
    #endregion

    public virtual Task InitAsync(IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel) => Task.CompletedTask;
    public abstract Task ProcessAsync(IHaContext ha, CancellationToken cancel);

    protected async Task<bool> ReadBusyAsync(CancellationToken cancel)
    {
        var res = await busyReadCmd.Read2Async(cancel).ConfigureAwait(false);
        var val = res.ValueResults.FirstOrDefault();
        return val.Succeeded && val.Value is bool b && b;
    }

    protected Task ClearBusyAsync(CancellationToken cancel) => busyWriteCmd.WriteAsync(new object[] { false }, cancel);

    /// <summary>Returns a severity prefix + title string suitable for the HA notification title field.</summary>
    protected static string FormatTitle(E_Mfr_NotifySeverity severity, string? title)
    {
        var prefix = severity switch
        {
            E_Mfr_NotifySeverity.Warning => "⚠ ",
            E_Mfr_NotifySeverity.Error   => "❌ ",
            _                            => ""
        };
        var trimmed = title?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(trimmed) ? prefix.TrimEnd() : $"{prefix}{trimmed}";
    }

    /// <summary>Creates a SumSymbolRead for the supplied symbols using the current ADS connection.</summary>
    protected SumSymbolRead CreateSumRead(params ISymbol[] symbols)
        => new SumSymbolRead(connection, symbols.ToList(), SumAccessMode.IndexGroupIndexOffset, SumFallbackMode.All);

    /// <summary>Normalizes an entity path to a valid persistent_notification ID (lowercase, underscores only).</summary>
    private static string NormalizeId(string path) => path
        .ToLowerInvariant()
        .Replace('.', '_')
        .Replace('/', '_')
        .Replace('-', '_');

    protected readonly IAdsConnection connection;
    private readonly ISymbol bBusySymbol;
    private readonly SumSymbolRead busyReadCmd;
    private readonly SumSymbolWrite busyWriteCmd;
}


/// <summary>
/// Event binding for <c>FB_Mfr_Notification</c> — fire-and-forget.<br/>
/// Each rising edge of <c>bSend</c> posts a new notification to the HA sidebar (no deduplication).
/// </summary>
internal sealed class NotificationBinding : EventBindingBase
{
    private readonly SumSymbolRead payloadReadCmd;
    private bool lastBusy;

    public NotificationBinding(ISymbol symbol, VirtualDevice? owner) : base(symbol, owner)
    {
        var sMessage  = symbol.SubSymbols["sMessage"];
        var sTitle    = symbol.SubSymbols["sTitle"];
        var eSeverity = symbol.SubSymbols["eSeverity"];
        this.payloadReadCmd = CreateSumRead(sMessage, sTitle, eSeverity);
    }

    public override async Task ProcessAsync(IHaContext ha, CancellationToken cancel)
    {
        var busy = await ReadBusyAsync(cancel).ConfigureAwait(false);

        if (!lastBusy && busy) // rising edge
        {
            try
            {
                var results  = (await payloadReadCmd.Read2Async(cancel).ConfigureAwait(false)).ValueResults.ToArray();
                var message  = results[0].Succeeded ? (string)results[0].Value! : string.Empty;
                var title    = results[1].Succeeded ? (string)results[1].Value! : string.Empty;
                var severity = results[2].Succeeded ? (E_Mfr_NotifySeverity)Convert.ToUInt32(results[2].Value!) : E_Mfr_NotifySeverity.Info;

                LogEvent.Gw.LogInformation("Firing notification '{0}'.", EntityId);

                ha.CallService("persistent_notification", "create", null, new
                {
                    message,
                    title = FormatTitle(severity, title)
                    // No notification_id → each trigger creates a separate sidebar entry.
                });
            }
            catch (Exception ex)
            {
                LogEvent.Gw.LogError(ex, "Failed to fire notification '{0}'.", EntityId);
            }
            finally
            {
                await ClearBusyAsync(cancel).ConfigureAwait(false);
            }
        }

        lastBusy = busy;
    }
}


/// <summary>
/// Event binding for <c>FB_Mfr_Event</c> — persistent, deduplicating.<br/>
/// <c>bActive TRUE</c> creates/refreshes a notification keyed by entity path; <c>bActive FALSE</c> dismisses it.<br/>
/// <c>bAckd</c> on the PLC FB is set when the user manually dismisses the notification in the HA sidebar.<br/>
/// <br/>
/// Dismiss detection uses polling via <c>persistent_notification/get</c> WebSocket command because HA's
/// persistent_notification integration communicates through an internal dispatcher signal — no
/// <c>state_changed</c> bus event is ever fired when the user clicks Dismiss.
/// </summary>
internal sealed class EventBinding : EventBindingBase
{
    private readonly ISymbol bActiveSymbol;
    private readonly ISymbol bAckdSymbol;
    private readonly SumSymbolRead payloadReadCmd;
    private readonly SumSymbolWrite ackdWriteCmd;
    private bool lastBusy;
    private volatile bool pendingAck;

    private IHomeAssistantRunner? runner;
    private bool notificationShown;
    private DateTime lastDismissCheck = DateTime.MinValue;
    private static readonly TimeSpan DismissCheckInterval = TimeSpan.FromSeconds(3);

    public EventBinding(ISymbol symbol, VirtualDevice? owner) : base(symbol, owner)
    {
        this.bActiveSymbol = symbol.SubSymbols["bActive"];
        this.bAckdSymbol   = symbol.SubSymbols["bAckd"];
        var sMessage       = symbol.SubSymbols["sMessage"];
        var sTitle         = symbol.SubSymbols["sTitle"];
        var eSeverity      = symbol.SubSymbols["eSeverity"];

        // Order: [0]=bActive, [1]=sMessage, [2]=sTitle, [3]=eSeverity
        this.payloadReadCmd = CreateSumRead(bActiveSymbol, sMessage, sTitle, eSeverity);
        this.ackdWriteCmd   = new SumSymbolWrite(connection, new[] { bAckdSymbol }.ToList());
    }

    public override async Task InitAsync(IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel)
    {
        this.runner = runner;

        // Startup sync: re-align HA sidebar with current PLC state (handles gateway restarts).
        try
        {
            var results  = (await payloadReadCmd.Read2Async(cancel).ConfigureAwait(false)).ValueResults.ToArray();
            var active   = results[0].Succeeded && results[0].Value is bool b && b;
            var message  = results[1].Succeeded ? (string)results[1].Value! : string.Empty;
            var title    = results[2].Succeeded ? (string)results[2].Value! : string.Empty;
            var severity = results[3].Succeeded ? (E_Mfr_NotifySeverity)Convert.ToUInt32(results[3].Value!) : E_Mfr_NotifySeverity.Info;

            notificationShown = await IsNotificationActiveAsync(cancel).ConfigureAwait(false);

            if (active && !notificationShown)
            {
                ha.CallService("persistent_notification", "create", null, new
                {
                    message,
                    title = FormatTitle(severity, title),
                    notification_id = EntityId
                });
                notificationShown = true;
                LogEvent.Gw.LogInformation("Restored notification '{0}' on startup.", EntityId);
            }
            else if (!active && notificationShown)
            {
                ha.CallService("persistent_notification", "dismiss", null, new { notification_id = EntityId });
                notificationShown = false;
                LogEvent.Gw.LogInformation("Dismissed stale notification '{0}' on startup.", EntityId);
            }
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogWarning(ex, "Failed to sync notification '{0}' on startup.", EntityId);
        }
    }

    public override async Task ProcessAsync(IHaContext ha, CancellationToken cancel)
    {
        // Poll HA every DismissCheckInterval while we believe the notification is shown.
        if (notificationShown && DateTime.UtcNow - lastDismissCheck >= DismissCheckInterval)
        {
            lastDismissCheck = DateTime.UtcNow;
            try
            {
                var stillActive = await IsNotificationActiveAsync(cancel).ConfigureAwait(false);
                if (!stillActive)
                {
                    LogEvent.Hass.LogInformation("Persistent notification '{0}' dismissed by user.", EntityId);
                    notificationShown = false;
                    pendingAck = true;
                }
                else
                {
                    LogEvent.Hass.LogDebug("Persistent notification '{0}' still active in HA.", EntityId);
                }
            }
            catch (Exception ex)
            {
                LogEvent.Gw.LogDebug(ex, "Failed to poll dismiss state for '{0}' — will retry.", EntityId);
            }
        }

        // Forward user-dismiss ACK to PLC (bAckd := TRUE):
        if (pendingAck)
        {
            pendingAck = false;
            LogEvent.Gw.LogDebug("Sending dismiss ACK to PLC for event '{0}'.", EntityId);
            await ackdWriteCmd.WriteAsync(new object[] { true }, cancel).ConfigureAwait(false);
        }

        var busy = await ReadBusyAsync(cancel).ConfigureAwait(false);

        if (!lastBusy && busy) // rising edge
        {
            try
            {
                var results  = (await payloadReadCmd.Read2Async(cancel).ConfigureAwait(false)).ValueResults.ToArray();
                var active   = results[0].Succeeded && results[0].Value is bool ab && ab;
                var message  = results[1].Succeeded ? (string)results[1].Value! : string.Empty;
                var title    = results[2].Succeeded ? (string)results[2].Value! : string.Empty;
                var severity = results[3].Succeeded ? (E_Mfr_NotifySeverity)Convert.ToUInt32(results[3].Value!) : E_Mfr_NotifySeverity.Info;

                LogEvent.Gw.LogTrace("Processing event '{0}' (active={1}).", EntityId, active);

                if (active)
                {
                    ha.CallService("persistent_notification", "create", null, new
                    {
                        message,
                        title = FormatTitle(severity, title),
                        notification_id = EntityId
                    });
                    notificationShown = true;
                }
                else
                {
                    ha.CallService("persistent_notification", "dismiss", null, new { notification_id = EntityId });
                    notificationShown = false;
                }
            }
            catch (Exception ex)
            {
                LogEvent.Gw.LogError(ex, "Failed to process event '{0}'.", EntityId);
            }
            finally
            {
                await ClearBusyAsync(cancel).ConfigureAwait(false);
            }
        }

        lastBusy = busy;
    }

    /// <summary>
    /// Queries HA via WebSocket <c>persistent_notification/get</c> to check whether our notification
    /// is still listed. This is the only reliable mechanism because HA's persistent_notification
    /// integration never fires a bus event when the user clicks Dismiss in the sidebar.
    /// </summary>
    private async Task<bool> IsNotificationActiveAsync(CancellationToken cancel)
    {
        var conn = runner?.CurrentConnection;
        if (conn is null)
            return false;

        var list = await conn
            .SendCommandAndReturnResponseAsync<GetNotificationsCommand, List<HassNotificationItem>>(
                new GetNotificationsCommand(), cancel)
            .ConfigureAwait(false);

        return list?.Any(n => string.Equals(n.NotificationId, EntityId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    // CommandMessage is a record (Type is an init property on HassMessageBase).
    // Set Type in the constructor body — do NOT pass it as a primary-constructor argument.
    private sealed record GetNotificationsCommand : CommandMessage
    {
        public GetNotificationsCommand() { Type = "persistent_notification/get"; }
    }

    private sealed class HassNotificationItem
    {
        [JsonPropertyName("notification_id")]
        public string? NotificationId { get; init; }
    }
}
