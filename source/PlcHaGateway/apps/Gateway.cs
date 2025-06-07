using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetDaemon.HassModel.Entities;
using Utilities.Core;

namespace HassModel;


/// <summary>
/// Manage to connect <i>Home Assistant</i> and <i>TwinCAT PLC</i>.
/// </summary>
[NetDaemonApp]
public class PlcHaGatewayApp : IAsyncInitializable, IDisposable
{
    public PlcHaGatewayApp(IConfiguration config, ILogger<PlcHaGatewayApp> logger, IHaContext context)
    {
        new Common(logger);

        this.plc = config.CreatePlcFromSettings();
        this.ha = context;
    }


    #region Properties.Management
    //private CancellationToken ConnectionToken => connectionToken.Token;
    //private CancellationTokenSource connectionToken = new();

    public IReadOnlyCollection<IMapping> Mappings;
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

        LogEvent.Gw.LogTrace("Creating mappings...");
        try
        {
            this.Mappings = MappingFactory.CreateMappings(ha, plc);
            if (Mappings.IsEmpty())
                LogEvent.Gw.LogWarning($"No mappings found to create.");
            else
                LogEvent.Gw.LogInformation($"Created {Mappings.Count} mapping(s).");
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mappings.");
            throw;
        }

        var avalOp = (AnalogMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_number.test_number"));
        avalOp.Value = 15;
        //var aval = (AnalogMapping)Mappings.First(m => m.Entity.EntityId.Equals("sensor.test_tfl"));
        //aval.Value = 20;

        var bvalOp = (BooleanMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_boolean.test_toggle"));
        bvalOp.Value = true;

        var mvalOp = (MultistateMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_select.test_sel1"));
        mvalOp.Value = 1;
    }
    public void Dispose() => plc.Dispose();
    #endregion


    private Plc plc;
    private IHaContext ha;
}