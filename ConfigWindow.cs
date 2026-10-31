using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

namespace DMUModelScale;

public sealed class ConfigWindow : Window
{
    private const string MediaFilter =
        "Media files{.mp4,.m4v,.mov,.mkv,.webm,.wmv,.avi,.m4a,.mp3,.wav,.flac,.aac,.wma},.*";

    private readonly Configuration config;
    private readonly Action save;
    private readonly Func<string> audioStatus;
    private readonly Func<string> modelStatus;
    private readonly Func<string> floorStatus;
    private readonly Func<Vector3?> playerPosition;
    private readonly FileDialogManager fileDialog = new();

    public ConfigWindow(Configuration config, Action save, Func<string> audioStatus, Func<string> modelStatus,
        Func<string> floorStatus, Func<Vector3?> playerPosition) : base("Sm0ll Chaos & Exdeath###DMUModelScale")
    {
        this.config = config;
        this.save = save;
        this.audioStatus = audioStatus;
        this.modelStatus = modelStatus;
        this.floorStatus = floorStatus;
        this.playerPosition = playerPosition;
        Size = new Vector2(620, 640);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 430),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Shows the floor video anywhere, for placing it. Not saved.</summary>
    public bool PreviewFloor { get; private set; }

    public override void OnClose() => PreviewFloor = false;

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
        var brainrot = config.Brainrot;
        if (ImGui.Checkbox("Brainrot", ref brainrot))
        {
            config.Brainrot = brainrot;
            save();
        }
        ImGui.TextWrapped("P1 Garuda and P2 Dancing Green models, names, clones and effects; phase music and P3 Bonk slams.");
        ImGui.TextWrapped("Boss hitboxes and mechanics stay the same.");
        ImGui.TextWrapped(modelStatus());
        ImGui.TextWrapped("Phase 1: Fallen Angel | Phase 2: Ride the Rhythm | Phase 3+: Circus. Each track loops until its phase ends.");
        ImGui.TextWrapped("Music follows the game's BGM and master volume sliders and pauses when BGM is off.");
        ImGui.TextWrapped("All four audio clips are included. Audio only plays in Dancing Mad (Ultimate).");
        ImGui.TextWrapped(audioStatus());

        var moveLifebar = config.MoveLifebarWithModel;
        if (ImGui.Checkbox("Move floating lifebar with model", ref moveLifebar))
        {
            config.MoveLifebarWithModel = moveLifebar;
            save();
        }

        ImGui.Separator();
        DrawPhase2Media();
        fileDialog.Draw();
    }

    private void DrawPhase2Media()
    {
        if (!ImGui.CollapsingHeader("Phase 2 media file (Brainrot)"))
            return;

        ImGui.TextWrapped("Pick a video or audio file on your PC. It is played from that file and is never bundled with the plugin.");
        var path = config.Phase2MediaPath;
        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputText("##phase2media", ref path, 1024))
        {
            config.Phase2MediaPath = path.Trim().Trim('"');
            save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            fileDialog.OpenFileDialog("Choose the Phase 2 media file", MediaFilter, (ok, chosen) =>
            {
                if (!ok)
                    return;
                config.Phase2MediaPath = chosen;
                save();
            });
        }

        if (!string.IsNullOrWhiteSpace(config.Phase2MediaPath) && !File.Exists(config.Phase2MediaPath))
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "File not found. The bundled Phase 2 track is used.");

        var music = config.Phase2MediaMusic;
        if (ImGui.Checkbox("Use its audio as the Phase 2 music", ref music))
        {
            config.Phase2MediaMusic = music;
            save();
        }

        var floor = config.Phase2FloorVideo;
        if (ImGui.Checkbox("Show its video on the arena floor", ref floor))
        {
            config.Phase2FloorVideo = floor;
            save();
        }
        ImGui.TextWrapped("The floor video is drawn over the game, so it also covers players and ground markers. " +
            "Lower the opacity if mechanics get hard to read. It follows the music when that is playing.");

        var preview = PreviewFloor;
        if (ImGui.Checkbox("Preview the floor here (until this window closes)", ref preview))
            PreviewFloor = preview;
        ImGui.SameLine();
        if (ImGui.Button("Center on me") && playerPosition() is { } position)
        {
            config.FloorCenterX = position.X;
            config.FloorHeight = position.Y;
            config.FloorCenterZ = position.Z;
            save();
        }

        var changed = false;
        var centerX = config.FloorCenterX;
        var centerZ = config.FloorCenterZ;
        var height = config.FloorHeight;
        var size = config.FloorSize;
        var rotation = config.FloorRotation;
        var opacity = config.FloorOpacity;
        changed |= ImGui.DragFloat("Center X", ref centerX, 0.1f, -1000f, 1000f, "%.1f");
        changed |= ImGui.DragFloat("Center Z", ref centerZ, 0.1f, -1000f, 1000f, "%.1f");
        changed |= ImGui.DragFloat("Height", ref height, 0.02f, -1000f, 1000f, "%.2f");
        changed |= ImGui.SliderFloat("Size", ref size, 5f, 100f, "%.1f yalms");
        changed |= ImGui.SliderFloat("Rotation", ref rotation, 0f, 360f, "%.0f deg");
        changed |= ImGui.SliderFloat("Opacity", ref opacity, 0.05f, 1f, "%.2f");

        var round = config.FloorRound;
        changed |= ImGui.Checkbox("Round floor", ref round);
        ImGui.SameLine();
        var crop = config.FloorCropToFill;
        changed |= ImGui.Checkbox("Crop to fill", ref crop);
        ImGui.SameLine();
        var keepHud = config.FloorKeepHudVisible;
        changed |= ImGui.Checkbox("Keep HUD visible", ref keepHud);

        if (changed)
        {
            config.FloorCenterX = centerX;
            config.FloorCenterZ = centerZ;
            config.FloorHeight = height;
            config.FloorSize = Math.Clamp(size, 5f, 100f);
            config.FloorRotation = rotation;
            config.FloorOpacity = Math.Clamp(opacity, 0.05f, 1f);
            config.FloorRound = round;
            config.FloorCropToFill = crop;
            config.FloorKeepHudVisible = keepHud;
            save();
        }

        ImGui.TextWrapped(floorStatus());
    }
}
