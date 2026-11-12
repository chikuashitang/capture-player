// Estimate local motion on 32-pixel tiles, then warp both adjacent frames.
// Estimate once per source frame, then render each fractional phase.
#define FLOW_TILE 32
constant sampler_t point = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;
constant sampler_t linear = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_LINEAR;

float match(read_only image2d_t prev, read_only image2d_t next, int2 p, int2 v)
{
    float error = 0.0f;
    int2 halfv = v / 2;
    for (int y = -10; y <= 10; y += 10)
        for (int x = -10; x <= 10; x += 10) {
            int2 pos = p + (int2)(x, y);
            float2 a = read_imagef(prev, point, pos - halfv).xy;
            float2 b = read_imagef(next, point, pos + v - halfv).xy;
            float2 delta = fabs(a - b);
            error += fmax(delta.x, delta.y);
        }
    return error * (1.0f / 9.0f);
}

kernel void flow(write_only image2d_t dst, uint frame,
                 read_only image2d_t next, read_only image2d_t prev)
{
    int2 tile = (int2)(get_global_id(0), get_global_id(1));
    // NV12 is processed one plane at a time. Pack signed X, Y and confidence
    // into three horizontal bands so even the single-channel luma plane works.
    int stride = get_image_width(dst) / 3;
    if (tile.x >= stride) return;
    int2 dx = (int2)(stride, 0);
    if (frame == 0u) {
        write_imagef(dst, tile, (float4)(128.0f / 255.0f));
        write_imagef(dst, tile + dx, (float4)(128.0f / 255.0f));
        write_imagef(dst, tile + 2 * dx, (float4)(0.0f));
        return;
    }
    int2 p = tile * FLOW_TILE + FLOW_TILE / 2;
    int2 best = 0;
    float zero = match(prev, next, p, best);
    float score = zero;
    if (zero > 0.007f) {
        // Four probes per level cap the cost of a full-screen camera pan.
        const int2 directions[4] = { (int2)(-1, 0), (int2)(1, 0), (int2)(0, -1), (int2)(0, 1) };
        for (int step = 16; step > 0; step /= 2) {
            int2 seed = best;
            for (int i = 0; i < 4; ++i) {
                int2 v = clamp(seed + directions[i] * step, (int2)(-31), (int2)(31));
                float e = match(prev, next, p, v) + 0.00055f * (float)(abs(v.x) + abs(v.y));
                if (e < score) { score = e; best = v; }
            }
        }
    }
    // Keep disocclusions and hard cuts on one source frame.
    float unreliable = smoothstep(0.045f, 0.13f, score);
    write_imagef(dst, tile, (float4)(((float)best.x + 128.0f) / 255.0f));
    write_imagef(dst, tile + dx, (float4)(((float)best.y + 128.0f) / 255.0f));
    write_imagef(dst, tile + 2 * dx, (float4)(unreliable));
}

void interpolate(write_only image2d_t dst, uint frame, uint multiplier,
                 read_only image2d_t next, read_only image2d_t prev,
                 read_only image2d_t vectors)
{
    int2 p = (int2)(get_global_id(0), get_global_id(1));
    float4 current = read_imagef(next, point, p);
    uint phase = frame % multiplier + 1u;
    if (phase == multiplier || frame < multiplier) {
        write_imagef(dst, p, current);
        return;
    }
    float2 center = convert_float2(p) + 0.5f;
    float stride = (float)(get_image_width(vectors) / 3);
    float2 tile = clamp(center / (float)FLOW_TILE, (float2)(0.5f),
                        (float2)(stride - 0.5f, (float)get_image_height(vectors) - 0.5f));
    float2 dx = (float2)(stride, 0.0f);
    float3 m = (float3)(read_imagef(vectors, linear, tile).x,
                        read_imagef(vectors, linear, tile + dx).x,
                        read_imagef(vectors, linear, tile + 2.0f * dx).x);
    float t = (float)phase / (float)multiplier;
    float2 motion = m.xy * 255.0f - 128.0f;
    float4 a = read_imagef(prev, linear, center - t * motion);
    float4 b = read_imagef(next, linear, center + (1.0f - t) * motion);
    float2 delta = fabs(a.xy - b.xy);
    float mismatch = fmax(delta.x, delta.y);
    float reject = fmax(m.z, smoothstep(0.06f, 0.18f, mismatch));
    write_imagef(dst, p, mix(mix(a, b, t), current, reject));
}

#define PHASE_KERNEL(n) \
kernel void phase##n(write_only image2d_t dst, uint frame, \
                     read_only image2d_t next, read_only image2d_t prev, \
                     read_only image2d_t vectors) { interpolate(dst, frame, n, next, prev, vectors); }
PHASE_KERNEL(2)
PHASE_KERNEL(3)
PHASE_KERNEL(4)
PHASE_KERNEL(5)
PHASE_KERNEL(6)
