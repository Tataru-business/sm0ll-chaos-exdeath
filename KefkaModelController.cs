using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace DMUModelScale;

/// <summary>Applies local render-model changes to the two Kefka phase actors.</summary>
internal sealed unsafe class KefkaModelController(IObjectTable objectTable, INamePlateGui namePlateGui) : IDisposable
{
    // BNpcBase 19504 is P1 Kefka (and is reused by an untargetable P3 actor).
    // BNpcBase 19506 is P2 Kefka. ModelChara IDs come from the game data sheets.
    private const uint KefkaP1BaseId = 19504;
    private const uint KefkaP2BaseId = 19506;
    private const uint KefkaP2CloneBaseId = 19513;
    private const uint ChaosP3BaseId = 19508;
    private const uint ExdeathP3BaseId = 19509;
    private const int KefkaP1OriginalModelId = 4966;
    private const int KefkaP2CloneOriginalModelId = 2137;
    private const int GarudaUwuModelId = 2276;
    private const int DancingGreenModelId = 4412;
    private const float GarudaVisualScale = 1f / 3f; // UWU Garuda's BNpcBase scale 1.0 vs P1 Kefka's 3.0.
    private const float DancingGreenCloneVisualScale = 0.5f;

    private readonly Dictionary<nint, CapturedModel> captured = new();
    private bool phase2Seen;
    private string status = "Kefka model options are off.";

    public string Status => status;
    public void ResetForNewPull() => phase2Seen = false;

    public void Update(bool inDuty, AudioPhase phase, bool useGarudaP1, bool useDancingGreenP2)
    {
        if (!inDuty)
            phase2Seen = false;
        else
        {
            // Once P2 or P3 has appeared, the reused P1 base ID must not be
            // interpreted as the P1 boss until the duty recommences.
            foreach (var obj in objectTable)
            {
                if (obj.ObjectKind == ObjectKind.BattleNpc &&
                    obj.BaseId is KefkaP2BaseId or KefkaP2CloneBaseId or ChaosP3BaseId or ExdeathP3BaseId)
                {
                    phase2Seen = true;
                    break;
                }
            }
        }

        var seen = new HashSet<nint>();
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc ||
                (obj.BaseId != KefkaP1BaseId && obj.BaseId != KefkaP2BaseId && obj.BaseId != KefkaP2CloneBaseId) ||
                obj.Address == IntPtr.Zero)
                continue;

            var address = (nint)obj.Address;
            var native = (Character*)obj.Address;
            seen.Add(address);
            if (!captured.TryGetValue(address, out var state) ||
                state.EntityId != obj.EntityId || state.BaseId != obj.BaseId)
            {
                state = new CapturedModel(obj.EntityId, obj.BaseId, native->ModelContainer.ModelCharaId,
                    native->NameString);
                captured[address] = state;
            }

            if (state.PendingEnable)
            {
                if (native->IsReadyToDraw())
                {
                    native->EnableDraw();
                    state.PendingEnable = false;
                    state.DrawAddress = 0;
                }
                else
                {
                    status = "Waiting for the game to finish loading Kefka's replacement model.";
                    continue;
                }
            }

            var target = state.OriginalModelId;
            if (inDuty && phase == AudioPhase.Phase1 && obj.BaseId == KefkaP1BaseId && useGarudaP1 &&
                !phase2Seen && obj.IsTargetable && state.OriginalModelId == KefkaP1OriginalModelId)
                target = GarudaUwuModelId;
            else if (inDuty && phase == AudioPhase.Phase2 && obj.BaseId == KefkaP2BaseId && useDancingGreenP2)
                target = DancingGreenModelId;
            else if (inDuty && phase == AudioPhase.Phase2 && obj.BaseId == KefkaP2CloneBaseId &&
                     useDancingGreenP2 && state.OriginalModelId == KefkaP2CloneOriginalModelId)
                target = DancingGreenModelId;

            var current = native->ModelContainer.ModelCharaId;
            if (target == state.OriginalModelId)
            {
                RestoreName(native, state);
                if (!state.OwnsModel)
                    continue;
                // If the game changed the model itself, do not overwrite it.
                state.OwnsModel = false;
                if (current != state.AppliedModelId)
                    continue;
                ChangeModel(native, state, state.OriginalModelId);
                continue;
            }

            if (!state.OwnsModel && current != state.OriginalModelId)
            {
                RestoreName(native, state);
                continue;
            }
            ApplyReplacementName(native, state, target == GarudaUwuModelId ? "Garuda" : "Dancing Green");
            if (current != target)
            {
                state.OwnsModel = true;
                state.AppliedModelId = target;
                ChangeModel(native, state, target);
                continue;
            }

            if (state.OwnsModel && target == GarudaUwuModelId)
                ApplyVisualScale(native, state, GarudaVisualScale);
            else if (state.OwnsModel && target == DancingGreenModelId && obj.BaseId == KefkaP2CloneBaseId)
                ApplyVisualScale(native, state, DancingGreenCloneVisualScale);
        }

        var stale = new List<nint>();
        foreach (var address in captured.Keys)
            if (!seen.Contains(address))
                stale.Add(address);
        foreach (var address in stale)
            captured.Remove(address);

        var pending = false;
        var active = false;
        foreach (var state in captured.Values)
        {
            pending |= state.PendingEnable;
            active |= state.OwnsModel;
        }

        if (pending)
            status = "Waiting for the game to finish loading Kefka's replacement model.";
        else if (!inDuty)
            status = "Kefka model changes work only in Dancing Mad (Ultimate).";
        else if (!useGarudaP1 && !useDancingGreenP2)
            status = "Kefka model options are off.";
        else if (active)
            status = "Kefka model change is active for the current phase.";
        else
            status = "Waiting for the selected Kefka phase.";
    }

    private static void ChangeModel(Character* native, CapturedModel state, int modelId)
    {
        native->ModelContainer.ModelCharaId = modelId;
        native->DisableDraw();
        state.PendingEnable = true;
        state.DrawAddress = 0;
    }

    private void ApplyReplacementName(Character* native, CapturedModel state, string replacement)
    {
        var current = native->NameString;
        if (state.AppliedName is { } applied && current != applied && current != state.OriginalName)
        {
            // Another system took over this name; leave it alone.
            state.AppliedName = null;
            return;
        }
        if (state.AppliedName is null && current != state.OriginalName)
            return;
        if (current != replacement)
        {
            native->SetName(replacement);
            namePlateGui.RequestRedraw();
        }
        state.AppliedName = replacement;
    }

    private void RestoreName(Character* native, CapturedModel state)
    {
        if (state.AppliedName is not { } applied)
            return;
        if (native->NameString == applied)
        {
            native->SetName(state.OriginalName);
            namePlateGui.RequestRedraw();
        }
        state.AppliedName = null;
    }

    private static void ApplyVisualScale(Character* native, CapturedModel state, float factor)
    {
        var draw = native->DrawObject;
        if (draw == null)
            return;

        if (state.DrawAddress != (nint)draw)
        {
            state.DrawAddress = (nint)draw;
            state.DrawScale = draw->Scale;
        }

        var target = state.DrawScale;
        target.X *= factor;
        target.Y *= factor;
        target.Z *= factor;
        if (draw->Scale.X != target.X || draw->Scale.Y != target.Y || draw->Scale.Z != target.Z)
        {
            draw->Scale = target;
            draw->NotifyTransformChanged();
        }
    }

    public void Dispose()
    {
        foreach (var obj in objectTable)
        {
            if (obj.Address == IntPtr.Zero ||
                !captured.TryGetValue((nint)obj.Address, out var state) ||
                obj.EntityId != state.EntityId || obj.BaseId != state.BaseId)
                continue;

            var native = (Character*)obj.Address;
            if (state.OwnsModel && native->ModelContainer.ModelCharaId == state.AppliedModelId)
            {
                native->ModelContainer.ModelCharaId = state.OriginalModelId;
                native->DisableDraw();
                // We cannot await another framework update after unload. Restore
                // visibility immediately; the game recreates the render model.
                native->EnableDraw();
            }
            else if (state.PendingEnable)
                native->EnableDraw();
            RestoreName(native, state);
        }
        captured.Clear();
    }

    private sealed class CapturedModel(uint entityId, uint baseId, int originalModelId, string originalName)
    {
        public uint EntityId { get; } = entityId;
        public uint BaseId { get; } = baseId;
        public int OriginalModelId { get; } = originalModelId;
        public string OriginalName { get; } = originalName;
        public string? AppliedName { get; set; }
        public int AppliedModelId { get; set; }
        public bool OwnsModel { get; set; }
        public bool PendingEnable { get; set; }
        public nint DrawAddress { get; set; }
        public Vector3 DrawScale { get; set; }
    }
}
