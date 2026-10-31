using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace DMUModelScale;

/// <summary>
/// Plays the player's Phase 2 media file as a video on the arena floor.
/// The floor is rendered with the game's depth buffer, so characters and bosses stay in front of it,
/// and visible game UI is cut out of it.
/// </summary>
internal sealed unsafe class VideoFloorController : IDisposable
{
    private const int SquareCells = 32;
    private const int RoundRings = 24;
    private const int RoundSegments = 96;
    // Anything this far above the floor plane (characters, bosses) is drawn in front of the video.
    private const float OcclusionHeight = 0.15f;
    // Lift the video slightly above the game's floor so the two never fight over the same pixels.
    private const float FloorLift = 0.03f;
    private const int MaxUiPieces = 2048;

    private readonly IUiBuilder uiBuilder;
    private readonly Configuration config;
    private readonly Func<MediaClock?> audioClock;
    private readonly object sync = new();
    private readonly List<Vector4> uiPieces = new();
    private readonly List<FloorVertex> floorVertices = new();
    private readonly List<Vector2> maskVertices = new();

    private VideoDecoder? decoder;
    private string? decoderPath;
    private long uploadedSerial;
    private float? groundHeight;
    private string status = "Floor video is off.";
    private string occlusionStatus = string.Empty;

    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11Texture2D* texture;
    private ID3D11ShaderResourceView* view;
    private int textureWidth;
    private int textureHeight;
    private bool textureHasMips;
    private FloorRenderer? renderer;

    public VideoFloorController(IUiBuilder uiBuilder, Configuration config, Func<MediaClock?> audioClock)
    {
        this.uiBuilder = uiBuilder;
        this.config = config;
        this.audioClock = audioClock;
        uiBuilder.Draw += Draw;
    }

    public string Status
    {
        get
        {
            lock (sync)
                return decoder is null ? status : decoder.Status + occlusionStatus;
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
                // Never let the floor break the game's UI frame.
                StopDecoder();
                renderer?.Dispose();
                renderer = null;
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
        var mainViewport = ImGui.GetMainViewport();
        var viewportMin = mainViewport.Pos;
        var viewportSize = mainViewport.Size;
        var width = (int)viewportSize.X;
        var height = (int)viewportSize.Y;
        if (width <= 0 || height <= 0)
            return;

        var control = Control.Instance();
        if (control == null)
            return;
        var viewProjection = control->ViewProjectionMatrix;
        if (!TryGetCameraPosition(viewProjection, out var camera))
            return;

        var floorY = config.FloorMatchPlayerHeight && groundHeight is float ground ? ground : config.FloorHeight;
        BuildFloor(floorY + FloorLift);
        BuildUiMask(viewportMin, viewportSize);

        renderer ??= new FloorRenderer(device, context);

        ID3D11ShaderResourceView* sceneDepth = null;
        var depthA = 0f;
        var depthB = 0f;
        var useDepth = config.FloorBehindCharacters && TryGetSceneDepth(out sceneDepth, out depthA, out depthB);

        var constants = new FloorConstants
        {
            ViewProjection = viewProjection,
            CameraAndFloor = new Vector4(camera, floorY),
            Depth = new Vector4(depthA, depthB, OcclusionHeight, useDepth ? 1f : 0f),
            Output = new Vector4(width, height, Math.Clamp(config.FloorOpacity, 0.05f, 1f), 0f)
        };

        renderer.Render(view, useDepth ? sceneDepth : null,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(floorVertices),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(maskVertices),
            constants, width, height);

        ImGui.GetBackgroundDrawList().AddImage(new ImTextureID((nint)renderer.Output),
            viewportMin, viewportMin + viewportSize);
    }

    /// <summary>Builds the floor as a triangle list in world space.</summary>
    private void BuildFloor(float floorY)
    {
        floorVertices.Clear();
        var round = config.FloorRound;
        var columns = round ? RoundSegments : SquareCells;
        var rows = round ? RoundRings : SquareCells;

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

        FloorVertex Vertex(int row, int column)
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
            var world = new Vector3(config.FloorCenterX + x * cos - z * sin, floorY, config.FloorCenterZ + x * sin + z * cos);
            return new FloorVertex(world, new Vector2(0.5f + s * 2f * uHalf, 0.5f + t * 2f * vHalf));
        }

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var a = Vertex(row, column);
                var b = Vertex(row, column + 1);
                var c = Vertex(row + 1, column + 1);
                var d = Vertex(row + 1, column);
                floorVertices.Add(a);
                floorVertices.Add(b);
                floorVertices.Add(c);
                floorVertices.Add(a);
                floorVertices.Add(c);
                floorVertices.Add(d);
            }
        }
    }

    /// <summary>Collects every drawn piece of game UI as triangles to cut out of the floor.</summary>
    private void BuildUiMask(Vector2 viewportMin, Vector2 viewportSize)
    {
        maskVertices.Clear();
        if (!config.FloorKeepHudVisible)
            return;

        uiPieces.Clear();
        var screenArea = viewportSize.X * viewportSize.Y;
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
            return;

        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list.Count && uiPieces.Count < MaxUiPieces; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible || unit->RootNode == null)
                continue;
            AddDrawnParts(&unit->UldManager, uiPieces, screenArea, 0);
        }

        foreach (var piece in uiPieces)
        {
            // Screen pixels to clip space.
            var left = piece.X / viewportSize.X * 2f - 1f;
            var right = piece.Z / viewportSize.X * 2f - 1f;
            var top = 1f - piece.Y / viewportSize.Y * 2f;
            var bottom = 1f - piece.W / viewportSize.Y * 2f;
            maskVertices.Add(new Vector2(left, top));
            maskVertices.Add(new Vector2(right, top));
            maskVertices.Add(new Vector2(right, bottom));
            maskVertices.Add(new Vector2(left, top));
            maskVertices.Add(new Vector2(right, bottom));
            maskVertices.Add(new Vector2(left, bottom));
        }
    }

    /// <summary>Adds the screen rectangle of every image and text node that is actually drawn.</summary>
    private static void AddDrawnParts(AtkUldManager* uld, List<Vector4> pieces, float screenArea, int depth)
    {
        if (uld == null || uld->NodeList == null)
            return;
        for (var i = 0; i < uld->NodeListCount && pieces.Count < MaxUiPieces; i++)
        {
            var node = uld->NodeList[i];
            if (node == null)
                continue;

            if ((int)node->Type >= 1000)
            {
                // Component nodes (buttons, gauges, lists) keep their parts in their own node list.
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null && depth < 4)
                    AddDrawnParts(&component->UldManager, pieces, screenArea, depth + 1);
                continue;
            }

            if (node->Type is not (NodeType.Image or NodeType.NineGrid or NodeType.Text or NodeType.Counter))
                continue;
            if (node->Width == 0 || node->Height == 0 || !IsDrawn(node, out var scaleX, out var scaleY))
                continue;

            var left = node->ScreenX;
            var top = node->ScreenY;
            var right = left + node->Width * scaleX;
            var bottom = top + node->Height * scaleY;
            if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom))
                continue;
            // Full-screen layers (screen tints, nameplate canvases) would hide the whole floor.
            if ((right - left) * (bottom - top) > screenArea * 0.25f)
                continue;
            pieces.Add(new Vector4(left, top, right, bottom));
        }
    }

    /// <summary>A node is drawn only if it and all its parents are visible and not fully transparent.</summary>
    private static bool IsDrawn(AtkResNode* node, out float scaleX, out float scaleY)
    {
        scaleX = 1f;
        scaleY = 1f;
        for (var current = node; current != null; current = current->ParentNode)
        {
            if (!current->IsVisible() || current->Alpha_2 == 0)
                return false;
            scaleX *= current->ScaleX;
            scaleY *= current->ScaleY;
        }
        return scaleX > 0 && scaleY > 0;
    }

    /// <summary>
    /// Finds the game's scene depth buffer and how its values map to view distance:
    /// distance = B / (depth - A). This form covers standard and reverse-Z, finite or infinite far planes.
    /// </summary>
    private bool TryGetSceneDepth(out ID3D11ShaderResourceView* depth, out float a, out float b)
    {
        depth = null;
        a = 0f;
        b = 0f;

        var targets = RenderTargetManager.Instance();
        if (targets == null || targets->DepthStencil == null || targets->DepthStencil->D3D11ShaderResourceView == null)
        {
            occlusionStatus = " Characters cannot be kept in front: the game's depth buffer is unavailable.";
            return false;
        }

        var cameras = SceneCameraManager.Instance();
        var camera = cameras == null ? null : cameras->CurrentCamera;
        if (camera == null || camera->RenderCamera == null)
        {
            occlusionStatus = " Characters cannot be kept in front: the camera is unavailable.";
            return false;
        }

        var renderCamera = camera->RenderCamera;
        if (!TryDepthMapping(renderCamera->ProjectionMatrix, out a, out b) &&
            !TryDepthMapping(renderCamera->ProjectionMatrix2, out a, out b) &&
            !TryDepthMapping(Matrix4x4.Transpose(renderCamera->ProjectionMatrix), out a, out b) &&
            !TryDepthFromPlanes(renderCamera->NearPlane, renderCamera->FarPlane, renderCamera->StandardZ,
                renderCamera->FiniteFarPlane, out a, out b))
        {
            occlusionStatus = " Characters cannot be kept in front: the camera's depth range is unknown.";
            return false;
        }

        depth = (ID3D11ShaderResourceView*)targets->DepthStencil->D3D11ShaderResourceView;
        occlusionStatus = " Characters are drawn in front of the video.";
        return true;
    }

    /// <summary>Reads the depth mapping from a projection matrix by projecting two points in front of the camera.</summary>
    private static bool TryDepthMapping(Matrix4x4 projection, out float a, out float b)
    {
        a = 0f;
        b = 0f;
        if (!TryProjectDepth(projection, 2f, out var near) || !TryProjectDepth(projection, 50f, out var far))
            return false;
        // The scene buffer is reverse-Z: closer points have larger depth values.
        if (near <= far + 1e-6f || near > 1.001f || far < -0.001f)
            return false;
        b = (near - far) / (1f / 2f - 1f / 50f);
        a = near - b / 2f;
        return float.IsFinite(a) && float.IsFinite(b) && b > 0f;
    }

    private static bool TryProjectDepth(Matrix4x4 projection, float distance, out float depth)
    {
        foreach (var sign in new[] { 1f, -1f })
        {
            var clip = Vector4.Transform(new Vector4(0f, 0f, sign * distance, 1f), projection);
            if (MathF.Abs(clip.W - distance) < distance * 0.01f)
            {
                depth = clip.Z / clip.W;
                return float.IsFinite(depth);
            }
        }
        depth = 0f;
        return false;
    }

    private static bool TryDepthFromPlanes(float near, float far, bool standardZ, bool finiteFar, out float a, out float b)
    {
        a = 0f;
        b = 0f;
        if (!(near > 0f) || (finiteFar && !(far > near)))
            return false;
        if (!standardZ && finiteFar)
        {
            a = -near / (far - near);
            b = near * far / (far - near);
        }
        else if (!standardZ)
        {
            a = 0f;
            b = near;
        }
        else if (finiteFar)
        {
            a = far / (far - near);
            b = -near * far / (far - near);
        }
        else
        {
            a = 1f;
            b = -near;
        }
        return true;
    }

    /// <summary>The camera is the one point that projects to x = y = w = 0.</summary>
    internal static bool TryGetCameraPosition(Matrix4x4 m, out Vector3 position)
    {
        var system = new Matrix4x4(
            m.M11, m.M21, m.M31, 0f,
            m.M12, m.M22, m.M32, 0f,
            m.M14, m.M24, m.M34, 0f,
            0f, 0f, 0f, 1f);
        if (!Matrix4x4.Invert(system, out var inverse))
        {
            position = default;
            return false;
        }
        var solved = Vector4.Transform(new Vector4(-m.M41, -m.M42, -m.M44, 1f), Matrix4x4.Transpose(inverse));
        position = new Vector3(solved.X, solved.Y, solved.Z);
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
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
            renderer?.Dispose();
            renderer = null;
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
