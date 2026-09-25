using System;
using Dalamud.Configuration;

namespace DMUModelScale;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public float ChaosScale { get; set; } = 0.60f;
    public float ExdeathScale { get; set; } = 0.60f;
    public bool ChaoticMode { get; set; } = false;
    public bool MoveLifebarWithModel { get; set; } = true;
}
