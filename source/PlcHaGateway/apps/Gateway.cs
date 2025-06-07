using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetDaemon.HassModel.Entities;

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
        }

        LogEvent.Gw.LogInformation("Create mappings.");
        try
        {
            // (BETA) ... create some test mappings until ADS selector is implemented:
            var entities = ha.GetAllEntities();
            this.Mappings = new IMapping[] {
                MappingFactory.CreateMapping(entities.First(e => e.EntityId.Equals("input_number.test_number")))
            };
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mappings.");
        }


        //var mapping = (PlcHaInputNumnerMapping)Mappings.First();
        //mapping.value = 12.5;
    }
    public void Dispose() => plc.Dispose();
    #endregion


    private Plc plc;

    private IHaContext ha;
}