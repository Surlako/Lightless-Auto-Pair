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
    private const string LifecycleTypeName = "LightlessSync.PluginLifecycle";
    private const string RuntimePluginTypeName = "LightlessSync.LightlessPlugin";
    private const string PlayerServiceTypeName = "LightlessSync.Services.LightFinder.LightFinderPlayerService";
    private const string SyncshellServiceTypeName = "LightlessSync.Services.LightFinder.LightFinderSyncshellService";
    private const string ApiControllerTypeName = "LightlessSync.WebAPI.ApiController";
    private const string PairRequestServiceTypeName = "LightlessSync.Services.PairRequestService";
    private const string MediatorTypeName = "LightlessSync.Services.Mediator.LightlessMediator";
    private const string NotificationMessageTypeName = "LightlessSync.Services.Mediator.LightlessNotificationMessage";
    private const string SubscriberInterfaceTypeName = "LightlessSync.Services.Mediator.IMediatorSubscriber";

    private static readonly string[] WrapperMemberNames =
    {
        "PublicInstance",
        "Instance",
        "PluginInstance",
        "DalamudPlugin",
        "LocalPlugin",
        "Plugin",
        "instance",
        "pluginInstance",
        "localPlugin",
        "plugin",
        "_localPlugin",
        "_plugin",
        "_instance",
    };

    private readonly Action<LightlessNotificationEvent> notificationCallback;

    private object? exposedPlugin;
    private object? lifecycleInstance;
    private object? runtimePluginInstance;
    private object? serviceProvider;
    private Assembly? lightlessAssembly;
    private object? playerService;
    private object? syncshellService;
    private object? apiController;
    private object? pairRequestService;
    private object? mediator;
    private object? subscriberProxy;
    private Delegate? notificationHandler;
    private MethodInfo? unsubscribeMethod;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private string providerSource = string.Empty;

    public LightlessBridge(Action<LightlessNotificationEvent> notificationCallback)
    {
        this.notificationCallback = notificationCallback;
    }

    public bool IsDetected { get; private set; }
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

            IsDetected = true;

            if (!ReferenceEquals(currentExposed, exposedPlugin) || playerService is null || apiController is null)
            {
                UnsubscribeNotifications();
                exposedPlugin = currentExposed;

                var roots = FindRuntimeRoots(currentExposed);
                lifecycleInstance = roots.Lifecycle;
                runtimePluginInstance = roots.RuntimePlugin;
                lightlessAssembly = roots.Assembly ?? FindLoadedLightlessAssembly();

                if (lifecycleInstance is null && runtimePluginInstance is null)
                {
                    ResetRuntimeServices(
                        "Lightless was detected, but its PluginLifecycle/LightlessPlugin runtime object could not be found.");
                    return false;
                }

                serviceProvider = FindKnownServiceProvider(lifecycleInstance, runtimePluginInstance, out providerSource)
                                  ?? FindServiceProviderFromGraph(
                                      new[] { lifecycleInstance, runtimePluginInstance },
                                      out providerSource);

                // ApiController is also held directly by LightlessPlugin, so resolve it before
                // requiring the DI provider. This gives a reliable connection-state fallback.
                apiController = ReflectionUtil.ReadMember(runtimePluginInstance, "_apiController", "ApiController")
                                ?? ResolveService(ApiControllerTypeName)
                                ?? FindReachableObjectByTypeName(ApiControllerTypeName);

                playerService = ResolveService(PlayerServiceTypeName)
                                ?? FindReachableObjectByTypeName(PlayerServiceTypeName);
                syncshellService = ResolveService(SyncshellServiceTypeName)
                                   ?? FindReachableObjectByTypeName(SyncshellServiceTypeName);
                pairRequestService = ResolveService(PairRequestServiceTypeName)
                                     ?? FindReachableObjectByTypeName(PairRequestServiceTypeName);
                mediator = ResolveService(MediatorTypeName)
                           ?? FindReachableObjectByTypeName(MediatorTypeName);

                if (playerService is null || apiController is null)
                {
                    var rootsText = DescribeRoots();
                    var providerText = serviceProvider is null
                        ? "No compatible service provider was found."
                        : $"Provider found via {providerSource}, but required services were unavailable.";
                    ResetRuntimeServices(
                        $"Lightless runtime found ({rootsText}). {providerText}");
                    return false;
                }

                TrySubscribeNotifications();
            }

            IsLoaded = playerService is not null && apiController is not null;
            RefreshConnectionState();
            CompatibilityStatus = IsLoaded
                ? string.IsNullOrWhiteSpace(providerSource)
                    ? "Connected to Lightless internal services."
                    : $"Connected to Lightless internal services via {providerSource}."
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

    public IReadOnlyList<NearbyPlayer> GetNearbyPlayers(bool refresh = true)
    {
        if ((refresh && !Refresh()) || !IsLoaded || playerService is null)
            return Array.Empty<NearbyPlayer>();

        try
        {
            var rawPlayers = ReflectionUtil.InvokeNoArgs(playerService, "GetNearbyPlayers");
            var incomingRequestHashes = GetIncomingRequestHashes();
            var joinedSyncshellBroadcasters = GetJoinedSyncshellBroadcasterKeys();
            var result = new List<NearbyPlayer>();

            foreach (var raw in ReflectionUtil.Enumerate(rawPlayers))
            {
                var hashedCid = ReflectionUtil.ReadString(raw, "HashedCid");
                if (string.IsNullOrWhiteSpace(hashedCid))
                    continue;

                var pair = ReflectionUtil.ReadMember(raw, "Pair");
                var isDirectlyPaired = ReflectionUtil.ReadBool(pair, "IsDirectlyPaired") ?? false;
                var isPaired = pair is not null &&
                    (ReflectionUtil.ReadBool(pair, "IsPaired") ?? isDirectlyPaired);

                // Lightless treats syncshell membership as a persistent connection even when
                // the users are not directly paired. Detect that connection explicitly, and
                // also detect broadcasters whose nearby syncshell card says AlreadyJoined.
                var hasPersistentConnection =
                    ReflectionUtil.ReadBool(pair, "HasPersistentConnection") ?? false;
                var hasGroupMembership = HasGroupMembership(pair);
                var connectedThroughSharedSyncshell =
                    pair is not null &&
                    !isDirectlyPaired &&
                    (isPaired || hasPersistentConnection || hasGroupMembership);

                var pairStatus = ReflectionUtil.ReadString(pair, "IndividualPairStatus", "Status");
                var statusLooksPending = pairStatus.Contains("pending", StringComparison.OrdinalIgnoreCase) ||
                                         pairStatus.Contains("request", StringComparison.OrdinalIgnoreCase);

                var displayName = ReflectionUtil.ReadString(raw, "DisplayName");
                var name = ReflectionUtil.ReadString(raw, "Name");
                var world = ReflectionUtil.ReadString(raw, "World");
                var broadcastsJoinedSyncshell = MatchesPlayerIdentity(
                    joinedSyncshellBroadcasters,
                    displayName,
                    name,
                    world);

                result.Add(new NearbyPlayer
                {
                    Raw = raw,
                    HashedCid = hashedCid,
                    Name = name,
                    World = world,
                    DisplayName = displayName,
                    IsPaired = isPaired,
                    IsCoveredByJoinedSyncshell =
                        connectedThroughSharedSyncshell || broadcastsJoinedSyncshell,
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
        foreach (var candidate in Plugin.PluginInterface.InstalledPlugins)
        {
            if (candidate.IsLoaded &&
                candidate.InternalName.Equals(LightlessInternalName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static RuntimeRoots FindRuntimeRoots(object exposed)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((exposed, 0));

        object? lifecycle = null;
        object? runtimePlugin = null;
        Assembly? assembly = null;
        var inspected = 0;

        while (queue.Count > 0 && inspected < 2500)
        {
            var (value, depth) = queue.Dequeue();
            if (!ShouldInspect(value) || !visited.Add(value))
                continue;

            inspected++;
            var type = value.GetType();
            var fullName = type.FullName ?? string.Empty;
            if (IsLightlessAssembly(type.Assembly))
            {
                assembly ??= type.Assembly;
                if (fullName.Equals(LifecycleTypeName, StringComparison.Ordinal))
                    lifecycle = value;
                else if (fullName.Equals(RuntimePluginTypeName, StringComparison.Ordinal))
                    runtimePlugin = value;
            }

            if (lifecycle is not null && runtimePlugin is null)
            {
                var directRuntime = ReflectionUtil.ReadMember(
                    lifecycle,
                    "_lightlessPlugin",
                    "LightlessPlugin");
                if (directRuntime?.GetType().FullName == RuntimePluginTypeName)
                    runtimePlugin = directRuntime;
            }

            if (lifecycle is not null && runtimePlugin is not null)
                break;
            if (depth >= 8)
                continue;

            foreach (var child in EnumerateGraphChildren(value))
            {
                if (ShouldInspect(child))
                    queue.Enqueue((child, depth + 1));
            }
        }

        return new RuntimeRoots(lifecycle, runtimePlugin, assembly);
    }

    private static object? FindKnownServiceProvider(
        object? lifecycle,
        object? runtimePlugin,
        out string source)
    {
        source = string.Empty;

        var runtimeScope = ReflectionUtil.ReadMember(
            runtimePlugin,
            "_runtimeServiceScope",
            "RuntimeServiceScope");
        var provider = ReflectionUtil.ReadMember(runtimeScope, "ServiceProvider", "Services");
        if (IsServiceProviderLike(provider))
        {
            source = "LightlessPlugin._runtimeServiceScope.ServiceProvider";
            return provider;
        }

        var host = ReflectionUtil.ReadMember(lifecycle, "_host", "Host");
        provider = ReflectionUtil.ReadMember(host, "Services", "ServiceProvider");
        if (IsServiceProviderLike(provider))
        {
            source = "PluginLifecycle._host.Services";
            return provider;
        }

        provider = ReflectionUtil.ReadMember(runtimePlugin, "Services", "ServiceProvider");
        if (IsServiceProviderLike(provider))
        {
            source = "LightlessPlugin.ServiceProvider";
            return provider;
        }

        return null;
    }

    private static object? FindServiceProviderFromGraph(
        IEnumerable<object?> roots,
        out string source)
    {
        source = string.Empty;
        var queue = new Queue<(object Value, int Depth, string Path)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var root in roots.Where(root => root is not null))
            queue.Enqueue((root!, 0, root!.GetType().Name));

        var inspected = 0;
        while (queue.Count > 0 && inspected < 2500)
        {
            var (value, depth, path) = queue.Dequeue();
            if (!ShouldInspect(value) || !visited.Add(value))
                continue;

            inspected++;
            if (IsServiceProviderLike(value))
            {
                source = path;
                return value;
            }

            var direct = ReflectionUtil.ReadMember(value, "ServiceProvider", "Services");
            if (IsServiceProviderLike(direct))
            {
                source = $"{path}.ServiceProvider";
                return direct;
            }

            if (depth >= 7)
                continue;

            foreach (var child in EnumerateGraphChildren(value))
            {
                if (!ShouldInspect(child))
                    continue;

                var childAssembly = child.GetType().Assembly.GetName().Name ?? string.Empty;
                if (!IsLightlessAssembly(child.GetType().Assembly) &&
                    !childAssembly.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) &&
                    !childAssembly.StartsWith("Dalamud", StringComparison.Ordinal))
                    continue;

                queue.Enqueue((child, depth + 1, $"{path}->{child.GetType().Name}"));
            }
        }

        return null;
    }

    private object? ResolveService(string fullTypeName)
    {
        var type = lightlessAssembly?.GetType(fullTypeName, false, false);
        if (type is null || serviceProvider is null)
            return null;

        try
        {
            if (serviceProvider is IServiceProvider provider)
                return provider.GetService(type);

            var providerInterface = serviceProvider.GetType().GetInterfaces()
                .FirstOrDefault(candidate =>
                    candidate.FullName == typeof(IServiceProvider).FullName);
            var interfaceMethod = providerInterface?.GetMethod(nameof(IServiceProvider.GetService));
            if (interfaceMethod is not null)
                return interfaceMethod.Invoke(serviceProvider, new object?[] { type });

            var directMethod = serviceProvider.GetType().GetMethods(ReflectionUtil.AllInstance)
                .FirstOrDefault(candidate =>
                    (candidate.Name == nameof(IServiceProvider.GetService) ||
                     candidate.Name.EndsWith($".{nameof(IServiceProvider.GetService)}", StringComparison.Ordinal)) &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType == typeof(Type));
            return directMethod?.Invoke(serviceProvider, new object?[] { type });
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not resolve Lightless service {ServiceType} from {ProviderSource}.",
                fullTypeName,
                providerSource);
            return null;
        }
    }

    private object? FindReachableObjectByTypeName(string fullTypeName)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        if (lifecycleInstance is not null)
            queue.Enqueue((lifecycleInstance, 0));
        if (runtimePluginInstance is not null)
            queue.Enqueue((runtimePluginInstance, 0));

        var inspected = 0;
        while (queue.Count > 0 && inspected < 3500)
        {
            var (value, depth) = queue.Dequeue();
            if (!ShouldInspect(value) || !visited.Add(value))
                continue;

            inspected++;
            if (value.GetType().FullName == fullTypeName)
                return value;
            if (depth >= 8)
                continue;

            foreach (var child in EnumerateGraphChildren(value))
            {
                if (!ShouldInspect(child))
                    continue;

                var assemblyName = child.GetType().Assembly.GetName().Name ?? string.Empty;
                if (IsLightlessAssembly(child.GetType().Assembly) ||
                    assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Dalamud", StringComparison.Ordinal))
                    queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateGraphChildren(object value)
    {
        var yielded = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var name in WrapperMemberNames)
        {
            var child = ReflectionUtil.ReadMember(value, name);
            if (child is not null && yielded.Add(child))
                yield return child;
        }

        for (var current = value.GetType(); current is not null; current = current.BaseType)
        {
            FieldInfo[] fields;
            try
            {
                fields = current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (var field in fields)
            {
                object? child;
                try
                {
                    child = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (child is not null && yielded.Add(child))
                    yield return child;
            }
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var count = 0;
            IEnumerator? enumerator = null;
            try
            {
                enumerator = enumerable.GetEnumerator();
                while (count++ < 100 && enumerator.MoveNext())
                {
                    var child = enumerator.Current;
                    if (child is not null && yielded.Add(child))
                        yield return child;
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
    }

    private static bool ShouldInspect(object? value)
    {
        if (value is null || value is string || value is Delegate || value is Task ||
            value is Type || value is Assembly || value is MemberInfo)
            return false;

        var type = value.GetType();
        return !type.IsPrimitive && !type.IsEnum && !type.IsPointer;
    }

    private static bool IsServiceProviderLike(object? value)
    {
        if (value is null)
            return false;
        if (value is IServiceProvider)
            return true;

        var type = value.GetType();
        if (type.GetInterfaces().Any(candidate =>
                candidate.FullName == typeof(IServiceProvider).FullName))
            return true;

        return type.GetMethods(ReflectionUtil.AllInstance).Any(candidate =>
            (candidate.Name == nameof(IServiceProvider.GetService) ||
             candidate.Name.EndsWith($".{nameof(IServiceProvider.GetService)}", StringComparison.Ordinal)) &&
            candidate.GetParameters().Length == 1 &&
            candidate.GetParameters()[0].ParameterType == typeof(Type));
    }

    private static bool IsLightlessAssembly(Assembly assembly)
        => assembly.GetName().Name?.Equals("LightlessSync", StringComparison.OrdinalIgnoreCase) == true;

    private static Assembly? FindLoadedLightlessAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(IsLightlessAssembly);

    private string DescribeRoots()
    {
        var roots = new List<string>();
        if (lifecycleInstance is not null)
            roots.Add(lifecycleInstance.GetType().FullName ?? lifecycleInstance.GetType().Name);
        if (runtimePluginInstance is not null)
            roots.Add(runtimePluginInstance.GetType().FullName ?? runtimePluginInstance.GetType().Name);
        return roots.Count == 0 ? "none" : string.Join(", ", roots);
    }

    private HashSet<string> GetJoinedSyncshellBroadcasterKeys()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (syncshellService is null)
            return result;

        try
        {
            var nearbySyncshells = ReflectionUtil.InvokeNoArgs(
                syncshellService,
                "GetNearbySyncshells");

            foreach (var raw in ReflectionUtil.Enumerate(nearbySyncshells))
            {
                if (ReflectionUtil.ReadBool(raw, "AlreadyJoined") != true)
                    continue;

                AddIdentityKey(
                    result,
                    ReflectionUtil.ReadString(raw, "BroadcasterName"));
            }
        }
        catch (Exception ex)
        {
            // Failure here must never stop ordinary player pairing. The Pair object fallback
            // still protects users who are already connected through a shared syncshell.
            Plugin.Log.Verbose(ex, "Could not inspect already-joined nearby syncshells.");
        }

        return result;
    }

    private static bool HasGroupMembership(object? pair)
    {
        if (pair is null)
            return false;

        var userPair = ReflectionUtil.ReadMember(pair, "UserPair");
        return HasAnyItems(ReflectionUtil.ReadMember(
                   userPair,
                   "Groups",
                   "GroupPairs",
                   "GroupPairPermissions",
                   "GroupPermissions")) ||
               HasAnyItems(ReflectionUtil.ReadMember(
                   pair,
                   "Groups",
                   "GroupPairs"));
    }

    private static bool HasAnyItems(object? value)
    {
        if (value is null || value is string || value is not IEnumerable enumerable)
            return false;

        IEnumerator? enumerator = null;
        try
        {
            enumerator = enumerable.GetEnumerator();
            return enumerator.MoveNext();
        }
        catch
        {
            return false;
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static bool MatchesPlayerIdentity(
        HashSet<string> joinedSyncshellBroadcasters,
        string displayName,
        string name,
        string world)
    {
        if (joinedSyncshellBroadcasters.Count == 0)
            return false;

        var candidates = new[]
        {
            displayName,
            string.IsNullOrWhiteSpace(world) ? name : $"{name} @ {world}",
            string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}",
            name,
        };

        return candidates
            .Select(NormalizeIdentityKey)
            .Any(candidate =>
                candidate.Length > 0 &&
                joinedSyncshellBroadcasters.Contains(candidate));
    }

    private static void AddIdentityKey(HashSet<string> target, string value)
    {
        var normalized = NormalizeIdentityKey(value);
        if (normalized.Length > 0)
            target.Add(normalized);
    }

    private static string NormalizeIdentityKey(string value)
        => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

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
            Plugin.Log.Warning(ex,
                "Could not subscribe to Lightless notifications. Declines may need manual blacklist management.");
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
        lifecycleInstance = null;
        runtimePluginInstance = null;
        lightlessAssembly = null;
        IsDetected = false;
        ResetRuntimeServices(status);
    }

    private void ResetRuntimeServices(string status)
    {
        UnsubscribeNotifications();
        serviceProvider = null;
        providerSource = string.Empty;
        playerService = null;
        syncshellService = null;
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

    private sealed record RuntimeRoots(
        object? Lifecycle,
        object? RuntimePlugin,
        Assembly? Assembly);
}
