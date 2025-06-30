using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Utilities.Core;

namespace HassModel;


public partial class PlcHaGatewayApp
{
    /// <summary>
    /// Creates configured export files.
    /// </summary>
    public IEnumerable<Task> CreateExportFilesAsync(CancellationToken cancellationToken)
    {
        if (config.GetValue<bool>("Export:Mapping"))
            yield return (CreateMappingExportAsync(cancellationToken));

        yield break;
    }
}