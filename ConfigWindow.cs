using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DMUModelScale;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Func<string> audioStatus;
    private readonly Func<string> modelStatus;

    public ConfigWindow(Configuration config, Action save, Func<string> audioStatus, Func<string> modelStatus) : base("Sm0ll Chaos & Exdeath###DMUModelScale")
    {
        this.config = config;
        this.save = save;
        this.audioStatus = audioStatus;
        this.modelStatus = modelStatus;
        Size = new Vector2(620, 635);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 635),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enable in Dancing Mad (Ultimate)", ref enabled))
        {
            config.Enabled = enabled;
            save();
        }

        var chaos = config.ChaosScale;
        ImGui.BeginDisabled(config.ChaoticMode);
        if (ImGui.SliderFloat("Chaos", ref chaos, 0.30f, 1.00f, "%.2f"))
        {
            config.ChaosScale = chaos;
            save();
        }
        ImGui.EndDisabled();

        var exdeath = config.ExdeathScale;
        if (ImGui.SliderFloat("Exdeath", ref exdeath, 0.30f, 1.00f, "%.2f"))
        {
            config.ExdeathScale = exdeath;
            save();
        }

        var chaoticMode = config.ChaoticMode;
        if (ImGui.Checkbox("Chaotic mode", ref chaoticMode))
        {
            config.ChaoticMode = chaoticMode;
            save();
        }

        ImGui.TextWrapped("Changes the size of Chaos and Exdeath as they can be extremely distracting in this fight.");
        ImGui.TextWrapped("Chaotic mode: Chaos is smallest while idle and full size while casting.");

        ImGui.Separator();
        var garuda = config.ShowGarudaInPhase1;
        if (ImGui.Checkbox("P1 Garuda (model + name)", ref garuda))
        {
            config.ShowGarudaInPhase1 = garuda;
            save();
        }
        var dancingGreen = config.ShowDancingGreenInPhase2;
        if (ImGui.Checkbox("P2 Dancing Green (model + name)", ref dancingGreen))
        {
            config.ShowDancingGreenInPhase2 = dancingGreen;
            save();
        }
        ImGui.TextWrapped("Model swaps also change the boss name locally. Boss hitboxes and mechanics stay the same.");
        ImGui.TextWrapped(modelStatus());

        ImGui.Separator();
        var replaceBgm = config.ReplaceBgm;
        if (ImGui.Checkbox("Replace fight BGM by phase", ref replaceBgm))
        {
            config.ReplaceBgm = replaceBgm;
            save();
        }
        ImGui.TextWrapped("Phase 1: Fallen Angel | Phase 2: Ride the Rhythm | Phase 3+: Circus. Each track loops until its phase ends.");

        var slamSound = config.PlayKefkaSlamSound;
        if (ImGui.Checkbox("Bonk on Kefka's P3 ground slams", ref slamSound))
        {
            config.PlayKefkaSlamSound = slamSound;
            save();
        }
        ImGui.TextWrapped("All four audio clips are included in this local build. Audio only plays in Dancing Mad (Ultimate).");
        ImGui.TextWrapped(audioStatus());

        var moveLifebar = config.MoveLifebarWithModel;
        if (ImGui.Checkbox("Move floating lifebar with model", ref moveLifebar))
        {
            config.MoveLifebarWithModel = moveLifebar;
            save();
        }
    }
}
