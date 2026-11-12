// Blend the model's low-resolution color changes into the full input.
// Apply the delta rather than replacing fine detail with an upscaled image.
constant sampler_t point = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;
constant sampler_t linear = CLK_NORMALIZED_COORDS_TRUE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_LINEAR;

kernel void neural_residual(write_only image2d_t dst, uint frame,
    read_only image2d_t full, read_only image2d_t reference, read_only image2d_t neural)
{
    int2 pixel = (int2)(get_global_id(0), get_global_id(1));
    float2 uv = (convert_float2(pixel) + 0.5f) /
        convert_float2((int2)(get_image_width(full), get_image_height(full)));
    float4 original = read_imagef(full, point, pixel);
    float4 delta = read_imagef(neural, linear, uv) - read_imagef(reference, linear, uv);
    float4 adjusted = original + 0.8f * clamp(delta, (float4)(-0.32f), (float4)(0.32f));
    write_imagef(dst, pixel, clamp(adjusted, (float4)(0.0f), (float4)(1.0f)));
}
