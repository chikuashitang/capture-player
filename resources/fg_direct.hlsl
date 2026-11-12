// CapturePlayer prototype: screen-space interpolation from two NV12 frames.
// Flow is estimated once per input pair; all phases are presented on the GPU.
cbuffer FrameInfo : register(b0) {
    uint2 sourceSize;
    uint2 flowSize;
    float phase;
    float3 padding;
};

Texture2D<float> olderY : register(t0);
Texture2D<float> newerY : register(t1);
Texture2D<float2> olderUV : register(t2);
Texture2D<float2> newerUV : register(t3);
Texture2D<float4> flow : register(t4);
RWTexture2D<float4> flowOut : register(u0);
SamplerState bilinear : register(s0);

static const int Tile = 32;

float score(int2 center, int2 shift) {
    float sad = 0;
    const int2 edge = int2(sourceSize) - 1;
    [unroll] for (int y = -10; y <= 10; y += 10) {
        [unroll] for (int x = -10; x <= 10; x += 10) {
            int2 p = center + int2(x, y);
            sad += abs(olderY.Load(int3(clamp(p, 0, edge), 0)) -
                       newerY.Load(int3(clamp(p + shift, 0, edge), 0)));
        }
    }
    return sad * (1.0 / 9.0);
}

[numthreads(8, 8, 1)]
void Estimate(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= flowSize)) return;
    const int2 center = min(int2(id.xy) * Tile + Tile / 2, int2(sourceSize) - 1);
    float staticError = score(center, 0);
    float bestError = staticError;
    int2 best = 0;
    if (staticError > 0.009) {
        const int2 dirs[8] = {
            int2(-1,0), int2(1,0), int2(0,-1), int2(0,1),
            int2(-1,-1), int2(1,-1), int2(-1,1), int2(1,1)
        };
        [unroll] for (int step = 16; step > 0; step /= 2) {
            int2 origin = best;
            [unroll] for (int d = 0; d < 8; ++d) {
                int2 p = origin + dirs[d] * step;
                float value = score(center, p) + 0.00014 * (abs(p.x) + abs(p.y));
                if (value < bestError) {
                    best = p;
                    bestError = value;
                }
            }
        }
    }
    // Avoid dragging textures across a cut, an occlusion, or a poor match.
    float reliable = 1 - smoothstep(0.035, 0.14, bestError);
    flowOut[id.xy] = float4(float2(best), reliable, staticError);
}

struct Vertex {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

Vertex Fullscreen(uint id : SV_VertexID) {
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float3 rgb(float y, float2 uv) {
    y = (y * 255 - 16) / 219;
    uv = (uv * 255 - 128) / 224;
    return saturate(y + float3(1.5748 * uv.y, -0.1873 * uv.x - 0.4681 * uv.y, 1.8556 * uv.x));
}

float4 Composite(Vertex v) : SV_Target {
    if (phase == 0) return float4(rgb(olderY.SampleLevel(bilinear, v.uv, 0),
                                     olderUV.SampleLevel(bilinear, v.uv, 0)), 1);
    if (phase >= 1) return float4(rgb(newerY.SampleLevel(bilinear, v.uv, 0),
                                     newerUV.SampleLevel(bilinear, v.uv, 0)), 1);
    const float2 flowUv = v.uv * float2(sourceSize) / (float2(flowSize) * Tile);
    const float4 mv = flow.SampleLevel(bilinear, flowUv, 0);
    const float2 delta = mv.xy / float2(sourceSize);
    const float2 a = v.uv - phase * delta;
    const float2 b = v.uv + (1 - phase) * delta;
    const float4 first = float4(olderY.SampleLevel(bilinear, a, 0), olderUV.SampleLevel(bilinear, a, 0), 1);
    const float4 second = float4(newerY.SampleLevel(bilinear, b, 0), newerUV.SampleLevel(bilinear, b, 0), 1);
    const float4 unwarped = phase < .5
        ? float4(olderY.SampleLevel(bilinear, v.uv, 0), olderUV.SampleLevel(bilinear, v.uv, 0), 1)
        : float4(newerY.SampleLevel(bilinear, v.uv, 0), newerUV.SampleLevel(bilinear, v.uv, 0), 1);
    float4 mixed = lerp(unwarped, lerp(first, second, phase), mv.z);
    return float4(rgb(mixed.x, mixed.yz), 1);
}
