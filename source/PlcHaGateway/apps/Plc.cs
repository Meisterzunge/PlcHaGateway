using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HassModel;
using Microsoft.Extensions.Configuration;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

internal class Plc : IDisposable
{
    #region Constants
    static SessionSettings AdsSettings = SessionSettings.Default;
    static ISymbolLoaderSettings SymbolLoaderSettings = new SymbolLoaderSettings(SymbolsLoadMode.Flat);
    #endregion


    public Plc(AmsAddress address)
    {
        this.session = new AdsSession(address, AdsSettings);
    }


    #region Properties.Management
    public ISymbol[] MappedSymbols { get; private set; }
    #endregion


    #region Connection
    public async Task ConnectAsync(CancellationToken cancel)
    {
        // Establish connection:
        LogEvent.Ads.LogInformation("Establish connection.");
        await session.ConnectAsync(cancel).ConfigureAwait(false);

        // Load symbols:
        var symbolLoader = SymbolLoaderFactory.Create(session.Connection, SymbolLoaderSettings);
        var res = await symbolLoader.GetSymbolsAsync(CancellationToken.None).ConfigureAwait(false);
        res.ThrowOnError();

        // Load mapped symbols:
        this.MappedSymbols = res.Symbols
            .Flatten()
            .Where(s => s.IsMapped())
            .ToArray();
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

    public static IEnumerable<ISymbol> Flatten(this ISymbol source) => source.SubSymbols
        .Flatten()
        .Prepend(source);
    public static IEnumerable<ISymbol> Flatten(this IEnumerable<ISymbol> source)
    {
        var subSymbols = source
            .Select(Flatten)
            .SelectMany(s => s);
        foreach (var sub in subSymbols)
            yield return (sub);
    }
}