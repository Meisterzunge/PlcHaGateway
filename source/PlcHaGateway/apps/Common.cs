// Common usings for NetDaemon apps
global using System;
global using System.Reactive.Linq;
global using Microsoft.Extensions.Logging;
global using NetDaemon.AppModel;
global using NetDaemon.HassModel;
using HassModel;
using Microsoft.Extensions.Configuration;


internal class Common
{
    public Common(ILogger<PlcHaGatewayApp> logger)
    {
        Common.Logger = logger;
    }


    public static ILogger<PlcHaGatewayApp> Logger { get; private set; }
}
internal static class LogEvent
{
    internal static readonly EventId Gw = new(101, "Gateway");
    internal static readonly EventId Hass = new(100, "Home Assistant");
    internal static readonly EventId Ads = new(100, "ADS");
    internal static readonly EventId Mqtt = new(102, "MQTT");
}


internal static partial class Ext
{
    public static void LogDebug(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogDebug(source, exception, message, args);
    public static void LogDebug(this EventId source, string? message, params object?[] args) => Common.Logger.LogDebug(source, message, args);
    public static void LogTrace(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogTrace(source, exception, message, args);
    public static void LogTrace(this EventId source, string? message, params object?[] args) => Common.Logger.LogTrace(source, message, args);
    public static void LogInformation(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogInformation(source, exception, message, args);
    public static void LogInformation(this EventId source, string? message, params object?[] args) => Common.Logger.LogInformation(source, message, args);
    public static void LogWarning(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogWarning(source, exception, message, args);
    public static void LogWarning(this EventId source, string? message, params object?[] args) => Common.Logger.LogWarning(source, message, args);
    public static void LogError(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogError(source, exception, message, args);
    public static void LogError(this EventId source, string? message, params object?[] args) => Common.Logger.LogError(source, message, args);
    public static void LogCritical(this EventId source, Exception? exception, string? message, params object?[] args) => Common.Logger.LogCritical(source, exception, message, args);
    public static void LogCritical(this EventId source, string? message, params object?[] args) => Common.Logger.LogCritical(source, message, args);
}