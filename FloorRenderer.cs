using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace DMUModelScale;

[StructLayout(LayoutKind.Sequential)]
internal struct FloorVertex(Vector3 position, Vector2 uv)
{
    public Vector3 Position = position;
    public Vector2 Uv = uv;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FloorConstants
{
    /// <summary>The game's world-to-clip matrix, row-vector convention.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>xyz: camera position, w: height of the floor plane.</summary>
    public Vector4 CameraAndFloor;

    /// <summary>
    /// Scene depth d maps to view distance B / (d - A). x: A, y: B, z: occlusion height (floor) or
    /// distance tolerance (screen), w: 1 if depth is usable.
    /// </summary>
    public Vector4 Depth;

    /// <summary>xy: output size in pixels, z: opacity, w: 1 for an upright screen, 0 for the floor.</summary>
    public Vector4 Output;
}

/// <summary>
/// Draws the floor video into an offscreen texture with Direct3D 11.
/// Pixels where the game's depth buffer shows something standing on the floor are left empty,
/// so characters stay in front of the video. Game UI pieces are cut out afterwards.
/// </summary>
internal sealed unsafe class FloorRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Floor : register(b0)
        {
            row_major float4x4 ViewProjection;
            float4 CameraAndFloor;
            float4 Depth;
            float4 Output;
        };

        Texture2D Video : register(t0);
        Texture2D<float> SceneDepth : register(t1);
        SamplerState VideoSampler : register(s0);
        SamplerState DepthSampler : register(s1);

        struct VsIn
        {
            float3 position : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct PsIn
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
            float3 world : TEXCOORD1;
            float viewDistance : TEXCOORD2;
        };

        PsIn FloorVs(VsIn input)
        {
            PsIn output;
            float4 clip = mul(float4(input.position, 1.0), ViewProjection);
            // Depth is compared in the pixel shader, so keep z inside the range and let
            // the hardware clip only at the camera plane.
            output.position = float4(clip.xy, clip.w * 0.5, clip.w);
            output.uv = input.uv;
            output.world = input.position;
            output.viewDistance = clip.w;
            return output;
        }

        float4 FloorPs(PsIn input) : SV_Target
        {
            if (Depth.w > 0.5)
            {
                float2 screenUv = input.position.xy / Output.xy;
                float sceneValue = SceneDepth.SampleLevel(DepthSampler, screenUv, 0);
                float denominator = sceneValue - Depth.x;
                if (abs(denominator) > 1e-8)
                {
                    float sceneDistance = Depth.y / denominator;
                    if (Output.w > 0.5)
                    {
                        // Upright screen: hide it wherever anything is clearly closer to the camera.
                        if (sceneDistance > 0.0 && sceneDistance < input.viewDistance - Depth.z)
                            discard;
                    }
                    else if (sceneDistance > 0.0 && sceneDistance < input.viewDistance)
                    {
                        // Something is in front of the floor on this pixel. Find how high above the
                        // floor it is: points on the same view ray scale with view distance.
                        float ratio = sceneDistance / input.viewDistance;
                        float sceneHeight = CameraAndFloor.y + (input.world.y - CameraAndFloor.y) * ratio;
                        if (sceneHeight > CameraAndFloor.w + Depth.z)
                            discard;
                    }
                }
            }

            float3 color = Video.Sample(VideoSampler, input.uv).rgb;
            return float4(color, Output.z);
        }

        float4 MaskVs(float2 position : POSITION) : SV_Position
        {
            return float4(position, 0.5, 1.0);
        }

        float4 MaskPs() : SV_Target
        {
            return float4(0.0, 0.0, 0.0, 0.0);
        }
        """;

    private const uint OptimizationLevel3 = 1 << 15;

    private readonly ID3D11Device* device;
    private readonly ID3D11DeviceContext* context;

    private ID3D11VertexShader* floorVs;
    private ID3D11PixelShader* floorPs;
    private ID3D11InputLayout* floorLayout;
    private ID3D11VertexShader* maskVs;
    private ID3D11PixelShader* maskPs;
    private ID3D11InputLayout* maskLayout;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* floorVertices;
    private int floorCapacity;
    private ID3D11Buffer* maskVertices;
    private int maskCapacity;
    private ID3D11SamplerState* videoSampler;
    private ID3D11SamplerState* depthSampler;
    private ID3D11RasterizerState* rasterizer;
    private ID3D11Texture2D* target;
    private ID3D11RenderTargetView* targetRtv;
    private ID3D11ShaderResourceView* targetSrv;
    private int targetWidth;
    private int targetHeight;

    public FloorRenderer(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        this.device = device;
        this.context = context;
        try
        {
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The rendered floor, ready to draw over the whole screen.</summary>
    public ID3D11ShaderResourceView* Output => targetSrv;

    public void Render(ID3D11ShaderResourceView* video, ID3D11ShaderResourceView* sceneDepth,
        ReadOnlySpan<FloorVertex> floor, ReadOnlySpan<Vector2> mask, in FloorConstants values, int width, int height)
    {
        EnsureTarget(width, height);
        Upload(ref floorVertices, ref floorCapacity, floor, sizeof(FloorVertex));
        if (mask.Length > 0)
            Upload(ref maskVertices, ref maskCapacity, mask, sizeof(Vector2));
        UploadConstants(values);

        var saved = new SavedState(context);
        try
        {
            var rtv = targetRtv;
            context->OMSetRenderTargets(1, &rtv, null);
            var viewport = new D3D11_VIEWPORT { TopLeftX = 0, TopLeftY = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
            context->RSSetViewports(1, &viewport);
            var clear = stackalloc float[4] { 0, 0, 0, 0 };
            context->ClearRenderTargetView(targetRtv, clear);

            context->RSSetState(rasterizer);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(null, 0);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

            var cb = constants;
            context->VSSetConstantBuffers(0, 1, &cb);
            context->PSSetConstantBuffers(0, 1, &cb);
            var views = stackalloc ID3D11ShaderResourceView*[2] { video, sceneDepth };
            context->PSSetShaderResources(0, 2, views);
            var samplers = stackalloc ID3D11SamplerState*[2] { videoSampler, depthSampler };
            context->PSSetSamplers(0, 2, samplers);

            // Pass 1: the floor, with characters left uncovered.
            var buffer = floorVertices;
            var stride = (uint)sizeof(FloorVertex);
            var offset = 0u;
            context->IASetInputLayout(floorLayout);
            context->IASetVertexBuffers(0, 1, &buffer, &stride, &offset);
            context->VSSetShader(floorVs, null, 0);
            context->PSSetShader(floorPs, null, 0);
            context->Draw((uint)floor.Length, 0);

            // Pass 2: clear the floor wherever game UI is drawn.
            if (mask.Length > 0)
            {
                buffer = maskVertices;
                stride = (uint)sizeof(Vector2);
                context->IASetInputLayout(maskLayout);
                context->IASetVertexBuffers(0, 1, &buffer, &stride, &offset);
                context->VSSetShader(maskVs, null, 0);
                context->PSSetShader(maskPs, null, 0);
                context->Draw((uint)mask.Length, 0);
            }

            // Unbind the inputs so the depth buffer is free for the game again.
            var none = stackalloc ID3D11ShaderResourceView*[2] { null, null };
            context->PSSetShaderResources(0, 2, none);
        }
        finally
        {
            saved.Restore(context);
        }
    }

    private void CreatePipeline()
    {
        using var floorVsCode = Compile("FloorVs", "vs_5_0");
        using var floorPsCode = Compile("FloorPs", "ps_5_0");
        using var maskVsCode = Compile("MaskVs", "vs_5_0");
        using var maskPsCode = Compile("MaskPs", "ps_5_0");

        fixed (ID3D11VertexShader** shader = &floorVs)
            Check(device->CreateVertexShader(floorVsCode.Pointer, floorVsCode.Size, null, shader), "create the floor shader");
        fixed (ID3D11PixelShader** shader = &floorPs)
            Check(device->CreatePixelShader(floorPsCode.Pointer, floorPsCode.Size, null, shader), "create the floor shader");
        fixed (ID3D11VertexShader** shader = &maskVs)
            Check(device->CreateVertexShader(maskVsCode.Pointer, maskVsCode.Size, null, shader), "create the UI mask shader");
        fixed (ID3D11PixelShader** shader = &maskPs)
            Check(device->CreatePixelShader(maskPsCode.Pointer, maskPsCode.Size, null, shader), "create the UI mask shader");

        var position = Encoding.ASCII.GetBytes("POSITION\0");
        var texcoord = Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* positionName = position)
        fixed (byte* texcoordName = texcoord)
        {
            var floorElements = stackalloc D3D11_INPUT_ELEMENT_DESC[2];
            floorElements[0] = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = (sbyte*)positionName,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                AlignedByteOffset = 0,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA
            };
            floorElements[1] = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = (sbyte*)texcoordName,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                AlignedByteOffset = 12,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA
            };
            fixed (ID3D11InputLayout** layout = &floorLayout)
                Check(device->CreateInputLayout(floorElements, 2, floorVsCode.Pointer, floorVsCode.Size, layout), "create the floor vertex layout");

            var maskElement = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = (sbyte*)positionName,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                AlignedByteOffset = 0,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA
            };
            fixed (ID3D11InputLayout** layout = &maskLayout)
                Check(device->CreateInputLayout(&maskElement, 1, maskVsCode.Pointer, maskVsCode.Size, layout), "create the UI mask vertex layout");
        }

        var constantsDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)((sizeof(FloorConstants) + 15) & ~15),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE
        };
        fixed (ID3D11Buffer** buffer = &constants)
            Check(device->CreateBuffer(&constantsDesc, null, buffer), "create the floor settings buffer");

        // Anisotropic filtering with mipmaps keeps the video sharp at the low angles a floor is seen from.
        var videoDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_ANISOTROPIC,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxAnisotropy = 16,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        };
        fixed (ID3D11SamplerState** sampler = &videoSampler)
            Check(device->CreateSamplerState(&videoDesc, sampler), "create the video sampler");

        var depthDesc = videoDesc;
        depthDesc.Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT;
        depthDesc.MaxAnisotropy = 1;
        fixed (ID3D11SamplerState** sampler = &depthSampler)
            Check(device->CreateSamplerState(&depthDesc, sampler), "create the depth sampler");

        var rasterizerDesc = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = true
        };
        fixed (ID3D11RasterizerState** state = &rasterizer)
            Check(device->CreateRasterizerState(&rasterizerDesc, state), "create the rasterizer state");
    }

    private ShaderCode Compile(string entryPoint, string profile)
    {
        var source = Encoding.ASCII.GetBytes(ShaderSource);
        var entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var target = Encoding.ASCII.GetBytes(profile + "\0");
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        HRESULT result;
        fixed (byte* sourcePointer = source)
        fixed (byte* entryPointer = entry)
        fixed (byte* targetPointer = target)
            result = D3DCompile(sourcePointer, (nuint)source.Length, null, null, null,
                (sbyte*)entryPointer, (sbyte*)targetPointer, OptimizationLevel3, 0, &code, &errors);

        string? message = null;
        if (errors != null)
        {
            message = Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize())?.Trim('\0', ' ', '\r', '\n');
            errors->Release();
        }
        if (result.FAILED || code == null)
        {
            if (code != null)
                code->Release();
            throw new InvalidOperationException($"Could not compile {entryPoint}: {message ?? $"0x{result.Value:X8}"}");
        }
        return new ShaderCode(code);
    }

    private void EnsureTarget(int width, int height)
    {
        if (target != null && width == targetWidth && height == targetHeight)
            return;
        ReleaseTarget();

        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE)
        };
        fixed (ID3D11Texture2D** texture = &target)
            Check(device->CreateTexture2D(&description, null, texture), "create the floor target");
        fixed (ID3D11RenderTargetView** view = &targetRtv)
            Check(device->CreateRenderTargetView((ID3D11Resource*)target, null, view), "create the floor target view");
        fixed (ID3D11ShaderResourceView** view = &targetSrv)
            Check(device->CreateShaderResourceView((ID3D11Resource*)target, null, view), "create the floor target view");
        targetWidth = width;
        targetHeight = height;
    }

    private void Upload<T>(ref ID3D11Buffer* buffer, ref int capacity, ReadOnlySpan<T> data, int elementSize) where T : unmanaged
    {
        if (buffer == null || capacity < data.Length)
        {
            if (buffer != null)
                buffer->Release();
            buffer = null;
            capacity = Math.Max(data.Length, Math.Max(capacity * 2, 1024));
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(capacity * elementSize),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE
            };
            ID3D11Buffer* created = null;
            Check(device->CreateBuffer(&description, null, &created), "create a vertex buffer");
            buffer = created;
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "write vertices");
        fixed (T* source = data)
            Buffer.MemoryCopy(source, mapped.pData, (long)capacity * elementSize, (long)data.Length * elementSize);
        context->Unmap((ID3D11Resource*)buffer, 0);
    }

    private void UploadConstants(in FloorConstants values)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "write floor settings");
        *(FloorConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)constants, 0);
    }

    private void ReleaseTarget()
    {
        Release(ref targetSrv);
        Release(ref targetRtv);
        Release(ref target);
        targetWidth = 0;
        targetHeight = 0;
    }

    private static void Release<T>(ref T* pointer) where T : unmanaged
    {
        if (pointer != null)
            ((IUnknown*)pointer)->Release();
        pointer = null;
    }

    private static void Check(HRESULT result, string action)
    {
        if (result.FAILED)
            throw new InvalidOperationException($"Could not {action} (0x{result.Value:X8}).");
    }

    public void Dispose()
    {
        ReleaseTarget();
        Release(ref floorVs);
        Release(ref floorPs);
        Release(ref floorLayout);
        Release(ref maskVs);
        Release(ref maskPs);
        Release(ref maskLayout);
        Release(ref constants);
        Release(ref floorVertices);
        Release(ref maskVertices);
        Release(ref videoSampler);
        Release(ref depthSampler);
        Release(ref rasterizer);
    }

    private readonly struct ShaderCode(ID3DBlob* blob) : IDisposable
    {
        public void* Pointer => blob->GetBufferPointer();
        public nuint Size => blob->GetBufferSize();
        public void Dispose() => blob->Release();
    }

    /// <summary>Everything this renderer changes on the device context, restored afterwards.</summary>
    private struct SavedState
    {
        private ID3D11RenderTargetView* rtv;
        private ID3D11DepthStencilView* dsv;
        private D3D11_VIEWPORT viewport;
        private uint viewportCount;
        private ID3D11RasterizerState* rasterizer;
        private ID3D11BlendState* blend;
        private fixed float blendFactor[4];
        private uint sampleMask;
        private ID3D11DepthStencilState* depthStencil;
        private uint stencilRef;
        private D3D_PRIMITIVE_TOPOLOGY topology;
        private ID3D11InputLayout* layout;
        private ID3D11Buffer* vertexBuffer;
        private uint vertexStride;
        private uint vertexOffset;
        private ID3D11VertexShader* vertexShader;
        private ID3D11PixelShader* pixelShader;
        private ID3D11Buffer* vsConstants;
        private ID3D11Buffer* psConstants;
        private ID3D11ShaderResourceView* psView0;
        private ID3D11ShaderResourceView* psView1;
        private ID3D11SamplerState* psSampler0;
        private ID3D11SamplerState* psSampler1;

        public SavedState(ID3D11DeviceContext* context)
        {
            fixed (SavedState* self = &this)
            {
                context->OMGetRenderTargets(1, &self->rtv, &self->dsv);
                self->viewportCount = 1;
                context->RSGetViewports(&self->viewportCount, &self->viewport);
                context->RSGetState(&self->rasterizer);
                context->OMGetBlendState(&self->blend, self->blendFactor, &self->sampleMask);
                context->OMGetDepthStencilState(&self->depthStencil, &self->stencilRef);
                context->IAGetPrimitiveTopology(&self->topology);
                context->IAGetInputLayout(&self->layout);
                context->IAGetVertexBuffers(0, 1, &self->vertexBuffer, &self->vertexStride, &self->vertexOffset);
                context->VSGetShader(&self->vertexShader, null, null);
                context->PSGetShader(&self->pixelShader, null, null);
                context->VSGetConstantBuffers(0, 1, &self->vsConstants);
                context->PSGetConstantBuffers(0, 1, &self->psConstants);
                context->PSGetShaderResources(0, 1, &self->psView0);
                context->PSGetShaderResources(1, 1, &self->psView1);
                context->PSGetSamplers(0, 1, &self->psSampler0);
                context->PSGetSamplers(1, 1, &self->psSampler1);
            }
        }

        public void Restore(ID3D11DeviceContext* context)
        {
            fixed (SavedState* self = &this)
            {
                context->OMSetRenderTargets(1, &self->rtv, self->dsv);
                if (self->viewportCount > 0)
                    context->RSSetViewports(self->viewportCount, &self->viewport);
                context->RSSetState(self->rasterizer);
                context->OMSetBlendState(self->blend, self->blendFactor, self->sampleMask);
                context->OMSetDepthStencilState(self->depthStencil, self->stencilRef);
                context->IASetPrimitiveTopology(self->topology);
                context->IASetInputLayout(self->layout);
                context->IASetVertexBuffers(0, 1, &self->vertexBuffer, &self->vertexStride, &self->vertexOffset);
                context->VSSetShader(self->vertexShader, null, 0);
                context->PSSetShader(self->pixelShader, null, 0);
                context->VSSetConstantBuffers(0, 1, &self->vsConstants);
                context->PSSetConstantBuffers(0, 1, &self->psConstants);
                context->PSSetShaderResources(0, 1, &self->psView0);
                context->PSSetShaderResources(1, 1, &self->psView1);
                context->PSSetSamplers(0, 1, &self->psSampler0);
                context->PSSetSamplers(1, 1, &self->psSampler1);

                // The Get calls above added references; drop them now that the state is back.
                Release(ref self->rtv);
                Release(ref self->dsv);
                Release(ref self->rasterizer);
                Release(ref self->blend);
                Release(ref self->depthStencil);
                Release(ref self->layout);
                Release(ref self->vertexBuffer);
                Release(ref self->vertexShader);
                Release(ref self->pixelShader);
                Release(ref self->vsConstants);
                Release(ref self->psConstants);
                Release(ref self->psView0);
                Release(ref self->psView1);
                Release(ref self->psSampler0);
                Release(ref self->psSampler1);
            }
        }
    }
}
