using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Math;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DMUModelScale;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const uint TerritoryId = 1363;
    private const uint ChaosBaseId = 19508;
    private const uint ExdeathBaseId = 19509;
    private const float LifebarHeightAdjustment = -0.33f;
    private const string Command = "/dmuscale";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;

    private readonly Dictionary<nint, CapturedScale> captured = new();
    private readonly WindowSystem windows = new("DMUModelScale");
    private readonly ConfigWindow configWindow;
    private readonly Configuration config;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.ChaosScale = ClampScale(config.ChaosScale);
        config.ExdeathScale = ClampScale(config.ExdeathScale);

        configWindow = new ConfigWindow(config, SaveConfig);
        windows.AddWindow(configWindow);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        CommandManager.AddHandler(Command, new CommandInfo((_, _) => ToggleConfig())
        {
            HelpMessage = "Open the DMU Model Scale settings."
        });
        Framework.Update += OnFrameworkUpdate;
    }

    private static float ClampScale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, 0.30f, 1.00f) : 0.60f;

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void ToggleConfig() => configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.Enabled || !ClientState.IsLoggedIn || ClientState.TerritoryType != TerritoryId)
        {
            RestoreVisibleModels();
            return;
        }

        var seen = new HashSet<nint>();
        foreach (var obj in ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc ||
                (obj.BaseId != ChaosBaseId && obj.BaseId != ExdeathBaseId) ||
                obj.Address == IntPtr.Zero)
                continue;

            var native = (NativeGameObject*)obj.Address;
            var draw = native->DrawObject;
            if (draw == null)
                continue;

            var address = (nint)obj.Address;
            var drawAddress = (nint)draw;
            seen.Add(address);

            // A reused object-table slot or replaced render object needs a fresh baseline.
            if (!captured.TryGetValue(address, out var state) ||
                state.EntityId != obj.EntityId || state.BaseId != obj.BaseId || state.DrawAddress != drawAddress)
            {
                state = new CapturedScale(obj.EntityId, obj.BaseId, drawAddress, draw->Scale,
                    native->NameplateOffsetTarget.Y);
                captured[address] = state;
            }

            var factor = obj.BaseId == ChaosBaseId ? ClampScale(config.ChaosScale) : ClampScale(config.ExdeathScale);
            var target = state.OriginalScale;
            target.X *= factor;
            target.Y *= factor;
            target.Z *= factor;
            SetRenderScale(draw, target);
            if (config.MoveLifebarWithModel)
            {
                var heightFactor = Math.Clamp(factor + LifebarHeightAdjustment, 0.00f, 1.00f);
                SetNameplateOffset(native, state, heightFactor);
            }
            else
                RestoreNameplateOffset(native, state);
        }

        // Never dereference an address after its object has left the table.
        var stale = new List<nint>();
        foreach (var address in captured.Keys)
            if (!seen.Contains(address))
                stale.Add(address);
        foreach (var address in stale)
            captured.Remove(address);
    }

    private static void SetRenderScale(DrawObject* draw, Vector3 scale)
    {
        if (draw->Scale.X == scale.X && draw->Scale.Y == scale.Y && draw->Scale.Z == scale.Z)
            return;

        draw->Scale = scale;
        draw->NotifyTransformChanged();
    }

    private static void SetNameplateOffset(NativeGameObject* native, CapturedScale state, float heightFactor)
    {
        var current = native->NameplateOffsetTarget.Y;
        var height = native->Height;
        if (!float.IsFinite(current) || !float.IsFinite(height) || height <= 0)
            return;

        // If the game changed the target offset, keep that as the new baseline.
        if (state.LastAppliedNameplateOffsetY is float last && current != last)
            state.OriginalNameplateOffsetY = current;

        // Apply the fixed adjustment to place the nameplate below the model-only height.
        var adjusted = state.OriginalNameplateOffsetY + height * (heightFactor - 1f);
        if (!float.IsFinite(adjusted))
            return;

        native->NameplateOffsetTarget.Y = adjusted;
        state.LastAppliedNameplateOffsetY = adjusted;
    }

    private static void RestoreNameplateOffset(NativeGameObject* native, CapturedScale state)
    {
        var current = native->NameplateOffsetTarget.Y;
        if (state.LastAppliedNameplateOffsetY is float last && current == last)
        {
            current = state.OriginalNameplateOffsetY;
            native->NameplateOffsetTarget.Y = current;
        }

        // Keep the game's latest offset as the baseline while the option is off.
        if (float.IsFinite(current))
            state.OriginalNameplateOffsetY = current;
        state.LastAppliedNameplateOffsetY = null;
    }

    private void RestoreVisibleModels()
    {
        if (captured.Count == 0)
            return;

        foreach (var obj in ObjectTable)
        {
            if (obj.Address == IntPtr.Zero || !captured.TryGetValue((nint)obj.Address, out var state) ||
                obj.ObjectKind != ObjectKind.BattleNpc || obj.BaseId != state.BaseId || obj.EntityId != state.EntityId)
                continue;

            var native = (NativeGameObject*)obj.Address;
            var draw = native->DrawObject;
            if (draw != null && (nint)draw == state.DrawAddress)
            {
                SetRenderScale(draw, state.OriginalScale);
                RestoreNameplateOffset(native, state);
            }
        }

        captured.Clear();
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        RestoreVisibleModels();
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
    }

    private sealed class CapturedScale(uint entityId, uint baseId, nint drawAddress,
        Vector3 originalScale, float originalNameplateOffsetY)
    {
        public uint EntityId { get; } = entityId;
        public uint BaseId { get; } = baseId;
        public nint DrawAddress { get; } = drawAddress;
        public Vector3 OriginalScale { get; } = originalScale;
        public float OriginalNameplateOffsetY { get; set; } = originalNameplateOffsetY;
        public float? LastAppliedNameplateOffsetY { get; set; }
    }
}
