// CapturePlayer's standalone SDR neural filter. The Frei0r ABI lets FFmpeg
// preserve the original capture timestamps and audio stream.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <nvsdk_ngx.h>
#include <nvsdk_ngx_params.h>
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <immintrin.h>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
constexpr auto featureId = static_cast<NVSDK_NGX_Feature>(18);
constexpr auto snippetId = 0x0876232Cull;
constexpr unsigned preset = 2;
HMODULE selfModule;
using FileNameFn = decltype(&GetModuleFileNameW);
FileNameFn originalFileName;

void check(bool ok, const char* reason) {
    if (!ok) throw std::runtime_error(reason);
}
void hr(HRESULT result, const char* stage) {
    if (FAILED(result)) {
        std::fprintf(stderr, "[CaptureNR] %s HRESULT=0x%08lx\n", stage, static_cast<unsigned long>(result));
        throw std::runtime_error(stage);
    }
}
void ngx(NVSDK_NGX_Result result, const char* stage) {
    if (result != NVSDK_NGX_Result_Success) {
        std::fprintf(stderr, "[CaptureNR] %s NGX=0x%08x\n", stage, static_cast<unsigned>(result));
        throw std::runtime_error(stage);
    }
}
template<class T> T symbol(HMODULE module, const char* name) {
    auto value = reinterpret_cast<T>(GetProcAddress(module, name));
    check(value != nullptr, name);
    return value;
}
std::wstring modulePath(HMODULE module) {
    std::wstring name(32768, L'\0');
    DWORD size = GetModuleFileNameW(module, name.data(), static_cast<DWORD>(name.size()));
    check(size > 0 && size < name.size(), "read module path");
    name.resize(size);
    return name;
}

// The model's snippet expects to be hosted by NGX. Match the compatibility
// contract documented by the Magpie source only for this local DLL's import
// and only when asked about the filter's own module. No driver or disk patch.
DWORD WINAPI snippetFileName(HMODULE module, LPWSTR name, DWORD size) {
    if (module != selfModule) return originalFileName(module, name, size);
    constexpr wchar_t identity[] = L"nvngx.dll";
    constexpr DWORD length = sizeof(identity) / sizeof(wchar_t) - 1;
    if (!name || !size) { SetLastError(ERROR_INSUFFICIENT_BUFFER); return 0; }
    DWORD copied = std::min(size - 1, length);
    std::memcpy(name, identity, copied * sizeof(wchar_t));
    name[copied] = L'\0';
    if (copied < length) { SetLastError(ERROR_INSUFFICIENT_BUFFER); return size; }
    return length;
}
struct LocalImport {
    void** slot{};
    void* original{};

    void install(HMODULE module) {
        auto base = reinterpret_cast<std::uint8_t*>(module);
        auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        check(dos->e_magic == IMAGE_DOS_SIGNATURE && dos->e_lfanew > 0 &&
              dos->e_lfanew < 4096, "model DOS header");
        auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        check(nt->Signature == IMAGE_NT_SIGNATURE &&
              nt->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC, "model PE header");
        auto dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        auto size = nt->OptionalHeader.SizeOfImage;
        auto valid = [size](size_t off, size_t len) { return off && off < size && len <= size - off; };
        check(valid(dir.VirtualAddress, dir.Size), "model import table");
        for (size_t i = 0; (i + 1) * sizeof(IMAGE_IMPORT_DESCRIPTOR) <= dir.Size; ++i) {
            auto d = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress)[i];
            if (!d.Name) break;
            if (!valid(d.Name, 1) || !valid(d.OriginalFirstThunk, 8) || !valid(d.FirstThunk, 8)) continue;
            const char* library = reinterpret_cast<const char*>(base + d.Name);
            if (_stricmp(library, "kernel32.dll") &&
                _strnicmp(library, "api-ms-win-core-libraryloader-", 30)) continue;
            for (size_t j = 0; valid(d.OriginalFirstThunk + 8*j, 8) && valid(d.FirstThunk + 8*j, 8); ++j) {
                auto name = reinterpret_cast<const ULONGLONG*>(base + d.OriginalFirstThunk)[j];
                if (!name) break;
                constexpr char target[] = "GetModuleFileNameW";
                if (IMAGE_SNAP_BY_ORDINAL64(name) || !valid(name, 2 + sizeof(target)) ||
                    std::memcmp(base + name + 2, target, sizeof(target))) continue;
                auto entry = reinterpret_cast<void**>(base + d.FirstThunk) + j;
                DWORD protect{};
                check(VirtualProtect(entry, sizeof(void*), PAGE_READWRITE, &protect) != 0, "local snippet import protection");
                slot = entry;
                original = *entry;
                originalFileName = reinterpret_cast<FileNameFn>(original);
                *entry = reinterpret_cast<void*>(&snippetFileName);
                VirtualProtect(entry, sizeof(void*), protect, &protect);
                return;
            }
        }
        throw std::runtime_error("model compatibility import unavailable");
    }
    void restore() noexcept {
        if (!slot) return;
        DWORD protect{};
        if (VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &protect)) {
            *slot = original;
            VirtualProtect(slot, sizeof(void*), protect, &protect);
        }
        slot = nullptr;
        originalFileName = nullptr;
    }
};

using Init = NVSDK_NGX_Result(NVSDK_CONV*)(unsigned long long, const wchar_t*, ID3D12Device*, NVSDK_NGX_Version, const NVSDK_NGX_Parameter*);
using Create = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D12GraphicsCommandList*, NVSDK_NGX_Feature, NVSDK_NGX_Parameter*, NVSDK_NGX_Handle**);
using Evaluate = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D12GraphicsCommandList*, const NVSDK_NGX_Handle*, const NVSDK_NGX_Parameter*, PFN_NVSDK_NGX_ProgressCallback);
using Release = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Handle*);
using Shutdown = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D12Device*);

NVSDK_NGX_Result NVSDK_CONV fixedRatio(NVSDK_NGX_Parameter* p) {
    p->Set("DLSSNR.ScalingRatio", 1.0f);
    return NVSDK_NGX_Result_Success;
}

struct Surface {
    ComPtr<ID3D12Resource> image;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout{};
    UINT64 bytes{};
};

struct Neural {
    unsigned width{}, height{};
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE event{};
    UINT64 sequence{};
    Surface color, output, motion, depth;
    ComPtr<ID3D12Resource> upload, guidance, readback;
    std::uint8_t *uploadBytes{}, *readbackBytes{};
    HMODULE snippet{};
    LocalImport compatibility;
    bool coreReady{}, snippetReady{}, changedReported{};
    NVSDK_NGX_Parameter* params{};
    NVSDK_NGX_Handle* feature{};
    Evaluate evaluate{};
    Release release{};
    Shutdown shutdown{};

    ~Neural() {
        if (feature && release) release(feature);
        if (params) NVSDK_NGX_D3D12_DestroyParameters(params);
        if (snippetReady) shutdown(device.Get());
        compatibility.restore();
        if (snippet) FreeLibrary(snippet);
        if (coreReady) NVSDK_NGX_D3D12_Shutdown1(device.Get());
        if (event) CloseHandle(event);
    }

    void init(unsigned w, unsigned h) {
        width = w;
        height = h;
        check(w && h && w <= 1920 && h <= 1080, "unsupported neural input dimensions");
        ComPtr<IDXGIFactory1> factory;
        hr(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "DXGI factory");
        for (UINT i = 0; ; ++i) {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(i, &adapter) != S_OK) break;
            DXGI_ADAPTER_DESC1 desc{};
            if (SUCCEEDED(adapter->GetDesc1(&desc)) && desc.VendorId == 0x10de &&
                SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) break;
        }
        check(device != nullptr, "no NVIDIA D3D12 device");
        D3D12_COMMAND_QUEUE_DESC q{};
        hr(device->CreateCommandQueue(&q, IID_PPV_ARGS(&queue)), "command queue");
        hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)), "allocator");
        hr(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)), "command list");
        hr(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "fence");
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        check(event != nullptr, "fence event");

        auto dir = std::filesystem::path(modulePath(selfModule)).parent_path();
        wchar_t env[32768]{};
        DWORD count = GetEnvironmentVariableW(L"CAPTURE_NR_CACHE", env, static_cast<DWORD>(std::size(env)));
        check(count && count < std::size(env), "neural cache directory");
        auto library = dir.parent_path() / L"models" / L"nvngx_dlssnr.dll";
        ngx(NVSDK_NGX_D3D12_Init_with_ProjectID("4d9058d2-67f8-435b-8c04-b11cc42ce585",
            NVSDK_NGX_ENGINE_TYPE_CUSTOM, "CapturePlayer", env, device.Get(), nullptr, NVSDK_NGX_Version_API), "NGX core init");
        coreReady = true;
        snippet = LoadLibraryExW(library.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(snippet != nullptr, "load model DLL");
        auto initModel = symbol<Init>(snippet, "NVSDK_NGX_D3D12_Init_Ext");
        auto create = symbol<Create>(snippet, "NVSDK_NGX_D3D12_CreateFeature");
        evaluate = symbol<Evaluate>(snippet, "NVSDK_NGX_D3D12_EvaluateFeature");
        release = symbol<Release>(snippet, "NVSDK_NGX_D3D12_ReleaseFeature");
        shutdown = symbol<Shutdown>(snippet, "NVSDK_NGX_D3D12_Shutdown1");
        compatibility.install(snippet);
        ngx(initModel(snippetId, env, device.Get(), NVSDK_NGX_Version_API, nullptr), "DLSSNR init");
        snippetReady = true;
        ngx(NVSDK_NGX_D3D12_AllocateParameters(&params), "allocate NGX parameters");
        for (auto key : {"Width", "DLSSNR.Width", "DLSSNR.InputWidth", "DLSSNR.OutputWidth", "DLSSNR.Output.Width"})
            params->Set(key, width);
        for (auto key : {"Height", "DLSSNR.Height", "DLSSNR.InputHeight", "DLSSNR.OutputHeight", "DLSSNR.Output.Height"})
            params->Set(key, height);
        params->Set("CreationNodeMask", 1u);
        params->Set("VisibilityNodeMask", 1u);
        params->Set("PerfQualityValue", static_cast<int>(NVSDK_NGX_PerfQuality_Value_Balanced));
        params->Set("DLSSNR.Upscaling", 0u);
        params->Set("DLSSNR.Scale", 1.0f);
        params->Set("DLSSNR.ScalingRatio", 1.0f);
        params->Set("DLSSNRComputeScalingRatioCallback", reinterpret_cast<void*>(&fixedRatio));
        params->Set("DLSSNR.Hint.Render.Preset", static_cast<int>(preset));
        ngx(create(list.Get(), featureId, params, &feature), "create Feature 18");
        check(feature != nullptr, "no neural feature handle");

        texture(color, DXGI_FORMAT_R8G8B8A8_UNORM);
        texture(output, DXGI_FORMAT_R8G8B8A8_UNORM);
        texture(motion, DXGI_FORMAT_R16G16_FLOAT);
        texture(depth, DXGI_FORMAT_R32_FLOAT);
        buffer(upload, color.bytes, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
        buffer(guidance, std::max(motion.bytes, depth.bytes), D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
        buffer(readback, output.bytes, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_RANGE noRead{0, 0};
        hr(upload->Map(0, &noRead, reinterpret_cast<void**>(&uploadBytes)), "map upload");
        std::uint8_t* zero{};
        hr(guidance->Map(0, &noRead, reinterpret_cast<void**>(&zero)), "map guidance");
        std::memset(zero, 0, static_cast<size_t>(std::max(motion.bytes, depth.bytes)));
        guidance->Unmap(0, nullptr);
        hr(readback->Map(0, nullptr, reinterpret_cast<void**>(&readbackBytes)), "map readback");
        uploadTo(motion, guidance.Get());
        uploadTo(depth, guidance.Get());
        submit(10000);
        std::fprintf(stderr, "[CaptureNR] Feature 18 created, requested preset=%u, input=%ux%u\n", preset, w, h);
    }

    void texture(Surface& s, DXGI_FORMAT format) {
        D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_DEFAULT};
        heap.CreationNodeMask = heap.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC d{};
        d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        d.Width = width; d.Height = height; d.DepthOrArraySize = 1; d.MipLevels = 1;
        d.Format = format; d.SampleDesc.Count = 1; d.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COMMON,
            nullptr, IID_PPV_ARGS(&s.image)), "allocate neural texture");
        device->GetCopyableFootprints(&d, 0, 1, 0, &s.layout, nullptr, nullptr, &s.bytes);
    }

    void buffer(ComPtr<ID3D12Resource>& p, UINT64 bytes, D3D12_HEAP_TYPE type, D3D12_RESOURCE_STATES state) {
        D3D12_HEAP_PROPERTIES heap{type};
        heap.CreationNodeMask = heap.VisibleNodeMask = 1;
        D3D12_RESOURCE_DESC d{};
        d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        d.Width = bytes; d.Height = 1; d.DepthOrArraySize = 1; d.MipLevels = 1;
        d.SampleDesc.Count = 1; d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        hr(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d, state, nullptr, IID_PPV_ARGS(&p)), "allocate staging buffer");
    }
    void state(const Surface& s, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to) {
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition = {s.image.Get(), D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, from, to};
        list->ResourceBarrier(1, &b);
    }
    D3D12_TEXTURE_COPY_LOCATION surface(const Surface& s) {
        D3D12_TEXTURE_COPY_LOCATION loc{};
        loc.pResource = s.image.Get(); loc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        return loc;
    }
    D3D12_TEXTURE_COPY_LOCATION staging(ID3D12Resource* buf, const Surface& s) {
        D3D12_TEXTURE_COPY_LOCATION loc{};
        loc.pResource = buf; loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; loc.PlacedFootprint = s.layout;
        return loc;
    }
    void uploadTo(const Surface& s, ID3D12Resource* buf) {
        auto src = staging(buf, s), dst = surface(s);
        state(s, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        state(s, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
    }
    void submit(DWORD timeout) {
        hr(list->Close(), "close command list");
        ID3D12CommandList* submission[] = {list.Get()};
        queue->ExecuteCommandLists(1, submission);
        hr(queue->Signal(fence.Get(), ++sequence), "signal neural fence");
        if (fence->GetCompletedValue() < sequence) {
            hr(fence->SetEventOnCompletion(sequence, event), "set fence event");
            check(WaitForSingleObject(event, timeout) == WAIT_OBJECT_0, "neural GPU timeout");
        }
        hr(device->GetDeviceRemovedReason(), "neural device status");
    }
    void run(const std::uint32_t* input, std::uint32_t* dest, bool reset) {
        auto bytes = reinterpret_cast<const std::uint8_t*>(input);
        for (unsigned y=0; y<height; ++y)
            std::memcpy(uploadBytes + y*color.layout.Footprint.RowPitch, bytes + y*width*4, width*4);
        hr(allocator->Reset(), "reset allocator");
        hr(list->Reset(allocator.Get(), nullptr), "reset neural list");
        uploadTo(color, upload.Get());
        params->Set("DLSSNR.Color", color.image.Get());
        params->Set("DLSSNR.Output", output.image.Get());
        params->Set("DLSSNR.MVec", motion.image.Get());
        params->Set("DLSSNR.Depth", depth.image.Get());
        for (auto part : {"Color", "Output", "MVec", "Depth"}) {
            const std::string prefix = std::string("DLSSNR.") + part + "Subrect";
            params->Set((prefix + "BaseX").c_str(), 0u);
            params->Set((prefix + "BaseY").c_str(), 0u);
            params->Set((prefix + "Width").c_str(), width);
            params->Set((prefix + "Height").c_str(), height);
        }
        params->Set("DLSSNR.MVecScaleX", 1.0f);
        params->Set("DLSSNR.MVecScaleY", 1.0f);
        params->Set("DLSSNR.DepthInverted", 1);
        params->Set("DLSSNR.Enabled", 1);
        params->Set("DLSSNR.Reset", reset ? 1 : 0);
        params->Set("DLSSNR.Style", 2);
        params->Set("DLSSNR.Intensity", 0.5f);
        params->Set("DLSSNR.LocalToneStrength", 1.0f);
        params->Set("DLSSNR.LocalStructureStrength", 1.0f);
        params->Set("DLSSNR.SkinStructureStrength", 1.0f);
        params->Set("DLSSNR.UseAutoMask", 1);
        params->Set("DLSSNR.UICorrection", 1);
        ngx(evaluate(list.Get(), feature, params, nullptr), "evaluate Feature 18");
        auto src = surface(output), dst = staging(readback.Get(), output);
        state(output, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_SOURCE);
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        state(output, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);
        submit(5000);
        auto out = reinterpret_cast<std::uint8_t*>(dest);
        for (unsigned y=0; y<height; ++y)
            std::memcpy(out + y*width*4, readbackBytes + y*output.layout.Footprint.RowPitch, width*4);
        if (!changedReported && std::memcmp(dest, input, static_cast<size_t>(width)*height*4) != 0) {
            changedReported = true;
            std::fprintf(stderr, "[CaptureNR] Feature 18 evaluated and produced changed pixels\n");
        }
    }
};

// Keep the original frame flowing. Only the latest model request and result
// are retained; the filter thread never waits on a GPU fence.
class AsyncNeural {
    using Clock = std::chrono::steady_clock;
    struct Result {
        Clock::time_point sourceTime;
        std::vector<std::uint32_t> before, after;
    };
    Neural model;
    std::mutex gate;
    std::condition_variable ready;
    std::thread worker;
    std::vector<std::uint32_t> pending;
    std::shared_ptr<const Result> latest;
    Clock::time_point pendingTime;
    bool waiting{}, stopping{}, applied{};

    void process() {
        std::vector<std::uint32_t> input(pending.size());
        bool reset = true;
        while (true) {
            Clock::time_point time;
            {
                std::unique_lock lock(gate);
                ready.wait(lock, [&] { return stopping || waiting; });
                if (stopping) return;
                input.swap(pending);
                time = pendingTime;
                waiting = false;
            }
            auto result = std::make_shared<Result>();
            result->sourceTime = time;
            result->after.resize(input.size());
            model.run(input.data(), result->after.data(), reset);
            reset = false;
            result->before = input;
            {
                std::lock_guard lock(gate);
                latest = std::move(result);
            }
        }
    }

public:
    void init(unsigned w, unsigned h) {
        model.init(w, h);
        pending.resize(static_cast<size_t>(w) * h);
        worker = std::thread([this] {
            try { process(); }
            catch (const std::exception& e) {
                std::fprintf(stderr, "[CaptureNR] inference failed: %s\n", e.what());
                std::fflush(stderr);
                ExitProcess(31);
            }
        });
    }

    ~AsyncNeural() {
        if (!worker.joinable()) return;
        {
            std::lock_guard lock(gate);
            stopping = true;
        }
        ready.notify_one();
        worker.join();
    }

    void update(const std::uint32_t* input, std::uint32_t* output) {
        const auto now = Clock::now();
        std::shared_ptr<const Result> result;
        size_t count;
        {
            std::lock_guard lock(gate);
            count = pending.size();
            std::memcpy(pending.data(), input, count * sizeof(*input));
            pendingTime = now;
            waiting = true;
            result = latest;
        }
        ready.notify_one();

        // A result from before a cut or from a stalled worker must never
        // recolor the current scene. Even when reusing a fresh result we only
        // reuse the color delta, never an old frame.
        if (result && now - result->sourceTime <= std::chrono::milliseconds(65)) {
            unsigned change = 0;
            for (size_t s = 0; s < 96; ++s) {
                const size_t i = (s * 7433) % count;
                for (unsigned shift : {0u, 8u, 16u}) {
                    const int a = (input[i] >> shift) & 255;
                    const int b = (result->before[i] >> shift) & 255;
                    change += static_cast<unsigned>(a > b ? a - b : b - a);
                }
            }
            if (change < 96 * 3 * 24) {
                const auto zero = _mm_setzero_si128();
                auto blend = [&](auto a, auto b, auto c) {
                    return _mm_packus_epi16(
                        _mm_add_epi16(_mm_unpacklo_epi8(a, zero), _mm_sub_epi16(_mm_unpacklo_epi8(c, zero), _mm_unpacklo_epi8(b, zero))),
                        _mm_add_epi16(_mm_unpackhi_epi8(a, zero), _mm_sub_epi16(_mm_unpackhi_epi8(c, zero), _mm_unpackhi_epi8(b, zero))));
                };
                size_t i = 0;
                for (; i + 4 <= count; i += 4) {
                    auto load = [i](const std::uint32_t* p) { return _mm_loadu_si128(reinterpret_cast<const __m128i*>(p + i)); };
                    _mm_storeu_si128(reinterpret_cast<__m128i*>(output + i), blend(load(input), load(result->before.data()), load(result->after.data())));
                }
                if (i < count) std::memcpy(output + i, input + i, (count - i) * sizeof(*input));
                if (!applied) {
                    applied = true;
                    std::fprintf(stderr, "[CaptureNR] Async result applied to live frame\n");
                }
                return;
            }
        }
        std::memcpy(output, input, count * sizeof(*input));
    }
};

bool instance{};
struct FreiInfo {
    const char* name; const char* author; int type; int color;
    int version; int major; int minor; int parameters; const char* explanation;
};
} // namespace

extern "C" {
BOOL WINAPI DllMain(HINSTANCE module, DWORD event, LPVOID) {
    if (event == DLL_PROCESS_ATTACH) { selfModule = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
__declspec(dllexport) int f0r_init() { return 1; }
__declspec(dllexport) void f0r_deinit() {}
__declspec(dllexport) void f0r_get_plugin_info(FreiInfo* p) {
    *p = {"CapturePlayer NR", "CapturePlayer", 0, 1, 1, 1, 0, 0, "DLSS 5 NR (SDR)"};
}
__declspec(dllexport) void f0r_get_param_info(void*, int) {}
__declspec(dllexport) void f0r_set_param_value(void*, void*, int) {}
__declspec(dllexport) void f0r_get_param_value(void*, void*, int) {}
__declspec(dllexport) void* f0r_construct(unsigned w, unsigned h) {
    if (instance) return nullptr;
    auto model = std::make_unique<AsyncNeural>();
    try { model->init(w, h); }
    catch (const std::exception& e) {
        std::fprintf(stderr, "[CaptureNR] initialization failed: %s\n", e.what());
        return nullptr;
    }
    instance = true;
    return model.release();
}
__declspec(dllexport) void f0r_destruct(void* p) { delete static_cast<AsyncNeural*>(p); instance = false; }
__declspec(dllexport) void f0r_update(void* p, double, const std::uint32_t* in, std::uint32_t* out) {
    static_cast<AsyncNeural*>(p)->update(in, out);
}
}
