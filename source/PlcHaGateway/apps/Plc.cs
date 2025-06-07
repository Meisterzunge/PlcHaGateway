using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;

internal class Plc : IDisposable
{
    public Plc(AmsAddress address, SessionSettings? settings = null)
    {
        this.session = new AdsSession(address, settings ?? SessionSettings.Default);
    }


    #region Connection
    public async Task ConnectAsync(CancellationToken cancel)
    {
        LogEvent.Ads.LogInformation("Establish connection.");
        await session.ConnectAsync(cancel).ConfigureAwait(false);
    }
    public void Dispose() => session.Dispose();
    #endregion


    private AdsSession session;
}


internal static partial class Ext
{
    public static Plc CreatePlcFromSettings(this IConfiguration source) => new Plc(source.GetPlcNetId());
    public static AmsAddress GetPlcNetId(this IConfiguration source) => new AmsAddress(
        source.GetValue<string>("Plc:NetId")!,
        source.GetValue<int>("Plc:Port")
    );
}