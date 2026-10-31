using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace DMUModelScale;

/// <summary>
/// Plays the player's Phase 2 media file as a flat video on the arena floor.
/// It is a screen overlay: it does not change the game's floor, and it draws over players and effects.
/// </summary>
internal sealed unsafe class VideoFloorController : IDisposable
{
    private const int SquareCells = 24;
    private const int RoundRings = 16;
    private const int RoundSegments = 72;

    private readonly IUiBuilder uiBuilder;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly Func<MediaClock?> audioClock;
    private readonly object sync = new();
    private readonly List<Vector4> clipRects = new();

    private VideoDecoder? decoder;
    private string? decoderPath;
    private long uploadedSerial;
    private float? groundHeight;
    private string status = "Floor video is off.";

    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11Texture2D* texture;
    private ID3D11ShaderResourceView* view;
    private int textureWidth;
    private int textureHeight;
    private bool textureHasMips;

    private Vector2[] screen = Array.Empty<Vector2>();
    private Vector2[] uvs = Array.Empty<Vector2>();
    private bool[] inFront = Array.Empty<bool>();

    public VideoFloorController(IUiBuilder uiBuilder, IGameGui gameGui, Configuration config, Func<MediaClock?> audioClock)
    {
        this.uiBuilder = uiBuilder;
        this.gameGui = gameGui;
        this.config = config;
        this.audioClock = audioClock;
        uiBuilder.Draw += Draw;
    }

    public string Status
    {
        get
        {
            lock (sync)
                return decoder?.Status ?? status;
        }
    }

    /// <summary>Starts or stops the video. Called on each framework update.</summary>
    public void Update(bool show, string? path, float? playerHeight)
    {
        lock (sync)
        {
            if (!show || path is null)
            {
                StopDecoder();
                status = path is null ? "Choose a media file to show it on the floor." : "Floor video shows only during Phase 2.";
                return;
            }

            // The arena floor is flat, so the lowest point the player stands at is the ground.
            // Taking the minimum ignores jumps.
            if (playerHeight is float height && float.IsFinite(height))
                groundHeight = groundHeight is float known ? MathF.Min(known, height) : height;

            if (decoder is not null && string.Equals(decoderPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            StopDecoder();
            decoderPath = path;
            uploadedSerial = 0;
            groundHeight = playerHeight is float start && float.IsFinite(start) ? start : null;
            decoder = new VideoDecoder(path, new FloorClock(path, audioClock, () => decoder?.Duration ?? 0));
        }
    }

    private void StopDecoder()
    {
        decoder?.Dispose();
        decoder = null;
        decoderPath = null;
    }

    private void Draw()
    {
        lock (sync)
        {
            if (decoder is null)
                return;
            try
            {
                if (!EnsureDevice())
                    return;
                decoder.TryUploadLatest(ref uploadedSerial, Upload);
                if (view != null)
                    DrawFloor();
            }
            catch (Exception ex)
            {
                // Never let the overlay break the game's UI frame.
                StopDecoder();
                status = $"Floor video stopped: {ex.Message}";
            }
        }
    }

    private bool EnsureDevice()
    {
        if (device != null)
            return true;
        var handle = uiBuilder.DeviceHandle;
        if (handle == 0)
            return false;

        ID3D11Device* found = null;
        Check(((IUnknown*)handle)->QueryInterface(__uuidof<ID3D11Device>(), (void**)&found), "get the game's Direct3D device");
        device = found;
        ID3D11DeviceContext* immediate = null;
        device->GetImmediateContext(&immediate);
        context = immediate;
        return true;
    }

    private void Upload(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (texture == null || width != textureWidth || height != textureHeight)
            CreateTexture(width, height);

        fixed (byte* data = pixels)
            context->UpdateSubresource((ID3D11Resource*)texture, 0, null, data, (uint)(width * 4), 0);

        // Mipmaps keep the far side of the floor smooth instead of shimmering.
        if (textureHasMips)
            context->GenerateMips(view);
    }

    private void CreateTexture(int width, int height)
    {
        ReleaseTexture();
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 0,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET),
            CPUAccessFlags = 0,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS
        };

        ID3D11Texture2D* created = null;
        textureHasMips = device->CreateTexture2D(&description, null, &created).SUCCEEDED;
        if (!textureHasMips)
        {
            description.MipLevels = 1;
            description.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
            description.MiscFlags = 0;
            Check(device->CreateTexture2D(&description, null, &created), "create the video texture");
        }

        ID3D11ShaderResourceView* createdView = null;
        var viewResult = device->CreateShaderResourceView((ID3D11Resource*)created, null, &createdView);
        if (viewResult.FAILED)
        {
            created->Release();
            Check(viewResult, "create the video texture view");
        }

        texture = created;
        view = createdView;
        textureWidth = width;
        textureHeight = height;
    }

    private void DrawFloor()
    {
        var round = config.FloorRound;
        var columns = round ? RoundSegments : SquareCells;
        var rows = round ? RoundRings : SquareCells;
        var vertexCount = (columns + 1) * (rows + 1);
        if (screen.Length < vertexCount)
        {
            screen = new Vector2[vertexCount];
            uvs = new Vector2[vertexCount];
            inFront = new bool[vertexCount];
        }

        // Fit the video: crop its long side to fill the floor, or stretch it.
        var uHalf = 0.5f;
        var vHalf = 0.5f;
        var aspect = textureWidth / (float)textureHeight;
        if (config.FloorCropToFill)
        {
            if (aspect > 1f)
                uHalf = 0.5f / aspect;
            else
                vHalf = 0.5f * aspect;
        }

        var size = Math.Max(1f, config.FloorSize);
        var angle = config.FloorRotation * MathF.PI / 180f;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var floorY = config.FloorMatchPlayerHeight && groundHeight is float ground ? ground : config.FloorHeight;
        // Lift the video slightly so it does not flicker against the ground.
        var center = new Vector3(config.FloorCenterX, floorY + 0.02f, config.FloorCenterZ);

        for (var row = 0; row <= rows; row++)
        {
            for (var column = 0; column <= columns; column++)
            {
                // Local floor position in -0.5..0.5. +X is east and +Z is south, so the video
                // reads normally from above with north at the top.
                float s, t;
                if (round)
                {
                    var radius = row / (float)rows * 0.5f;
                    var theta = column / (float)columns * MathF.Tau;
                    s = radius * MathF.Cos(theta);
                    t = radius * MathF.Sin(theta);
                }
                else
                {
                    s = column / (float)columns - 0.5f;
                    t = row / (float)rows - 0.5f;
                }

                var x = s * size;
                var z = t * size;
                var world = center + new Vector3(x * cos - z * sin, 0f, x * sin + z * cos);
                var index = row * (columns + 1) + column;
                inFront[index] = gameGui.WorldToScreen(world, out screen[index], out _);
                uvs[index] = new Vector2(0.5f + s * 2f * uHalf, 0.5f + t * 2f * vHalf);
            }
        }

        var viewport = ImGui.GetMainViewport();
        var viewportMin = viewport.Pos;
        var viewportMax = viewport.Pos + viewport.Size;
        BuildClipRects(viewportMin, viewportMax);

        var drawList = ImGui.GetBackgroundDrawList();
        var textureId = new ImTextureID((nint)view);
        var color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Math.Clamp(config.FloorOpacity, 0.05f, 1f)));

        foreach (var clip in clipRects)
        {
            drawList.PushClipRect(new Vector2(clip.X, clip.Y), new Vector2(clip.Z, clip.W), true);
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var a = row * (columns + 1) + column;
                    var b = a + 1;
                    var c = a + columns + 2;
                    var d = a + columns + 1;
                    // Skip cells that reach behind the camera; their projection is not usable.
                    if (!inFront[a] || !inFront[b] || !inFront[c] || !inFront[d])
                        continue;

                    var minX = MathF.Min(MathF.Min(screen[a].X, screen[b].X), MathF.Min(screen[c].X, screen[d].X));
                    var maxX = MathF.Max(MathF.Max(screen[a].X, screen[b].X), MathF.Max(screen[c].X, screen[d].X));
                    var minY = MathF.Min(MathF.Min(screen[a].Y, screen[b].Y), MathF.Min(screen[c].Y, screen[d].Y));
                    var maxY = MathF.Max(MathF.Max(screen[a].Y, screen[b].Y), MathF.Max(screen[c].Y, screen[d].Y));
                    if (maxX < clip.X || minX > clip.Z || maxY < clip.Y || minY > clip.W)
                        continue;

                    drawList.AddImageQuad(textureId, screen[a], screen[b], screen[c], screen[d],
                        uvs[a], uvs[b], uvs[c], uvs[d], color);
                }
            }
            drawList.PopClipRect();
        }
    }

    /// <summary>
    /// Splits the screen into rectangles that avoid visible game UI windows, so the
    /// floor never covers hotbars, the party list or chat.
    /// </summary>
    private void BuildClipRects(Vector2 viewportMin, Vector2 viewportMax)
    {
        clipRects.Clear();
        var full = new Vector4(viewportMin.X, viewportMin.Y, viewportMax.X, viewportMax.Y);
        if (!config.FloorKeepHudVisible)
        {
            clipRects.Add(full);
            return;
        }

        var hud = new List<Vector4>();
        var screenArea = (viewportMax.X - viewportMin.X) * (viewportMax.Y - viewportMin.Y);
        var stage = AtkStage.Instance();
        if (stage != null && stage->RaptureAtkUnitManager != null)
        {
            ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < list.Count; i++)
            {
                var unit = list.Entries[i].Value;
                if (unit == null || !unit->IsVisible || unit->RootNode == null)
                    continue;
                var scale = unit->Scale;
                var width = unit->RootNode->Width * scale;
                var height = unit->RootNode->Height * scale;
                // Full-screen layers such as nameplates and screen text would hide the whole floor.
                if (width <= 1 || height <= 1 || width * height > screenArea * 0.4f)
                    continue;
                var x = viewportMin.X + unit->X;
                var y = viewportMin.Y + unit->Y;
                hud.Add(new Vector4(x, y, x + width, y + height));
            }
        }

        if (hud.Count == 0)
        {
            clipRects.Add(full);
            return;
        }

        var xs = new List<float> { full.X, full.Z };
        var ys = new List<float> { full.Y, full.W };
        foreach (var rect in hud)
        {
            xs.Add(Math.Clamp(rect.X, full.X, full.Z));
            xs.Add(Math.Clamp(rect.Z, full.X, full.Z));
            ys.Add(Math.Clamp(rect.Y, full.Y, full.W));
            ys.Add(Math.Clamp(rect.W, full.Y, full.W));
        }
        xs.Sort();
        ys.Sort();

        for (var yi = 0; yi < ys.Count - 1; yi++)
        {
            var top = ys[yi];
            var bottom = ys[yi + 1];
            if (bottom - top < 0.5f)
                continue;
            var midY = (top + bottom) * 0.5f;
            var runStart = float.NaN;
            for (var xi = 0; xi < xs.Count - 1; xi++)
            {
                var left = xs[xi];
                var right = xs[xi + 1];
                if (right - left < 0.5f)
                    continue;
                var midX = (left + right) * 0.5f;
                var covered = false;
                foreach (var rect in hud)
                {
                    if (midX > rect.X && midX < rect.Z && midY > rect.Y && midY < rect.W)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered && float.IsNaN(runStart))
                    runStart = left;
                if (covered && !float.IsNaN(runStart))
                {
                    AddClip(runStart, top, left, bottom);
                    runStart = float.NaN;
                }
            }
            if (!float.IsNaN(runStart))
                AddClip(runStart, top, xs[^1], bottom);
        }
    }

    private void AddClip(float left, float top, float right, float bottom)
    {
        // Merge with the rectangle directly above when it spans the same columns.
        for (var i = clipRects.Count - 1; i >= 0; i--)
        {
            var above = clipRects[i];
            if (above.W == top && above.X == left && above.Z == right)
            {
                clipRects[i] = new Vector4(left, above.Y, right, bottom);
                return;
            }
        }
        clipRects.Add(new Vector4(left, top, right, bottom));
    }

    private void ReleaseTexture()
    {
        if (view != null)
            view->Release();
        if (texture != null)
            texture->Release();
        view = null;
        texture = null;
        textureWidth = 0;
        textureHeight = 0;
    }

    private static void Check(HRESULT result, string action)
    {
        if (result.FAILED)
            throw new InvalidOperationException($"Could not {action} (0x{result.Value:X8}).");
    }

    public void Dispose()
    {
        uiBuilder.Draw -= Draw;
        lock (sync)
        {
            StopDecoder();
            ReleaseTexture();
            if (context != null)
                context->Release();
            if (device != null)
                device->Release();
            context = null;
            device = null;
        }
    }

    /// <summary>
    /// Follows the music while it plays; otherwise keeps time on its own and loops at the video's end.
    /// Only the decoder thread calls this.
    /// </summary>
    private sealed class FloorClock(string path, Func<MediaClock?> audioClock, Func<double> duration) : IVideoClock
    {
        private readonly Stopwatch stopwatch = new();
        private double offset;
        private double last;
        private bool followingAudio;

        public double Now()
        {
            var audio = audioClock();
            if (audio is { Playing: true } && string.Equals(audio.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                // Interpolate between framework updates, but never run far ahead of a stale sample.
                var elapsed = Math.Min(Stopwatch.GetElapsedTime(audio.Timestamp).TotalSeconds, 0.25);
                followingAudio = true;
                last = audio.Seconds + elapsed;
                return last;
            }

            if (followingAudio || !stopwatch.IsRunning)
            {
                offset = last;
                stopwatch.Restart();
                followingAudio = false;
            }

            last = offset + stopwatch.Elapsed.TotalSeconds;
            var length = duration();
            if (length > 0 && last >= length + 0.5)
                Ended();
            return last;
        }

        public void Ended()
        {
            if (followingAudio)
                return; // The music decides when to loop.
            offset = 0;
            last = 0;
            stopwatch.Restart();
        }
    }
}
