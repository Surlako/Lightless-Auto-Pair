using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LightlessAutoPair;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/lap";
    private const string LegacyCommandName = "/lightautopair";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration configuration;
    private readonly AutoPairController controller;
    private bool windowOpen;
    private bool clearBlacklistPopup;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Normalize();
        configuration.Save();

        controller = new AutoPairController(configuration);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Lightless Auto Pair settings and status.",
        });
        CommandManager.AddHandler(LegacyCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Legacy alias for /lap.",
        });

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LegacyCommandName);
        controller.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework) => controller.OnFrameworkUpdate();
    private void OnCommand(string command, string arguments) => windowOpen = !windowOpen;
    private void OpenConfig() => windowOpen = true;

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(760, 650), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Lightless Auto Pair", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawControls();
        ImGui.Separator();
        DrawStatus();
        ImGui.Separator();
        DrawPending();
        ImGui.Separator();
        DrawBlacklist();
        ImGui.Separator();
        DrawLog();

        ImGui.End();
    }

    private void DrawControls()
    {
        var changed = false;
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable automatic Lightfinder pairing", ref enabled))
        {
            configuration.Enabled = enabled;
            changed = true;
        }

        ImGui.TextDisabled("The master toggle defaults to off. Users already covered by a joined/shared syncshell are ignored.");

        var delay = Math.Clamp(configuration.DelaySeconds, 3, 30);
        if (ImGui.SliderInt("Delay between requests", ref delay, 3, 30, "%d seconds"))
        {
            configuration.DelaySeconds = delay;
            changed = true;
        }

        if (changed)
            configuration.Save();
    }

    private void DrawStatus()
    {
        ImGui.TextUnformatted("Status");
        ImGui.BulletText($"Master toggle: {(configuration.Enabled ? "On" : "Off")}");
        ImGui.BulletText($"Lightless detected: {(controller.LightlessDetected ? "Yes" : "No")}");
        ImGui.BulletText($"Internal bridge ready: {(controller.LightlessLoaded ? "Yes" : "No")}");
        ImGui.BulletText($"Lightless connected: {(controller.LightlessConnected ? "Yes" : "No — automatically paused")}");
        ImGui.BulletText($"Automation state: {FormatState(controller.State)}");
        ImGui.BulletText($"Nearby Lightfinder users: {controller.NearbyCount}");
        ImGui.BulletText($"Ignored through joined/shared syncshells: {controller.JoinedSyncshellIgnoredCount}");
        ImGui.BulletText($"Eligible now: {controller.EligibleCount}");
        ImGui.BulletText($"Outgoing requests tracked as pending: {controller.PendingCount}");
        ImGui.TextWrapped(controller.CompatibilityStatus);
    }

    private void DrawPending()
    {
        var pending = controller.GetPendingSnapshot();
        ImGui.TextUnformatted($"Pending requests ({pending.Count})");
        ImGui.TextDisabled("People in this persistent list are never contacted again while their request remains pending.");

        if (pending.Count == 0)
        {
            ImGui.TextDisabled("None");
            return;
        }

        if (ImGui.BeginChild("PendingRequests", new Vector2(0, 90), true))
        {
            foreach (var request in pending)
            {
                var localTime = request.SentAtUtc.ToLocalTime();
                ImGui.TextUnformatted($"[{localTime:HH:mm:ss}] {request.FriendlyName}");
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Clear persistent pending tracker"))
            controller.ClearPendingTracker();
        ImGui.SameLine();
        ImGui.TextDisabled("Use only when you know Lightless no longer has those requests pending.");
    }

    private void DrawBlacklist()
    {
        ImGui.TextUnformatted($"Lifetime decline blacklist ({configuration.DeclinedBlacklist.Count})");
        ImGui.TextDisabled("A matched Lightless decline notification permanently adds that person here.");

        if (ImGui.Button("Clear lifetime blacklist"))
        {
            clearBlacklistPopup = true;
            ImGui.OpenPopup("Clear decline blacklist?");
        }

        if (clearBlacklistPopup && ImGui.BeginPopupModal("Clear decline blacklist?", ref clearBlacklistPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Remove every person from the permanent decline blacklist?");
            if (ImGui.Button("Clear all", new Vector2(120, 0)))
            {
                configuration.DeclinedBlacklist.Clear();
                configuration.Save();
                clearBlacklistPopup = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                clearBlacklistPopup = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (configuration.DeclinedBlacklist.Count == 0)
        {
            ImGui.TextDisabled("None");
            return;
        }

        if (ImGui.BeginChild("DeclineBlacklist", new Vector2(0, 130), true))
        {
            BlacklistEntry? remove = null;
            foreach (var entry in configuration.DeclinedBlacklist
                         .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.PushID(entry.HashedCid);
                if (ImGui.SmallButton("Remove"))
                    remove = entry;
                ImGui.SameLine();
                ImGui.TextUnformatted($"{entry.DisplayName} — {entry.DeclinedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
                ImGui.PopID();
            }

            if (remove is not null)
            {
                configuration.DeclinedBlacklist.Remove(remove);
                configuration.Save();
            }
        }
        ImGui.EndChild();
    }

    private void DrawLog()
    {
        ImGui.TextUnformatted("Status log");
        if (ImGui.Button("Clear log"))
            controller.ClearLog();

        var entries = controller.GetLogSnapshot();
        if (ImGui.BeginChild("AutoPairLog", new Vector2(0, 190), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            foreach (var entry in entries)
            {
                var person = string.IsNullOrWhiteSpace(entry.Person) ? string.Empty : $" {entry.Person} —";
                ImGui.TextUnformatted($"[{entry.TimestampUtc.ToLocalTime():HH:mm:ss}] [{entry.Kind}]{person} {entry.Message}");
            }

            if (entries.Count > 0 && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 8f)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private static string FormatState(AutoPairState state) => state switch
    {
        AutoPairState.Disabled => "Disabled",
        AutoPairState.WaitingForLightless => "Waiting for Lightless",
        AutoPairState.Disconnected => "Paused — disconnected",
        AutoPairState.Ready => "Ready",
        AutoPairState.Sending => "Sending request",
        AutoPairState.Error => "Error",
        _ => state.ToString(),
    };
}
