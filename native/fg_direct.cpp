// D3D11 frame generation probe: no encoding, decoding, or GPU readback on
// the presentation path. FG_BENCH enables an offscreen timing executable.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_5.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;

static void require(HRESULT hr, const char* message) {
    if (FAILED(hr)) {
        std::fprintf(stderr, "%s (0x%08lx)\n", message, static_cast<unsigned long>(hr));
        throw std::runtime_error(message);
    }
}

struct Texture {
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11ShaderResourceView> srv;
};

struct Params {
    UINT sourceW{}, sourceH{}, flowW{}, flowH{};
    float phase{};
    float padding[3]{};
};

class DirectFg {
    struct Frame { Texture y, uv; };
    Frame frames[2];
    Texture vectors;
    ComPtr<ID3D11UnorderedAccessView> flowUav;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11ComputeShader> cs;
    ComPtr<ID3D11Buffer> constant;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<IDXGISwapChain2> swap;
    HANDLE waitable{};
    UINT outputW{}, outputH{}, newest{}, frameCount{};
    UINT swapFlags{};
    Params params{};

    Texture texture(UINT w, UINT h, DXGI_FORMAT format, UINT bind) {
        D3D11_TEXTURE2D_DESC d{};
        d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1;
        d.Format = format; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = bind;
        Texture t;
        require(device->CreateTexture2D(&d, nullptr, t.tex.GetAddressOf()), "create texture");
        if (bind & D3D11_BIND_SHADER_RESOURCE)
            require(device->CreateShaderResourceView(t.tex.Get(), nullptr, t.srv.GetAddressOf()), "create texture view");
        return t;
    }

    void createTarget(UINT w, UINT h) {
        outputW = w; outputH = h;
        if (swap) require(swap->GetBuffer(0, IID_PPV_ARGS(target.GetAddressOf())), "get swap buffer");
        else target = texture(w, h, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_RENDER_TARGET).tex;
        require(device->CreateRenderTargetView(target.Get(), nullptr, rtv.GetAddressOf()), "create render target view");
    }

    static ComPtr<ID3DBlob> shader(const wchar_t* path, const char* entry, const char* profile) {
        ComPtr<ID3DBlob> code, messages;
        HRESULT hr = D3DCompileFromFile(path, nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0, code.GetAddressOf(), messages.GetAddressOf());
        if (FAILED(hr) && messages) std::fprintf(stderr, "%s", static_cast<const char*>(messages->GetBufferPointer()));
        require(hr, "compile shader");
        return code;
    }

public:
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<ID3D11Texture2D> target;
    ComPtr<ID3D11RenderTargetView> rtv;
    DXGI_ADAPTER_DESC1 adapterInfo{};

    DirectFg(HWND window, UINT w, UINT h, UINT outW, UINT outH, const wchar_t* shaderPath) {
        if (w == 0 || h == 0 || ((w | h) & 1) || w > 7680 || h > 4320 || !outW || !outH)
            throw std::runtime_error("invalid video size");
        ComPtr<IDXGIFactory2> factory;
        require(CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf())), "create DXGI factory");
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0; ; ++i) {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(i, candidate.GetAddressOf()) != S_OK) break;
            DXGI_ADAPTER_DESC1 desc{};
            if (SUCCEEDED(candidate->GetDesc1(&desc)) && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) {
                if (!adapter || desc.VendorId == 0x10de) { adapter = candidate; adapterInfo = desc; }
                if (desc.VendorId == 0x10de) break;
            }
        }
        if (!adapter) throw std::runtime_error("no hardware adapter");
        const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
        require(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 1, D3D11_SDK_VERSION, device.GetAddressOf(), nullptr, ctx.GetAddressOf()), "create D3D11 device");

        params.sourceW = w; params.sourceH = h; params.flowW = (w + 31) / 32; params.flowH = (h + 31) / 32;
        for (auto& frame : frames) {
            frame.y = texture(w, h, DXGI_FORMAT_R8_UNORM, D3D11_BIND_SHADER_RESOURCE);
            frame.uv = texture(w / 2, h / 2, DXGI_FORMAT_R8G8_UNORM, D3D11_BIND_SHADER_RESOURCE);
        }
        vectors = texture(params.flowW, params.flowH, DXGI_FORMAT_R16G16B16A16_FLOAT,
            D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS);
        require(device->CreateUnorderedAccessView(vectors.tex.Get(), nullptr, flowUav.GetAddressOf()), "create motion field view");
        auto code = shader(shaderPath, "Fullscreen", "vs_5_0");
        require(device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, vs.GetAddressOf()), "create vertex shader");
        code = shader(shaderPath, "Composite", "ps_5_0");
        require(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, ps.GetAddressOf()), "create pixel shader");
        code = shader(shaderPath, "Estimate", "cs_5_0");
        require(device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, cs.GetAddressOf()), "create compute shader");
        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(Params); cb.Usage = D3D11_USAGE_DEFAULT; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        require(device->CreateBuffer(&cb, nullptr, constant.GetAddressOf()), "create parameters");
        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX; sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
        require(device->CreateSamplerState(&sd, sampler.GetAddressOf()), "create sampler");

        if (window) {
            swapFlags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            ComPtr<IDXGIFactory5> factory5;
            BOOL tearing = FALSE;
            if (SUCCEEDED(factory.As(&factory5)) && SUCCEEDED(factory5->CheckFeatureSupport(
                DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof(tearing))) && tearing)
                swapFlags |= DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
            DXGI_SWAP_CHAIN_DESC1 d{};
            d.Width = outW; d.Height = outH; d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            d.BufferCount = 3; d.SampleDesc.Count = 1; d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; d.Flags = swapFlags;
            ComPtr<IDXGISwapChain1> s;
            require(factory->CreateSwapChainForHwnd(device.Get(), window, &d, nullptr, nullptr, s.GetAddressOf()), "create swap chain");
            require(s.As(&swap), "get DXGI pacing interface");
            require(swap->SetMaximumFrameLatency(1), "limit swap chain queue");
            waitable = swap->GetFrameLatencyWaitableObject();
            factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER);
        }
        createTarget(outW, outH);
    }

    ~DirectFg() { if (waitable) CloseHandle(waitable); }

    bool ready() const { return frameCount >= 2; }

    void upload(const unsigned char* data, bool consecutive) {
        if (!consecutive) frameCount = 0;
        newest ^= 1;
        auto& frame = frames[newest];
        ctx->UpdateSubresource(frame.y.tex.Get(), 0, nullptr, data, params.sourceW, 0);
        ctx->UpdateSubresource(frame.uv.tex.Get(), 0, nullptr,
            data + size_t(params.sourceW) * params.sourceH, params.sourceW, 0);
        frameCount = std::min(2u, frameCount + 1);
    }

    void estimate() {
        if (!ready()) return;
        ID3D11ShaderResourceView* input[]{frames[newest ^ 1].y.srv.Get(), frames[newest].y.srv.Get()};
        ID3D11ShaderResourceView* empty[2]{};
        ID3D11UnorderedAccessView* nullUav{};
        ctx->UpdateSubresource(constant.Get(), 0, nullptr, &params, 0, 0);
        ctx->CSSetShader(cs.Get(), nullptr, 0);
        ctx->CSSetConstantBuffers(0, 1, constant.GetAddressOf());
        ctx->CSSetShaderResources(0, 2, input);
        ctx->CSSetUnorderedAccessViews(0, 1, flowUav.GetAddressOf(), nullptr);
        ctx->Dispatch((params.flowW + 7) / 8, (params.flowH + 7) / 8, 1);
        ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, nullptr);
        ctx->CSSetShaderResources(0, 2, empty);
    }

    void draw(float phase) {
        params.phase = ready() ? phase : 1.f;
        ID3D11ShaderResourceView* inputs[]{
            frames[newest ^ 1].y.srv.Get(), frames[newest].y.srv.Get(),
            frames[newest ^ 1].uv.srv.Get(), frames[newest].uv.srv.Get(), vectors.srv.Get()};
        ID3D11ShaderResourceView* empty[5]{};
        ctx->UpdateSubresource(constant.Get(), 0, nullptr, &params, 0, 0);
        ctx->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        constexpr float black[4]{0,0,0,1};
        ctx->ClearRenderTargetView(rtv.Get(), black);
        const float fit = std::min(float(outputW) / params.sourceW, float(outputH) / params.sourceH);
        const float width = params.sourceW * fit, height = params.sourceH * fit;
        const D3D11_VIEWPORT vp{(outputW - width) / 2, (outputH - height) / 2, width, height, 0.f, 1.f};
        ctx->RSSetViewports(1, &vp);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs.Get(), nullptr, 0);
        ctx->PSSetShader(ps.Get(), nullptr, 0);
        ctx->PSSetConstantBuffers(0, 1, constant.GetAddressOf());
        ctx->PSSetSamplers(0, 1, sampler.GetAddressOf());
        ctx->PSSetShaderResources(0, 5, inputs);
        ctx->Draw(3, 0);
        ctx->PSSetShaderResources(0, 5, empty);
        ctx->OMSetRenderTargets(0, nullptr, nullptr);
    }

    bool available(DWORD ms = 0) const { return !waitable || WaitForSingleObject(waitable, ms) == WAIT_OBJECT_0; }

    DXGI_FRAME_STATISTICS stats() {
        DXGI_FRAME_STATISTICS stats{};
        if (swap) swap->GetFrameStatistics(&stats);
        return stats;
    }

    bool present() {
        if (!swap) return false;
        const bool tearing = (swapFlags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) != 0;
        const HRESULT hr = swap->Present(tearing ? 0 : 1,
            DXGI_PRESENT_DO_NOT_WAIT | (tearing ? DXGI_PRESENT_ALLOW_TEARING : 0));
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING || hr == DXGI_STATUS_OCCLUDED) return false;
        require(hr, "present video frame");
        return true;
    }

    void resize(UINT w, UINT h) {
        if (!swap || !w || !h || (w == outputW && h == outputH)) return;
        ctx->OMSetRenderTargets(0, nullptr, nullptr);
        rtv.Reset(); target.Reset(); ctx->Flush();
        require(swap->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, swapFlags), "resize swap chain");
        createTarget(w, h);
    }
};

#if defined(FG_BENCH)
static ComPtr<ID3D11Query> query(DirectFg& fg, D3D11_QUERY type) {
    ComPtr<ID3D11Query> out;
    const D3D11_QUERY_DESC desc{type, 0};
    require(fg.device->CreateQuery(&desc, out.GetAddressOf()), "create timing query");
    return out;
}

template<class T>
static T result(DirectFg& fg, ID3D11Query* q) {
    T value{};
    const auto deadline = Clock::now() + std::chrono::seconds(2);
    for (;;) {
        const HRESULT hr = fg.ctx->GetData(q, &value, sizeof(value), 0);
        if (hr == S_OK) return value;
        require(hr, "read GPU timing");
        if (Clock::now() >= deadline) throw std::runtime_error("GPU timing timed out");
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
}

static std::vector<unsigned char> sample(DirectFg& fg, UINT w, UINT h) {
    D3D11_TEXTURE2D_DESC d{};
    fg.target->GetDesc(&d);
    d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> copy;
    require(fg.device->CreateTexture2D(&d, nullptr, copy.GetAddressOf()), "create test readback");
    fg.ctx->CopyResource(copy.Get(), fg.target.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    require(fg.ctx->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &mapped), "read test frame");
    std::vector<unsigned char> out;
    const auto* data = static_cast<const unsigned char*>(mapped.pData);
    for (UINT y = 8; y < h; y += 16)
        for (UINT x = 8; x < w; x += 16)
            out.push_back(data[size_t(y) * mapped.RowPitch + x * 4 + 1]);
    fg.ctx->Unmap(copy.Get(), 0);
    return out;
}

static LRESULT CALLBACK testWindowProc(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, w, l);
}

struct DisplayTest { std::wstring name; RECT bounds{}; bool found{}; };
static BOOL CALLBACK findDisplay(HMONITOR monitor, HDC, LPRECT, LPARAM value) {
    auto& output = *reinterpret_cast<DisplayTest*>(value);
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (GetMonitorInfoW(monitor, reinterpret_cast<MONITORINFO*>(&info)) && _wcsicmp(info.szDevice, output.name.c_str()) == 0) {
        output.bounds = info.rcWork; output.found = true; return FALSE;
    }
    return TRUE;
}

static void waitUntil(Clock::time_point due, HANDLE timer) {
    using namespace std::chrono;
    const auto delay = duration_cast<nanoseconds>(due - Clock::now());
    if (delay > microseconds(300) && timer) {
        LARGE_INTEGER expires{};
        expires.QuadPart = -((delay - microseconds(150)).count() / 100);
        if (SetWaitableTimer(timer, &expires, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 50);
    }
    while (Clock::now() < due) YieldProcessor();
}

// Read 60 input frames/sec independently of the 120/180-Hz renderer.
// Two slots absorb jitter from the capture thread without a long queue.
class FrameStream {
    struct Slot {
        std::vector<unsigned char> bytes;
        size_t sequence{};
        Clock::time_point at{};
    };
    std::array<Slot, 2> pending;
    std::mutex gate;
    std::thread worker;
    std::atomic_bool stop{}, eof{};
    size_t sequence{}, head{}, count{};

public:
    explicit FrameStream(size_t bytes) {
        for (auto& slot : pending) slot.bytes.resize(bytes);
        worker = std::thread([this, bytes] {
            const HANDLE handle = GetStdHandle(STD_INPUT_HANDLE);
            std::vector<unsigned char> buffer(bytes);
            while (!stop) {
                for (size_t offset = 0; offset < bytes; ) {
                    DWORD count{};
                    if (stop || !ReadFile(handle, buffer.data() + offset,
                        static_cast<DWORD>(std::min<size_t>(1024 * 1024, bytes - offset)), &count, nullptr) || count == 0) {
                        eof = true; return;
                    }
                    offset += count;
                }
                std::lock_guard lock(gate);
                if (count == pending.size()) { head = (head + 1) % pending.size(); --count; }
                auto& slot = pending[(head + count) % pending.size()];
                slot.bytes.swap(buffer);
                slot.sequence = ++sequence;
                slot.at = Clock::now();
                ++count;
            }
        });
    }
    ~FrameStream() {
        stop = true;
        while (WaitForSingleObject(worker.native_handle(), 0) == WAIT_TIMEOUT) {
            CancelSynchronousIo(worker.native_handle());
            if (WaitForSingleObject(worker.native_handle(), 25) == WAIT_OBJECT_0) break;
        }
        worker.join();
    }
    size_t take(std::vector<unsigned char>& frame, Clock::time_point& arrived) {
        std::lock_guard lock(gate);
        if (!count) return 0;
        if (count > 1 && Clock::now() - pending[head].at > std::chrono::milliseconds(36)) {
            head = (head + 1) % pending.size(); --count;
        }
        auto& slot = pending[head];
        frame.swap(slot.bytes);
        arrived = slot.at;
        head = (head + 1) % pending.size(); --count;
        return slot.sequence;
    }
    bool ended() const { return eof; }
};

static void presentFixture(const wchar_t* shaderPath, UINT w, UINT h,
    const std::vector<std::vector<unsigned char>>& fixture, int multiplier, const wchar_t* display, bool streamed, int seconds) {
    DisplayTest monitor{display};
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    EnumDisplayMonitors(nullptr, nullptr, findDisplay, reinterpret_cast<LPARAM>(&monitor));
    if (!monitor.found) throw std::runtime_error("requested display not found");
    const auto bounds = monitor.bounds;
    const UINT outW = std::min(1280L, (bounds.right - bounds.left) / 2);
    const UINT outH = outW * 9 / 16;
    const auto module = GetModuleHandleW(nullptr);
    WNDCLASSW klass{};
    klass.lpfnWndProc = testWindowProc;
    klass.hInstance = module;
    klass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    klass.lpszClassName = L"CapturePlayerFgProbe";
    if (!RegisterClassW(&klass)) throw std::runtime_error("could not register test window");
    RECT size{0, 0, static_cast<LONG>(outW), static_cast<LONG>(outH)};
    constexpr DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU;
    constexpr DWORD exStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
    AdjustWindowRectEx(&size, style, FALSE, exStyle);
    HWND window = CreateWindowExW(exStyle, klass.lpszClassName, L"CapturePlayer D3D11 frame pacing test", style,
        bounds.right - (size.right - size.left) - 30, bounds.top + 45, size.right - size.left, size.bottom - size.top,
        nullptr, nullptr, module, nullptr);
    if (!window) throw std::runtime_error("could not create test window");
    const auto destroy = [](void* p) { if (IsWindow(static_cast<HWND>(p))) DestroyWindow(static_cast<HWND>(p)); };
    const std::unique_ptr<void, decltype(destroy)> guard(window, destroy);
    std::unique_ptr<FrameStream> stream;
    std::vector<unsigned char> incoming;
    if (streamed) {
        const size_t bytes = size_t(w) * h * 3 / 2;
        incoming.resize(bytes);
        stream = std::make_unique<FrameStream>(bytes);
    }
    DirectFg fg(window, w, h, outW, outH, shaderPath);
    ShowWindow(window, SW_SHOWNOACTIVATE);
    if (MonitorFromWindow(window, MONITOR_DEFAULTTONULL) == nullptr) throw std::runtime_error("test window not on a display");
    const std::unique_ptr<void, decltype(&CloseHandle)> timer(
        CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS), CloseHandle);
    std::vector<double> sourceAge;
    size_t latest = 0;
    if (stream) {
        const auto deadline = Clock::now() + std::chrono::seconds(8);
        Clock::time_point arrived;
        while (!(latest = stream->take(incoming, arrived))) {
            if (stream->ended() || Clock::now() >= deadline) throw std::runtime_error("no source frames arrived");
            Sleep(5);
        }
        fg.upload(incoming.data(), false);
    } else fg.upload(fixture[0].data(), false);
    const auto epoch = Clock::now() + std::chrono::milliseconds(stream ? 25 : 0);
    auto lastInput = epoch;
    auto at = [&](int slot) {
        return epoch + std::chrono::nanoseconds(1000000000ll * slot / (60 * multiplier));
    };
    int total = 0, generated = 0, missed = 0, skipped = 0, lastPair = -1, inputFrames = 0, inputSkipped = 0, inputStarved = 0;
    bool hasNewPair = false, sentBase = false;
    DXGI_FRAME_STATISTICS startStats{}, endStats{};
    const int finish = 60 * multiplier * (seconds + 1);
    for (int slot = 0; slot < finish; ++slot) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) return;
            TranslateMessage(&msg); DispatchMessageW(&msg);
        }
        waitUntil(at(slot), timer.get());
        const bool measure = slot >= 60 * multiplier;
        if (slot == 60 * multiplier) startStats = fg.stats();
        const int pair = slot / multiplier;
        if (pair != lastPair) {
            sentBase = false;
            if (stream) {
                Clock::time_point arrived;
                const size_t seq = stream->take(incoming, arrived);
                hasNewPair = seq != 0;
                if (seq) {
                    lastInput = Clock::now();
                    const bool contiguous = seq == latest + 1;
                    if (measure) {
                        ++inputFrames; inputSkipped += static_cast<int>(seq - latest - 1);
                        sourceAge.push_back(std::chrono::duration<double, std::milli>(Clock::now() - arrived).count());
                    }
                    latest = seq;
                    fg.upload(incoming.data(), contiguous);
                    fg.estimate();
                } else {
                    if (stream->ended() || Clock::now() - lastInput > std::chrono::milliseconds(500))
                        throw std::runtime_error("capture input stopped");
                    if (measure) ++inputStarved;
                }
            } else {
                hasNewPair = true;
                if (measure) ++inputFrames;
                fg.upload(fixture[(static_cast<size_t>(pair) + 1) % fixture.size()].data(), pair == lastPair + 1);
                fg.estimate();
            }
            lastPair = pair;
        }
        if (!hasNewPair || (sentBase && !fg.ready())) continue;
        if (Clock::now() >= at(slot + 1)) { if (measure) ++skipped; continue; }
        if (!fg.available(2)) { if (measure) ++missed; continue; }
        const bool isGenerated = sentBase && slot % multiplier != 0;
        fg.draw(isGenerated ? float(slot % multiplier) / multiplier : 0);
        if (fg.present()) {
            sentBase = true;
            if (measure) { ++total; if (isGenerated) ++generated; }
        } else if (measure) ++missed;
    }
    waitUntil(at(finish), timer.get());
    endStats = fg.stats();
    const int presented = startStats.PresentCount && endStats.PresentCount >= startStats.PresentCount
        ? static_cast<int>(endStats.PresentCount - startStats.PresentCount) : -1;
    const int refreshes = startStats.PresentRefreshCount && endStats.PresentRefreshCount >= startStats.PresentRefreshCount
        ? static_cast<int>(endStats.PresentRefreshCount - startStats.PresentRefreshCount) : -1;
    std::sort(sourceAge.begin(), sourceAge.end());
    const double age95 = sourceAge.empty() ? -1 : sourceAge[(sourceAge.size() - 1) * 95 / 100];
    std::printf("multiplier=%d window=%ux%u seconds=%d input=%d source_skipped=%d source_starved=%d input_queue_p95_ms=%.2f submitted=%d generated=%d queue_missed=%d late=%d dxgi_presents=%d dxgi_refreshes=%d\n",
        multiplier, outW, outH, seconds, inputFrames, inputSkipped, inputStarved, age95, total, generated, missed, skipped, presented, refreshes);
}

// Production helper. The parent owns the controls and the capture lifetime;
// this child owns a fixed-depth input queue, the GPU device and presentation.
static int playEmbedded(const wchar_t* shaderPath, UINT w, UINT h, int multiplier, HWND parent) {
    if (!IsWindow(parent) || multiplier < 1 || multiplier > 6 || !w || !h ||
        ((w | h) & 1) || w > 7680 || h > 4320) throw std::runtime_error("invalid player settings");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
    FrameStream stream(size_t(w) * h * 3 / 2);
    std::vector<unsigned char> frame(size_t(w) * h * 3 / 2);
    const auto module = GetModuleHandleW(nullptr);
    WNDCLASSW klass{};
    klass.lpfnWndProc = testWindowProc;
    klass.hInstance = module;
    klass.lpszClassName = L"CapturePlayerGpuPreview";
    if (!RegisterClassW(&klass)) throw std::runtime_error("could not register preview");
    HWND window = CreateWindowExW(0, klass.lpszClassName, L"CapturePlayer GPU preview",
        WS_CHILD | WS_DISABLED, 0, 0, 960, 540, parent, nullptr, module, nullptr);
    if (!window) throw std::runtime_error("could not create preview");
    const auto destroy = [](void* p) { if (IsWindow(static_cast<HWND>(p))) DestroyWindow(static_cast<HWND>(p)); };
    const std::unique_ptr<void, decltype(destroy)> guard(window, destroy);
    DirectFg fg(window, w, h, 960, 540, shaderPath);
    const std::unique_ptr<void, decltype(&CloseHandle)> timer(
        CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS), CloseHandle);
    Clock::time_point arrived;
    size_t seq = 0;
    const auto initialDeadline = Clock::now() + std::chrono::seconds(8);
    while (!(seq = stream.take(frame, arrived))) {
        if (!IsWindow(parent)) return 0;
        if (stream.ended() || Clock::now() >= initialDeadline) throw std::runtime_error("no live video");
        Sleep(5);
    }
    fg.upload(frame.data(), false);
    std::fprintf(stderr, "CaptureFG ready=%llu\n", static_cast<unsigned long long>(reinterpret_cast<UINT_PTR>(window)));
    std::fflush(stderr);

    const int rate = 60 * multiplier;
    const auto epoch = Clock::now() + std::chrono::milliseconds(25);
    auto at = [&](long long slot) { return epoch + std::chrono::nanoseconds(1000000000ll * slot / rate); };
    auto lastInput = Clock::now(), lastReport = Clock::now();
    bool havePair = false, sentBase = false;
    long long lastGroup = -1;
    int sent = 0, generated = 0, input = 0, missed = 0;
    double age = 0;
    UINT priorW = 0, priorH = 0;
    for (long long slot = 0; IsWindow(parent); ++slot) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) return 0;
            TranslateMessage(&msg); DispatchMessageW(&msg);
        }
        waitUntil(at(slot), timer.get());
        const auto now = Clock::now();
        const auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(now - epoch).count();
        slot = std::max(slot, elapsed * rate / 1000000000ll);
        const auto group = slot / multiplier;
        if (group != lastGroup) {
            sentBase = false;
            size_t next = stream.take(frame, arrived);
            havePair = next != 0;
            if (next) {
                lastInput = now;
                age = std::chrono::duration<double, std::milli>(now - arrived).count();
                fg.upload(frame.data(), multiplier > 1 && next == seq + 1 && group == lastGroup + 1);
                seq = next;
                ++input;
                if (multiplier > 1) fg.estimate();
            }
            lastGroup = group;
        }
        if (stream.ended() || now - lastInput > std::chrono::milliseconds(600))
            throw std::runtime_error("live video stopped");
        if (havePair && (!sentBase || fg.ready()) && IsWindowVisible(window)) {
            RECT rect{};
            GetClientRect(window, &rect);
            const UINT width = rect.right, height = rect.bottom;
            if (width && height && (width != priorW || height != priorH)) {
                fg.resize(width, height);
                priorW = width; priorH = height;
                std::fprintf(stderr, "CaptureFG size=%ux%u\n", width, height);
            }
            if (width && height && Clock::now() < at(slot + 1) && fg.available(1)) {
                const bool fill = sentBase && slot % multiplier != 0;
                fg.draw(multiplier == 1 ? 1.f : fill ? float(slot % multiplier) / multiplier : 0.f);
                if (fg.present()) {
                    sentBase = true; ++sent;
                    if (fill) ++generated;
                } else ++missed;
            } else ++missed;
        }
        const double seconds = std::chrono::duration<double>(now - lastReport).count();
        if (seconds >= 1) {
            std::fprintf(stderr, "CaptureFG fps=%.1f generated=%.1f input=%.1f missed=%d queue=%.1f\n",
                sent / seconds, generated / seconds, input / seconds, missed, age);
            std::fflush(stderr);
            sent = generated = input = missed = 0;
            lastReport = now;
        }
    }
    return 0;
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 7 && std::wstring(argv[1]) == L"--embedded") {
        try {
            return playEmbedded(argv[2], static_cast<UINT>(std::stoul(argv[3])),
                static_cast<UINT>(std::stoul(argv[4])), std::stoi(argv[5]),
                reinterpret_cast<HWND>(static_cast<UINT_PTR>(std::stoull(argv[6]))));
        } catch (const std::exception& e) {
            std::fprintf(stderr, "CaptureFG error: %s\n", e.what());
            return 1;
        }
    }
    if (argc != 5 && argc != 7 && argc != 8) {
        std::fprintf(stderr, "usage: fg_bench.exe shader.hlsl input.nv12 width height [multiplier display-device [seconds]]\n");
        return 2;
    }
    try {
        const UINT w = static_cast<UINT>(std::stoul(argv[3]));
        const UINT h = static_cast<UINT>(std::stoul(argv[4]));
        if (!w || !h || ((w | h) & 1) || w > 7680 || h > 4320) throw std::runtime_error("invalid fixture size");
        const size_t bytes = size_t(w) * h * 3 / 2;
        const bool streamed = std::wstring(argv[2]) == L"-";
        std::ifstream input(argv[2], std::ios::binary);
        std::vector<std::vector<unsigned char>> fixture;
        for (unsigned i = 0; !streamed && i < 16; ++i) {
            std::vector<unsigned char> frame(bytes);
            if (!input.read(reinterpret_cast<char*>(frame.data()), bytes)) break;
            fixture.push_back(std::move(frame));
        }
        if (!streamed && fixture.size() < 2) throw std::runtime_error("need at least two complete source frames");
        if (argc >= 7) {
            const int multiplier = std::stoi(argv[5]);
            if (multiplier != 2 && multiplier != 3) throw std::runtime_error("test multiplier must be 2 or 3");
            const int seconds = argc >= 8 ? std::clamp(std::stoi(argv[7]), 1, 45) : 5;
            presentFixture(argv[1], w, h, fixture, multiplier, argv[6], streamed, seconds);
            return 0;
        }
        if (streamed) throw std::runtime_error("streamed input requires a display");
        DirectFg fg(nullptr, w, h, w, h, argv[1]);
        fg.upload(fixture[0].data(), false);
        fg.upload(fixture[1].data(), true);
        fg.estimate();
        fg.draw(0); const auto first = sample(fg, w, h);
        fg.draw(.5f); const auto generated = sample(fg, w, h);
        fg.draw(1); const auto last = sample(fg, w, h);
        size_t intermediate = 0;
        for (size_t i = 0; i < generated.size(); ++i)
            if (generated[i] != first[i] && generated[i] != last[i]) ++intermediate;
        std::printf("frames=%zu width=%u height=%u samples=%zu differing_from_both=%zu\n",
            fixture.size(), w, h, generated.size(), intermediate);
        if (intermediate == 0) throw std::runtime_error("test clip did not yield an intermediate image");

        const auto disjoint = query(fg, D3D11_QUERY_TIMESTAMP_DISJOINT);
        const auto begin = query(fg, D3D11_QUERY_TIMESTAMP);
        const auto end = query(fg, D3D11_QUERY_TIMESTAMP);
        for (int multiplier : {2, 3}) {
            std::vector<double> gpu, wall;
            auto due = Clock::now();
            for (int i = 0; i < 64; ++i) {
                const auto start = Clock::now();
                fg.ctx->Begin(disjoint.Get()); fg.ctx->End(begin.Get());
                fg.upload(fixture[static_cast<size_t>(i) % fixture.size()].data(), true);
                fg.estimate();
                for (int k = 0; k < multiplier; ++k) fg.draw(float(k) / multiplier);
                fg.ctx->End(end.Get()); fg.ctx->End(disjoint.Get()); fg.ctx->Flush();
                const auto info = result<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>(fg, disjoint.Get());
                const UINT64 a = result<UINT64>(fg, begin.Get()), b = result<UINT64>(fg, end.Get());
                if (i >= 4 && !info.Disjoint && info.Frequency && b >= a) {
                    gpu.push_back((b - a) * 1000. / info.Frequency);
                    wall.push_back(std::chrono::duration<double, std::milli>(Clock::now() - start).count());
                }
                due += std::chrono::microseconds(16667);
                std::this_thread::sleep_until(due);
            }
            if (gpu.size() < 40) throw std::runtime_error("insufficient GPU timings");
            std::sort(gpu.begin(), gpu.end()); std::sort(wall.begin(), wall.end());
            const auto index = (gpu.size() - 1) * 95 / 100;
            std::printf("multiplier=%d groups=%zu gpu_p50_ms=%.2f gpu_p95_ms=%.2f wall_p95_ms=%.2f\n",
                multiplier, gpu.size(), gpu[gpu.size() / 2], gpu[index], wall[index]);
        }
        return 0;
    } catch (const std::exception& e) {
        std::fprintf(stderr, "FG probe: %s\n", e.what());
        return 1;
    }
}
#endif
