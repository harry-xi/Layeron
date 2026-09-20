using System;
using System.Collections.Generic;
using Layeron.App.ViewModels;

namespace Layeron.App.Session;

/// <summary>
/// The single source of truth for the current session's core data. Dockables and,
/// in the future, plugins resolve the shared volume / mask / window-level objects
/// from here instead of receiving them through constructor arguments. This keeps
/// the dock layout serializable (layout records only identifiers, never live
/// object graphs) and gives plugins one stable entry point into the core data.
/// </summary>
public sealed class SessionContext
{
    private readonly Dictionary<object, object> _services = new();

    public SessionContext(
        SharedDataSourceViewModel sharedData,
        MaskManagerViewModel maskManager,
        WindowLevelViewModel windowLevel)
    {
        SharedData = sharedData;
        MaskManager = maskManager;
        WindowLevel = windowLevel;
    }

    /// <summary>Current main volume and crosshair position.</summary>
    public SharedDataSourceViewModel SharedData { get; }

    /// <summary>Mask overlay layers bound to the current main volume.</summary>
    public MaskManagerViewModel MaskManager { get; }

    /// <summary>Shared window/level lookup applied by every slice view.</summary>
    public WindowLevelViewModel WindowLevel { get; }

    /// <summary>Registers (or replaces) a service for plugins to consume.</summary>
    public void Provide<T>(T service) where T : class
        => _services[typeof(T)] = service;

    /// <summary>Registers a keyed service, allowing several instances of the same type.</summary>
    public void Provide<T>(string key, T service) where T : class
        => _services[new ServiceKey(typeof(T), key)] = service;

    /// <summary>Resolves a registered service, returning null when it is not present.</summary>
    public T? Inject<T>() where T : class
        => _services.TryGetValue(typeof(T), out var service) ? (T)service : null;

    /// <summary>Resolves a keyed service, returning null when it is not present.</summary>
    public T? Inject<T>(string key) where T : class
        => _services.TryGetValue(new ServiceKey(typeof(T), key), out var service) ? (T)service : null;

    /// <summary>Removes a previously registered service.</summary>
    public bool Revoke<T>() where T : class
        => _services.Remove(typeof(T));

    /// <summary>Removes a previously registered keyed service.</summary>
    public bool Revoke<T>(string key) where T : class
        => _services.Remove(new ServiceKey(typeof(T), key));

    private readonly record struct ServiceKey(Type Type, string Key);
}