using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace LightlessAutoPair;

internal sealed class LightlessBridge : IDisposable
{
    private const string LightlessInternalName = "LightlessSync";
    private const string PlayerServiceTypeName = "LightlessSync.Services.LightFinder.LightFinderPlayerService";
    private const string ApiControllerTypeName = "LightlessSync.WebAPI.ApiController";
    private const string PairRequestServiceTypeName = "LightlessSync.Services.PairRequestService";
    private const string MediatorTypeName = "LightlessSync.Services.Mediator.LightlessMediator";
    private const string NotificationMessageTypeName = "LightlessSync.Services.Mediator.LightlessNotificationMessage";
    private const string SubscriberInterfaceTypeName = "LightlessSync.Services.Mediator.IMediatorSubscriber";

    private readonly Action<LightlessNotificationEvent> notificationCallback;

    private object? exposedPlugin;
    private object? pluginInstance;
    private IServiceProvider? serviceProvider;
    private Assembly? lightlessAssembly;
    private object? playerService;
    private object? apiController;
    private object? pairRequestService;
    private object? mediator;
    private object? subscriberProxy;
    private Delegate? notificationHandler;
    private MethodInfo? unsubscribeMethod;
    private DateTime nextRefreshUtc = DateTime.MinValue;

    public LightlessBridge(Action<LightlessNotificationEvent> notificationCallback)
    {
        this.notificationCallback = notificationCallback;
    }

    public bool IsLoaded { get; private set; }
    public bool IsConnected { get; private set; }
    public string CompatibilityStatus { get; private set; } = "Lightless has not been detected yet.";

    public bool Refresh(bool force = false)
    {
        if (!force && DateTime.UtcNow < nextRefreshUtc)
        {
            RefreshConnectionState();
            return IsLoaded;
        }

        nextRefreshUtc = DateTime.UtcNow.AddSeconds(2);

        try
        {
            var currentExposed = FindInstalledLightlessPlugin();
            if (currentExposed is null)
            {
                ResetBridge("Lightless Sync is not loaded.");
                return false;
            }

            if (!ReferenceEquals(currentExposed, exposedPlugin) || serviceProvider is null)
            {
                UnsubscribeNotifications();
                exposedPlugin = currentExposed;
                pluginInstance = FindPluginInstance(currentExposed);
                if (pluginInstance is null)
                {
                    ResetRuntimeServices("Lightless was found, but its plugin instance is not exposed by this Dalamud build.");
                    return false;
                }

                lightlessAssembly = pluginInstance.GetType().Assembly;
                serviceProvider = FindServiceProvider(pluginInstance);
                if (serviceProvider is null)
                {
                    ResetRuntimeServices("Lightless was found, but its internal service provider could not be resolved.");
                    return false;
                }

                playerService = ResolveService(PlayerServiceTypeName);
                apiController = ResolveService(ApiControllerTypeName);
                pairRequestService = ResolveService(PairRequestServiceTypeName);
                mediator = ResolveService(MediatorTypeName);

                if (playerService is null || apiController is null)
                {
                    ResetRuntimeServices("Lightless was found, but required Lightfinder services could not be resolved.");
                    return false;
                }

                TrySubscribeNotifications();
            }

            IsLoaded = playerService is not null && apiController is not null;
            RefreshConnectionState();
            CompatibilityStatus = IsLoaded
                ? "Connected to Lightless internal services."
                : "Required Lightless services are unavailable.";
            return IsLoaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not refresh the Lightless bridge.");
            ResetRuntimeServices($"Lightless bridge error: {ex.GetBaseException().Message}");
            return false;
        }
    }

    public IReadOnlyList<NearbyPlayer> GetNearbyPlayers()
    {
        if (!Refresh() || playerService is null)
            return Array.Empty<NearbyPlayer>();

        try
        {
            var rawPlayers = ReflectionUtil.InvokeNoArgs(playerService, "GetNearbyPlayers");
            var incomingRequestHashes = GetIncomingRequestHashes();
            var result = new List<NearbyPlayer>();

            foreach (var raw in ReflectionUtil.Enumerate(rawPlayers))
            {
                var hashedCid = ReflectionUtil.ReadString(raw, "HashedCid");
                if (string.IsNullOrWhiteSpace(hashedCid))
                    continue;

                var pair = ReflectionUtil.ReadMember(raw, "Pair");
                var isPaired = pair is not null &&
                    (ReflectionUtil.ReadBool(pair, "IsPaired", "IsDirectlyPaired") ?? true);

                var pairStatus = ReflectionUtil.ReadString(pair, "IndividualPairStatus", "Status");
                var statusLooksPending = pairStatus.Contains("pending", StringComparison.OrdinalIgnoreCase) ||
                                         pairStatus.Contains("request", StringComparison.OrdinalIgnoreCase);

                var displayName = ReflectionUtil.ReadString(raw, "DisplayName");
                var name = ReflectionUtil.ReadString(raw, "Name");
                var world = ReflectionUtil.ReadString(raw, "World");

                result.Add(new NearbyPlayer
                {
                    Raw = raw,
                    HashedCid = hashedCid,
                    Name = name,
                    World = world,
                    DisplayName = displayName,
                    IsPaired = isPaired,
                    HasLightlessPendingRequest = incomingRequestHashes.Contains(hashedCid) || statusLooksPending,
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read nearby Lightfinder players.");
            CompatibilityStatus = $"Could not read nearby Lightfinder players: {ex.GetBaseException().Message}";
            return Array.Empty<NearbyPlayer>();
        }
    }

    public async Task SendPairRequestAsync(NearbyPlayer player)
    {
        if (!Refresh() || playerService is null)
            throw new InvalidOperationException("Lightless is not available.");
        if (!IsConnected)
            throw new InvalidOperationException("Lightless is disconnected.");

        var method = playerService.GetType().GetMethods(ReflectionUtil.AllInstance)
            .FirstOrDefault(candidate =>
                candidate.Name == "SendPairRequestAsync" &&
                candidate.GetParameters().Length == 1);

        if (method is null)
            throw new MissingMethodException(PlayerServiceTypeName, "SendPairRequestAsync");

        var invocation = method.Invoke(playerService, new[] { player.Raw });
        if (invocation is Task task)
            await task.ConfigureAwait(false);
    }

    private object? FindInstalledLightlessPlugin()
    {
        var installed = ReflectionUtil.ReadMember(Plugin.PluginInterface, "InstalledPlugins");
        foreach (var candidate in ReflectionUtil.Enumerate(installed))
        {
            var internalName = ReflectionUtil.ReadString(candidate, "InternalName");
            var isLoaded = ReflectionUtil.ReadBool(candidate, "IsLoaded") ?? true;
            if (isLoaded && internalName.Equals(LightlessInternalName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static object? FindPluginInstance(object exposed)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return FindPluginInstanceRecursive(exposed, 0, visited);
    }

    private static object? FindPluginInstanceRecursive(
        object? value,
        int depth,
        HashSet<object> visited)
    {
        if (value is null || depth > 4)
            return null;
        if (IsLightlessObject(value))
            return value;

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is Delegate || value is Task)
            return null;
        if (!type.IsValueType && !visited.Add(value))
            return null;

        var knownNames = new[]
        {
            "PublicInstance",
            "Instance",
            "PluginInstance",
            "DalamudPlugin",
            "Plugin",
            "instance",
            "plugin",
            "localPlugin",
            "_plugin",
            "_instance",
        };

        foreach (var name in knownNames)
        {
            var child = ReflectionUtil.ReadMember(value, name);
            var found = FindPluginInstanceRecursive(child, depth + 1, visited);
            if (found is not null)
                return found;
        }

        // Dalamud's public IExposedPlugin intentionally hides the plugin instance. Its concrete
        // wrapper has changed between API versions, so inspect only plugin/instance-shaped members.
        foreach (var property in type.GetProperties(ReflectionUtil.AllInstance))
        {
            if (property.GetIndexParameters().Length != 0 ||
                (!property.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase) &&
                 !property.Name.Contains("instance", StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var found = FindPluginInstanceRecursive(property.GetValue(value), depth + 1, visited);
                if (found is not null)
                    return found;
            }
            catch
            {
                // Ignore loader properties that are unavailable during plugin reload.
            }
        }

        foreach (var field in type.GetFields(ReflectionUtil.AllInstance))
        {
            if (!field.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase) &&
                !field.Name.Contains("instance", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var found = FindPluginInstanceRecursive(field.GetValue(value), depth + 1, visited);
                if (found is not null)
                    return found;
            }
            catch
            {
                // Ignore volatile loader fields.
            }
        }

        return null;
    }

    private static bool IsLightlessObject(object? value)
        => value?.GetType().Assembly.GetName().Name?.Equals("LightlessSync", StringComparison.OrdinalIgnoreCase) == true;

    private static IServiceProvider? FindServiceProvider(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return FindServiceProviderRecursive(root, 0, visited);
    }

    private static IServiceProvider? FindServiceProviderRecursive(
        object? value,
        int depth,
        HashSet<object> visited)
    {
        if (value is null || depth > 5)
            return null;
        if (value is IServiceProvider provider)
            return provider;
        if (!value.GetType().IsValueType && !visited.Add(value))
            return null;

        var services = ReflectionUtil.ReadMember(value, "Services", "ServiceProvider");
        if (services is IServiceProvider direct)
            return direct;

        var knownChildren = new[]
        {
            "_host",
            "_lightlessPlugin",
            "_runtimeServiceScope",
            "_serviceScopeFactory",
            "Host",
            "Plugin",
        };

        foreach (var name in knownChildren)
        {
            var child = ReflectionUtil.ReadMember(value, name);
            var found = FindServiceProviderRecursive(child, depth + 1, visited);
            if (found is not null)
                return found;
        }

        return null;
    }

    private object? ResolveService(string fullTypeName)
    {
        var type = lightlessAssembly?.GetType(fullTypeName, false, false);
        return type is null ? null : serviceProvider?.GetService(type);
    }

    private HashSet<string> GetIncomingRequestHashes()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pairRequestService is null)
            return result;

        try
        {
            var requests = ReflectionUtil.InvokeNoArgs(pairRequestService, "GetActiveRequests");
            foreach (var request in ReflectionUtil.Enumerate(requests))
            {
                var hash = ReflectionUtil.ReadString(request, "HashedCid");
                if (!string.IsNullOrWhiteSpace(hash))
                    result.Add(hash);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not inspect Lightless incoming pair requests.");
        }

        return result;
    }

    private void RefreshConnectionState()
    {
        IsConnected = ReflectionUtil.ReadBool(apiController, "IsConnected") ?? false;
    }

    private void TrySubscribeNotifications()
    {
        if (mediator is null || lightlessAssembly is null || subscriberProxy is not null)
            return;

        try
        {
            var subscriberType = lightlessAssembly.GetType(SubscriberInterfaceTypeName, true)!;
            var messageType = lightlessAssembly.GetType(NotificationMessageTypeName, true)!;

            subscriberProxy = CreateDispatchProxy(subscriberType);
            if (subscriberProxy is MediatorSubscriberProxy proxy)
                proxy.MediatorInstance = mediator;

            var callbackMethod = GetType().GetMethod(
                nameof(OnNotificationMessage),
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var parameter = Expression.Parameter(messageType, "message");
            var callback = Expression.Call(
                Expression.Constant(this),
                callbackMethod,
                Expression.Convert(parameter, typeof(object)));
            var actionType = typeof(Action<>).MakeGenericType(messageType);
            notificationHandler = Expression.Lambda(actionType, callback, parameter).Compile();

            var subscribe = mediator.GetType().GetMethods(ReflectionUtil.AllInstance)
                .First(method =>
                    method.Name == "Subscribe" &&
                    method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 2);
            subscribe.MakeGenericMethod(messageType)
                .Invoke(mediator, new object?[] { subscriberProxy, notificationHandler });

            unsubscribeMethod = mediator.GetType().GetMethods(ReflectionUtil.AllInstance)
                .FirstOrDefault(method =>
                    method.Name == "Unsubscribe" &&
                    method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 1)
                ?.MakeGenericMethod(messageType);

            Plugin.Log.Information("Subscribed to Lightless notifications for pair-request outcomes.");
        }
        catch (Exception ex)
        {
            subscriberProxy = null;
            notificationHandler = null;
            unsubscribeMethod = null;
            Plugin.Log.Warning(ex, "Could not subscribe to Lightless notifications. Declines may need manual blacklist management.");
        }
    }

    private static object CreateDispatchProxy(Type interfaceType)
    {
        var genericCreate = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(method =>
                method.Name == nameof(DispatchProxy.Create) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 2 &&
                method.GetParameters().Length == 0);

        return genericCreate
            .MakeGenericMethod(interfaceType, typeof(MediatorSubscriberProxy))
            .Invoke(null, null)!;
    }

    private void OnNotificationMessage(object message)
    {
        try
        {
            var notification = ReflectionUtil.ReadMember(message, "Notification");
            if (notification is null)
                return;

            var title = ReflectionUtil.ReadString(notification, "Title");
            var body = ReflectionUtil.ReadString(notification, "Message");
            var profile = ReflectionUtil.CollectText(
                ReflectionUtil.ReadMember(notification, "ProfileUser"),
                2);

            notificationCallback(new LightlessNotificationEvent(title, body, profile));
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not inspect a Lightless notification.");
        }
    }

    private void UnsubscribeNotifications()
    {
        try
        {
            if (mediator is not null && subscriberProxy is not null && unsubscribeMethod is not null)
                unsubscribeMethod.Invoke(mediator, new[] { subscriberProxy });
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not unsubscribe from Lightless notifications.");
        }

        subscriberProxy = null;
        notificationHandler = null;
        unsubscribeMethod = null;
    }

    private void ResetBridge(string status)
    {
        exposedPlugin = null;
        pluginInstance = null;
        lightlessAssembly = null;
        ResetRuntimeServices(status);
    }

    private void ResetRuntimeServices(string status)
    {
        UnsubscribeNotifications();
        serviceProvider = null;
        playerService = null;
        apiController = null;
        pairRequestService = null;
        mediator = null;
        IsLoaded = false;
        IsConnected = false;
        CompatibilityStatus = status;
    }

    public void Dispose()
    {
        UnsubscribeNotifications();
        ResetBridge("Disposed.");
    }
}
