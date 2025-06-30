using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Utilities.Core;

namespace HassModel;


public partial class PlcHaGatewayApp
{
    #region Constants
    static readonly string MappingExportFilePath = Path.Combine(UtilAssembly.GetLocationFolder(), "exp.mapping.json");
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    #endregion


    #region Types
    struct MappingExport
    {
        public MappingExport(VirtualDeviceProxy[] devices, MappingProxy[] mappings)
        {
            this.Devices = devices;
            this.Mappings = mappings;
        }



        #region Properties.Management
        public string Hash => hash;
        string hash = "";
        public VirtualDeviceProxy[] Devices { init; get; }
        public MappingProxy[] Mappings { init; get; }
        #endregion


        public void SetHash(string hash)
        {
            this.hash = hash;
        }
    }
    struct VirtualDeviceProxy
    {
        public VirtualDeviceProxy(VirtualDevice device)
        {
            this.device = device;
        }


        #region Properties
        public string Identifier => device.Identifier;
        public string Name => device.Name;
        public string? Model => device.Model;
        public string? Manufacturer => device.Manufacturer;
        public string? Version => device.Version?.ToString();
        #endregion


        VirtualDevice device;
    }
    struct MappingProxy
    {
        public MappingProxy(IMapping mapping)
        {
            this.mapping = mapping;
        }


        #region Properties.Management
        public string EntityType => mapping.Info.EntityTypeName;
        public string Backend => mapping.Backend.ToString();
        public string FunctionBlockType => mapping.FunctionBlockType.GetTypeName();
        public string? Device => mapping.Owner?.ToString();
        public string EntityId => mapping.EntityId;
        #endregion
        #region Properties
        public string Name => mapping.Name;
        public string? DeviceClass => mapping.DeviceClass;
        #endregion


        IMapping mapping;
    }
    #endregion


    /// <summary>
    /// Creates an export file containing available mapping information.
    /// </summary>
    private Task CreateMappingExportAsync(CancellationToken cancellationToken)
    {
        LogEvent.Gw.LogTrace("Generate mapping export.");

        // Create serializable data:
        var devices = DeviceMappings.Select(m => new VirtualDeviceProxy(m)).ToArray();
        var mappings = Mappings.Select(m => new MappingProxy(m)).ToArray();
        var export = new MappingExport(devices, mappings);

        // Create hash:
        var expJson = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(expJson);
            export.SetHash(hash.ToHashString());
        }

        // Serialize:
        expJson = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);

        // Write export file:
        return (File.WriteAllBytesAsync(MappingExportFilePath, expJson, cancellationToken));
    }
}