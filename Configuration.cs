using System;
using Dalamud.Configuration;

namespace DMUModelScale;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public float ChaosScale { get; set; } = 0.60f;
    public float ExdeathScale { get; set; } = 0.60f;
    public bool ChaoticMode { get; set; } = false;
    public bool MoveLifebarWithModel { get; set; } = true;
    public bool Brainrot { get; set; } = false;

    // Phase 2 media file chosen by the player. It is never bundled with the plugin.
    public string Phase2MediaPath { get; set; } = string.Empty;
    public bool Phase2MediaMusic { get; set; } = true;
    public bool Phase2FloorVideo { get; set; } = true;
    public float FloorCenterX { get; set; } = 100f;
    public float FloorCenterZ { get; set; } = 100f;
    public float FloorHeight { get; set; } = 0f;
    public float FloorSize { get; set; } = 40f;
    public float FloorRotation { get; set; } = 0f;
    public bool FloorRound { get; set; } = false;
    public bool FloorCropToFill { get; set; } = true;
    public float FloorOpacity { get; set; } = 0.85f;
    public bool FloorKeepHudVisible { get; set; } = true;
    // Kept only to migrate settings saved by earlier releases.
    public bool ShowGarudaInPhase1 { get; set; } = false;
    public bool ShowDancingGreenInPhase2 { get; set; } = false;
    public bool ReplaceBgm { get; set; } = false;
    public bool PlayKefkaSlamSound { get; set; } = false;
}
