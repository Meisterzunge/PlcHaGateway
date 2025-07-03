using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Utilities.Core;

namespace HassModel;


public partial class PlcHaGatewayApp
{
    #region Constants
    static readonly string ExportFolderPath = Path.Combine(UtilAssembly.GetLocationFolder(), "export");
    #endregion


    /// <summary>
    /// Creates configured export files.
    /// </summary>
    public IEnumerable<Task> CreateExportFilesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ExportFolderPath);

        if (config.GetValue<bool>("Export:Hmi:Enable"))
            yield return (CreateHmiExportAsync(cancellationToken));
        if (config.GetValue<bool>("Export:Mapping:Enable"))
            yield return (CreateMappingExportAsync(cancellationToken));

        yield break;
    }
}