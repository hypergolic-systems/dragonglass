//! Un-premultiply pass for CEF's accelerated-paint output.
//!
//! CEF's OSR shared texture on Windows is premultiplied BGRA
//! (`CEF_ALPHA_TYPE_PREMULTIPLIED` — no accelerated-paint opt-out),
//! but Unity's default UGUI shader (`Blend SrcAlpha OneMinusSrcAlpha`)
//! expects straight alpha. Feeding premul content directly to that
//! blend equation darkens every pixel by a factor of its own alpha,
//! which shows up as a uniform gray wash over KSP's scene. On macOS
//! this never bit us because the IOSurface path emits straight alpha.
//!
//! This pass runs a fullscreen triangle inside `blit_from_cef`: it
//! samples the CEF texture (which was just `CopyResource`-d into a
//! stage texture) and writes the un-premultiplied result into the
//! canvas render target. After the pass, canvas pixels are straight
//! alpha, so the plugin's render thread can hand them to Unity's
//! `UI/Default` shader without any further adjustment.

use windows::Win32::Graphics::Direct3D::Fxc::{D3DCompile, D3DCOMPILE_OPTIMIZATION_LEVEL3};
use windows::Win32::Graphics::Direct3D::{
    ID3DBlob, D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11BlendState, ID3D11Device1, ID3D11DeviceContext, ID3D11PixelShader,
    ID3D11RasterizerState, ID3D11RenderTargetView, ID3D11SamplerState, ID3D11ShaderResourceView,
    ID3D11VertexShader, D3D11_BLEND_DESC, D3D11_BLEND_ONE, D3D11_BLEND_OP_ADD, D3D11_BLEND_ZERO,
    D3D11_COLOR_WRITE_ENABLE_ALL, D3D11_COMPARISON_NEVER, D3D11_CULL_NONE, D3D11_FILL_SOLID,
    D3D11_FILTER_MIN_MAG_MIP_POINT, D3D11_RASTERIZER_DESC, D3D11_RENDER_TARGET_BLEND_DESC,
    D3D11_SAMPLER_DESC, D3D11_TEXTURE_ADDRESS_CLAMP, D3D11_VIEWPORT,
};

/// HLSL for a fullscreen-triangle un-premultiply copy. The vertex
/// shader derives a three-vert triangle covering the viewport from
/// `SV_VertexID`; the pixel shader samples the stage texture and
/// divides RGB by A (clamped to avoid overflow on slightly-greater-
/// than-A premultiplied values from premultiplication rounding).
const HLSL: &str = r#"
Texture2D<float4> Source : register(t0);
SamplerState Samp : register(s0);

struct VsOut {
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOut VS(uint id : SV_VertexID) {
    VsOut o;
    // 0 -> (0,0), 1 -> (2,0), 2 -> (0,2). Covers the [0,1]x[0,1] UV
    // range via a triangle that extends one screen's worth past
    // the viewport on the right and bottom.
    o.uv = float2(float((id << 1) & 2), float(id & 2));
    // Map UV [0,1] to clip-space [-1,1] with Y flipped (D3D11 has
    // origin top-left in UV, clip-space has origin bottom-left).
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PS(VsOut i) : SV_Target {
    float4 c = Source.Sample(Samp, i.uv);
    // Un-premultiply: straight.rgb = premul.rgb / alpha.
    // Premultiplied invariant guarantees rgb <= a, but rounding
    // during the premultiply can make rgb exceed a very slightly;
    // saturate() caps the result at 1.0 so rounding noise doesn't
    // produce values > 1 that the straight-alpha blend would then
    // re-multiply into an over-bright pixel.
    if (c.a > 0.0) {
        c.rgb = saturate(c.rgb / c.a);
    } else {
        c.rgb = float3(0, 0, 0);
    }
    return c;
}
"#;

pub struct UnpremulPass {
    vs: ID3D11VertexShader,
    ps: ID3D11PixelShader,
    sampler: ID3D11SamplerState,
    blend: ID3D11BlendState,
    rasterizer: ID3D11RasterizerState,
}

impl UnpremulPass {
    pub fn create(device: &ID3D11Device1) -> Result<Self, String> {
        let vs_blob = compile("VS", "vs_5_0").map_err(|e| format!("unpremul VS compile: {e}"))?;
        let ps_blob = compile("PS", "ps_5_0").map_err(|e| format!("unpremul PS compile: {e}"))?;

        let vs = unsafe {
            let mut out: Option<ID3D11VertexShader> = None;
            device
                .CreateVertexShader(blob_slice(&vs_blob), None, Some(&mut out))
                .map_err(|e| format!("CreateVertexShader: {e}"))?;
            out.ok_or("CreateVertexShader returned none")?
        };
        let ps = unsafe {
            let mut out: Option<ID3D11PixelShader> = None;
            device
                .CreatePixelShader(blob_slice(&ps_blob), None, Some(&mut out))
                .map_err(|e| format!("CreatePixelShader: {e}"))?;
            out.ok_or("CreatePixelShader returned none")?
        };

        // Point filtering — canvas and stage are always the same size
        // and the fullscreen triangle maps 1:1 in UV, so linear
        // filtering would just burn cycles on pixel-accurate data.
        let sampler_desc = D3D11_SAMPLER_DESC {
            Filter: D3D11_FILTER_MIN_MAG_MIP_POINT,
            AddressU: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW: D3D11_TEXTURE_ADDRESS_CLAMP,
            MipLODBias: 0.0,
            MaxAnisotropy: 1,
            ComparisonFunc: D3D11_COMPARISON_NEVER,
            BorderColor: [0.0; 4],
            MinLOD: 0.0,
            MaxLOD: 0.0,
        };
        let sampler = unsafe {
            let mut out: Option<ID3D11SamplerState> = None;
            device
                .CreateSamplerState(&sampler_desc, Some(&mut out))
                .map_err(|e| format!("CreateSamplerState: {e}"))?;
            out.ok_or("CreateSamplerState returned none")?
        };

        // No blending — we overwrite the canvas with the straight-alpha
        // output of the pixel shader.
        let blend_desc = D3D11_BLEND_DESC {
            AlphaToCoverageEnable: false.into(),
            IndependentBlendEnable: false.into(),
            RenderTarget: [D3D11_RENDER_TARGET_BLEND_DESC {
                BlendEnable: false.into(),
                SrcBlend: D3D11_BLEND_ONE,
                DestBlend: D3D11_BLEND_ZERO,
                BlendOp: D3D11_BLEND_OP_ADD,
                SrcBlendAlpha: D3D11_BLEND_ONE,
                DestBlendAlpha: D3D11_BLEND_ZERO,
                BlendOpAlpha: D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask: D3D11_COLOR_WRITE_ENABLE_ALL.0 as u8,
            }; 8],
        };
        let blend = unsafe {
            let mut out: Option<ID3D11BlendState> = None;
            device
                .CreateBlendState(&blend_desc, Some(&mut out))
                .map_err(|e| format!("CreateBlendState: {e}"))?;
            out.ok_or("CreateBlendState returned none")?
        };

        let rast_desc = D3D11_RASTERIZER_DESC {
            FillMode: D3D11_FILL_SOLID,
            CullMode: D3D11_CULL_NONE,
            FrontCounterClockwise: false.into(),
            DepthBias: 0,
            DepthBiasClamp: 0.0,
            SlopeScaledDepthBias: 0.0,
            DepthClipEnable: true.into(),
            ScissorEnable: false.into(),
            MultisampleEnable: false.into(),
            AntialiasedLineEnable: false.into(),
        };
        let rasterizer = unsafe {
            let mut out: Option<ID3D11RasterizerState> = None;
            device
                .CreateRasterizerState(&rast_desc, Some(&mut out))
                .map_err(|e| format!("CreateRasterizerState: {e}"))?;
            out.ok_or("CreateRasterizerState returned none")?
        };

        Ok(Self {
            vs,
            ps,
            sampler,
            blend,
            rasterizer,
        })
    }

    /// Draw the fullscreen triangle that samples `stage_srv` and
    /// writes un-premultiplied pixels into `canvas_rtv`. Width/height
    /// define the viewport (both textures should be this size).
    pub fn draw(
        &self,
        context: &ID3D11DeviceContext,
        stage_srv: &ID3D11ShaderResourceView,
        canvas_rtv: &ID3D11RenderTargetView,
        width: u32,
        height: u32,
    ) {
        let viewport = D3D11_VIEWPORT {
            TopLeftX: 0.0,
            TopLeftY: 0.0,
            Width: width as f32,
            Height: height as f32,
            MinDepth: 0.0,
            MaxDepth: 1.0,
        };
        unsafe {
            // No input layout, no vertex/index buffer — the VS uses
            // SV_VertexID to build the triangle procedurally.
            context.IASetInputLayout(None);
            context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context.VSSetShader(&self.vs, None);
            context.PSSetShader(&self.ps, None);
            context.PSSetShaderResources(0, Some(&[Some(stage_srv.clone())]));
            context.PSSetSamplers(0, Some(&[Some(self.sampler.clone())]));
            context.RSSetState(&self.rasterizer);
            context.RSSetViewports(Some(&[viewport]));
            context.OMSetBlendState(&self.blend, None, 0xffff_ffff);
            context.OMSetRenderTargets(Some(&[Some(canvas_rtv.clone())]), None);
            context.Draw(3, 0);
            // Unbind so the stage texture isn't held as an SRV on the
            // next frame when we're copying into it again.
            context.PSSetShaderResources(0, Some(&[None]));
            context.OMSetRenderTargets(Some(&[None]), None);
        }
    }
}

fn compile(entry: &str, target: &str) -> Result<ID3DBlob, String> {
    // Null-terminate ASCII strings for D3DCompile's PCSTR args.
    let entry_c = std::ffi::CString::new(entry).map_err(|e| e.to_string())?;
    let target_c = std::ffi::CString::new(target).map_err(|e| e.to_string())?;
    let mut blob: Option<ID3DBlob> = None;
    let mut errors: Option<ID3DBlob> = None;
    let hr = unsafe {
        D3DCompile(
            HLSL.as_ptr() as *const _,
            HLSL.len(),
            windows::core::PCSTR::null(),
            None,
            None,
            windows::core::PCSTR(entry_c.as_ptr() as *const u8),
            windows::core::PCSTR(target_c.as_ptr() as *const u8),
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            &mut blob,
            Some(&mut errors),
        )
    };
    if hr.is_err() {
        let msg = errors
            .as_ref()
            .map(|e| unsafe {
                let p = e.GetBufferPointer() as *const u8;
                let n = e.GetBufferSize();
                std::str::from_utf8(std::slice::from_raw_parts(p, n))
                    .unwrap_or("<invalid utf8>")
                    .to_string()
            })
            .unwrap_or_else(|| format!("hr={hr:?}"));
        return Err(msg);
    }
    blob.ok_or_else(|| "D3DCompile returned no blob".into())
}

fn blob_slice(blob: &ID3DBlob) -> &[u8] {
    unsafe {
        std::slice::from_raw_parts(
            blob.GetBufferPointer() as *const u8,
            blob.GetBufferSize(),
        )
    }
}

