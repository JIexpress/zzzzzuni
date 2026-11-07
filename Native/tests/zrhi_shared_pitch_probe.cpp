// =============================================================================
// zrhi_shared_pitch_probe.cpp —— 共享纹理跨侧回读的"行距/布局"诊断程序
//（TASK-B-UNITY-05）
//
// 用途：把"共享纹理整面回读只有 1/8 行正确"从一句现象量化成数字。
// 全程不渲染，只做拷贝，把"布局"隔离成唯一变量：
//   1) 走 ZRender 的 CreateSharedTarget 建纹理 → 取 NT 句柄 → 宿主 OpenSharedHandle。
//      同一块显存上于是有**两个 ID3D12Resource 对象**：resOwn（DLL 内那个）与
//      resImported（宿主 Open 出来的那个，= Unity CreateExternalTexture 的等价动作）。
//   2) 对照：再建一张**普通（非共享堆）**同规格纹理 resPlain。
//   3) 每张资源各自 GetCopyableFootprints 逐行写/读确定性图案
//      （每像素 BGRA = (x 低 8 位, x 高 8 位, y, 255)），逐行比对。
//   4) 单行探针：整面清零后只写第 0 行，回读看还有哪些行也是这份内容。
//
// 输出的关键数字：每张资源的 RowPitch / totalBytes / 分配尺寸，以及"正确行集合"
// 的规律（每 8 行错 7 行？只对前 1/8？还是周期性重复？）——根因由它决定。
//
// 用法：zrhi_shared_pitch_probe.exe [width height]，默认 640x480。
// 退出码 0 = 所有路径逐像素一致。
// =============================================================================
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

#include "zplugin/zplugin.h" // 错误码 / 函数指针类型（不链接实现）

using Microsoft::WRL::ComPtr;

namespace {

struct Gpu {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE event = nullptr;
    uint64_t fenceValue = 0u;

    bool Init(ID3D12Device* dev)
    {
        device = dev;
        D3D12_COMMAND_QUEUE_DESC qd {};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)))
            || FAILED(device->CreateCommandAllocator(
                   D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)))
            || FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                   allocator.Get(), nullptr, IID_PPV_ARGS(&list)))
            || FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)))) {
            return false;
        }
        list->Close();
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        return event != nullptr;
    }

    bool Begin()
    {
        allocator->Reset();
        return SUCCEEDED(list->Reset(allocator.Get(), nullptr));
    }

    bool ExecuteAndWait()
    {
        if (FAILED(list->Close())) {
            return false;
        }
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1u, lists);
        fenceValue += 1u;
        if (FAILED(queue->Signal(fence.Get(), fenceValue))) {
            return false;
        }
        if (fence->GetCompletedValue() < fenceValue) {
            if (FAILED(fence->SetEventOnCompletion(fenceValue, event))) {
                return false;
            }
            if (WaitForSingleObject(event, 30000u) != WAIT_OBJECT_0) {
                return false;
            }
        }
        return true;
    }
};

/// 一张资源在"可拷贝脚印"里的尺寸：RowPitch 是权威行距，不是 width*4
struct Footprint {
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    UINT numRows = 0u;
    UINT64 rowSizeBytes = 0u; // 一行"有效"字节数（= width * bpp）
    UINT64 totalBytes = 0u;
};

void GetFootprint(ID3D12Device* device, ID3D12Resource* res, Footprint* out)
{
    const D3D12_RESOURCE_DESC desc = res->GetDesc();
    device->GetCopyableFootprints(
        &desc, 0u, 1u, 0u, &out->placed, &out->numRows, &out->rowSizeBytes, &out->totalBytes);
}

bool CreateBuffer(ID3D12Device* device, D3D12_HEAP_TYPE type, uint64_t bytes,
    D3D12_RESOURCE_STATES state, ComPtr<ID3D12Resource>* out)
{
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = type;
    D3D12_RESOURCE_DESC desc {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = bytes;
    desc.Height = 1u;
    desc.DepthOrArraySize = 1u;
    desc.MipLevels = 1u;
    desc.SampleDesc.Count = 1u;
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    return SUCCEEDED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state,
        nullptr, IID_PPV_ARGS(out->ReleaseAndGetAddressOf())));
}

// 图案编码（宽度/高度均 ≤ 1023 时唯一）：
//   B = x 低 8 位，G 的 bit1:0 = x 高 2 位，G 的 bit3:2 = y 高 2 位，
//   R = y 低 8 位，A = 255。
// 为什么 y 不能只放 R：R 是 8 位，y ≥ 256 的行会和 y-256 行完全相同，
// 于是"内容重复"这种错觉会被自己的图案伪造出来（本任务真踩过这一脚）。
constexpr uint32_t kMaxDim = 1023u;

void FillPattern(std::vector<uint8_t>* buffer, uint32_t pitch, uint32_t width, uint32_t height)
{
    buffer->assign(static_cast<size_t>(pitch) * height, 0u);
    for (uint32_t y = 0; y < height; ++y) {
        uint8_t* row = buffer->data() + static_cast<size_t>(y) * pitch;
        for (uint32_t x = 0; x < width; ++x) {
            uint8_t* px = row + static_cast<size_t>(x) * 4u;
            px[0] = static_cast<uint8_t>(x & 0xFFu);
            px[1] = static_cast<uint8_t>(((x >> 8) & 0x03u) | (((y >> 8) & 0x03u) << 2));
            px[2] = static_cast<uint8_t>(y & 0xFFu);
            px[3] = 255u;
        }
    }
}

bool Transition(Gpu& gpu, ID3D12Resource* res, D3D12_RESOURCE_STATES before,
    D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER barrier {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = res;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    gpu.list->ResourceBarrier(1u, &barrier);
    return true;
}

/// 宿主侧写纹理：upload（按 fp 逐行）→ res，可选只写 dstY..dstY+rows 行
bool WriteTexture(Gpu& gpu, ID3D12Resource* res, const Footprint& fp,
    const std::vector<uint8_t>& pattern, uint32_t pitch, uint32_t srcFirstRow, uint32_t rows)
{
    // 上传缓冲按"行距 × 全高"给足：CopyTextureRegion 传 nullptr 的 src box 会把整个
    // 子资源拷一遍（height 行），而 totalBytes 的最后一行只算 rowBytes —— 按 totalBytes
    // 建缓冲再逐行 memcpy 越界 252B（本探针在 641x357 上真踩过，且只在部分宽度崩）。
    const uint64_t uploadBytes
        = static_cast<uint64_t>(fp.placed.Footprint.RowPitch) * fp.numRows;
    ComPtr<ID3D12Resource> upload;
    if (!CreateBuffer(gpu.device.Get(), D3D12_HEAP_TYPE_UPLOAD, uploadBytes,
            D3D12_RESOURCE_STATE_GENERIC_READ, &upload)) {
        return false;
    }
    void* mapped = nullptr;
    if (FAILED(upload->Map(0, nullptr, &mapped)) || mapped == nullptr) {
        return false;
    }
    std::memset(mapped, 0, static_cast<size_t>(uploadBytes));
    for (uint32_t y = 0; y < rows; ++y) {
        std::memcpy(static_cast<uint8_t*>(mapped) + static_cast<size_t>(y) * fp.placed.Footprint.RowPitch,
            pattern.data() + static_cast<size_t>(srcFirstRow + y) * pitch, pitch);
    }
    upload->Unmap(0, nullptr);

    D3D12_TEXTURE_COPY_LOCATION src {};
    src.pResource = upload.Get();
    src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    src.PlacedFootprint = fp.placed;
    D3D12_TEXTURE_COPY_LOCATION dst {};
    dst.pResource = res;
    dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    dst.SubresourceIndex = 0u;

    if (!gpu.Begin()) {
        return false;
    }
    Transition(gpu, res, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
    gpu.list->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, nullptr);
    Transition(gpu, res, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON);
    return gpu.ExecuteAndWait();
}

/// 纹理 → READBACK（用该资源自己的脚印），返回原始字节（按该脚印行距排布）
///
/// 读前先把 READBACK 缓冲整块填成哨兵 0x77：这样"拷贝没有覆盖到的字节"一眼可辨
/// （0x77 = 没写，纯 0 = 写了零），用来区分"写进纹理时就重复了"与"读出来时被截断"。
bool ReadTexture(Gpu& gpu, ID3D12Resource* res, std::vector<uint8_t>* out, Footprint* outFp,
    D3D12_RESOURCE_STATES actualState = D3D12_RESOURCE_STATE_COMMON,
    bool wrongStateBefore = false)
{
    Footprint fp;
    GetFootprint(gpu.device.Get(), res, &fp);
    ComPtr<ID3D12Resource> readback;
    if (!CreateBuffer(gpu.device.Get(), D3D12_HEAP_TYPE_READBACK, fp.totalBytes,
            D3D12_RESOURCE_STATE_COPY_DEST, &readback)) {
        return false;
    }
    ComPtr<ID3D12Resource> poison;
    if (!CreateBuffer(gpu.device.Get(), D3D12_HEAP_TYPE_UPLOAD, fp.totalBytes,
            D3D12_RESOURCE_STATE_GENERIC_READ, &poison)) {
        return false;
    }
    void* poisonMapped = nullptr;
    if (FAILED(poison->Map(0, nullptr, &poisonMapped)) || poisonMapped == nullptr) {
        return false;
    }
    std::memset(poisonMapped, 0x77, static_cast<size_t>(fp.totalBytes));
    poison->Unmap(0, nullptr);

    D3D12_TEXTURE_COPY_LOCATION dst {};
    dst.pResource = readback.Get();
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint = fp.placed;
    D3D12_TEXTURE_COPY_LOCATION src {};
    src.pResource = res;
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    src.SubresourceIndex = 0u;
    if (!gpu.Begin()) {
        return false;
    }
    gpu.list->CopyBufferRegion(readback.Get(), 0u, poison.Get(), 0u, fp.totalBytes);
    // wrongStateBefore = 复刻 B-UNITY-02 自证程序里那条回读：屏障把 StateBefore 写死成
    // COMMON，而纹理实际在 PIXEL_SHADER_RESOURCE（ZRender 渲染完就是那个态）。
    const D3D12_RESOURCE_STATES claimed
        = wrongStateBefore ? D3D12_RESOURCE_STATE_COMMON : actualState;
    Transition(gpu, res, claimed, D3D12_RESOURCE_STATE_COPY_SOURCE);
    gpu.list->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, nullptr);
    Transition(gpu, res, D3D12_RESOURCE_STATE_COPY_SOURCE, actualState);
    if (!gpu.ExecuteAndWait()) {
        return false;
    }
    void* mapped = nullptr;
    if (FAILED(readback->Map(0, nullptr, &mapped)) || mapped == nullptr) {
        return false;
    }
    out->assign(static_cast<size_t>(fp.totalBytes), 0u);
    std::memcpy(out->data(), mapped, static_cast<size_t>(fp.totalBytes));
    readback->Unmap(0, nullptr);
    *outFp = fp;
    return true;
}

inline void DecodePixel(const uint8_t* px, int* x, int* y)
{
    *x = static_cast<int>(px[0]) | ((static_cast<int>(px[1]) & 0x03) << 8);
    *y = static_cast<int>(px[2]) | (((static_cast<int>(px[1]) >> 2) & 0x03) << 8);
}

/// 逐行判定：本行是否与图案第 y 行完全一致；不一致时记下它实际是图案的第几行
struct RowReport {
    uint32_t goodRows = 0u;
    uint32_t zeroRows = 0u;
    uint32_t poisonRows = 0u; // 整行仍是 0x77 = 这次拷贝根本没覆盖到
    std::vector<int> goodFlags;
    std::vector<int> actualRow;
};

void Analyze(const std::vector<uint8_t>& got, uint32_t pitch, uint32_t width, uint32_t height,
    RowReport* rep)
{
    rep->goodFlags.assign(height, 0);
    rep->actualRow.assign(height, -1);
    for (uint32_t y = 0; y < height; ++y) {
        const uint8_t* row = got.data() + static_cast<size_t>(y) * pitch;
        bool allZero = true;
        bool allPoison = true;
        for (uint32_t x = 0; x < width * 4u; ++x) {
            if (row[x] != 0u) {
                allZero = false;
            }
            if (row[x] != 0x77u) {
                allPoison = false;
            }
            if (!allZero && !allPoison) {
                break;
            }
        }
        if (allPoison) {
            ++rep->poisonRows;
            rep->actualRow[y] = -2;
            continue;
        }
        if (allZero) {
            ++rep->zeroRows;
            continue;
        }
        bool same = true;
        for (uint32_t x = 0; x < width; ++x) {
            const uint8_t* px = row + static_cast<size_t>(x) * 4u;
            int dx = 0;
            int dy = 0;
            DecodePixel(px, &dx, &dy);
            if (dx != static_cast<int>(x) || dy != static_cast<int>(y) || px[3] != 255u) {
                same = false;
                break;
            }
        }
        if (same) {
            rep->goodFlags[y] = 1;
            rep->actualRow[y] = static_cast<int>(y);
            ++rep->goodRows;
        } else {
            int cx = 0;
            int cy = 0;
            DecodePixel(row, &cx, &cy);
            rep->actualRow[y] = cy;
        }
    }
}

/// 把 [0..height) 压成"区间"打印：`rows 0-255 OK | rows 256-479 = pattern row 0..223`
void PrintShape(const RowReport& rep, uint32_t height, uint32_t width)
{
    (void)width;
    auto category = [&rep](uint32_t y) {
        if (rep.goodFlags[y] != 0) {
            return 1;
        }
        if (rep.actualRow[y] == -2) {
            return -2;
        }
        return (rep.actualRow[y] < 0) ? -1 : 0;
    };
    std::printf("  shape: ");
    uint32_t y = 0u;
    while (y < height) {
        const int cat = category(y);
        uint32_t end = y;
        while (end + 1u < height && category(end + 1u) == cat) {
            ++end;
        }
        if (cat == 1) {
            std::printf("rows %u-%u OK", y, end);
        } else if (cat == -2) {
            std::printf("rows %u-%u UNTOUCHED(0x77)", y, end);
        } else if (cat == -1) {
            std::printf("rows %u-%u ZERO", y, end);
        } else {
            std::printf("rows %u-%u = pattern row %d..%d", y, end, rep.actualRow[y],
                rep.actualRow[end]);
        }
        if (end + 1u < height) {
            std::printf(" | ");
        }
        y = end + 1u;
    }
    std::printf("\n");
}

void PrintAlloc(ID3D12Device* device, ID3D12Resource* res, const char* label)
{
    const D3D12_RESOURCE_DESC desc = res->GetDesc();
    const D3D12_RESOURCE_ALLOCATION_INFO info = device->GetResourceAllocationInfo(0u, 1u, &desc);
    std::printf("%-10s : %ux%u fmt=%u layout=%u flags=0x%X alloc=%llu align=%llu\n", label,
        static_cast<unsigned>(desc.Width), static_cast<unsigned>(desc.Height),
        static_cast<unsigned>(desc.Format), static_cast<unsigned>(desc.Layout),
        static_cast<unsigned>(desc.Flags), static_cast<unsigned long long>(info.SizeInBytes),
        static_cast<unsigned long long>(info.Alignment));
}

} // namespace

int main(int argc, char** argv)
{
    uint32_t width = 640u;
    uint32_t height = 480u;
    if (argc >= 3) {
        width = static_cast<uint32_t>(std::atoi(argv[1]));
        height = static_cast<uint32_t>(std::atoi(argv[2]));
    }
    std::printf("zrhi_shared_pitch_probe: shared texture cross-side readback (%ux%u)\n", width, height);
    if (width == 0u || height == 0u || width > kMaxDim || height > kMaxDim) {
        std::printf("FAILED: width/height must be 1..%u (pattern encoding limit)\n", kMaxDim);
        return 2;
    }

    char dllPath[MAX_PATH] = {};
    {
        char module[MAX_PATH] = {};
        if (GetModuleFileNameA(nullptr, module, MAX_PATH) == 0u) {
            return 2;
        }
        char* slash = std::strrchr(module, '\\');
        if (slash == nullptr) {
            return 2;
        }
        slash[1] = '\0';
        std::snprintf(dllPath, sizeof(dllPath), "%szrender.dll", module);
    }
    HMODULE dll = LoadLibraryA(dllPath);
    if (dll == nullptr) {
        std::printf("FAILED: LoadLibrary(%s) err=%lu\n", dllPath, GetLastError());
        return 2;
    }
    auto attachTex = reinterpret_cast<ZrUnityAttachDeviceFromTextureFn>(
        GetProcAddress(dll, "ZR_Unity_AttachDeviceFromTexture"));
    auto createTarget = reinterpret_cast<ZrUnityCreateSharedTargetFn>(
        GetProcAddress(dll, "ZR_Unity_CreateSharedTarget"));
    auto getHandle = reinterpret_cast<ZrUnityGetSharedTargetHandleFn>(
        GetProcAddress(dll, "ZR_Unity_GetSharedTargetHandle"));
    auto getResource = reinterpret_cast<ZrUnityGetSharedTargetResourceFn>(
        GetProcAddress(dll, "ZR_Unity_GetSharedTargetResource"));
    auto releaseTarget = reinterpret_cast<ZrUnityReleaseSharedTargetFn>(
        GetProcAddress(dll, "ZR_Unity_ReleaseSharedTarget"));
    auto readPixels = reinterpret_cast<ZrUnityReadTargetPixelsFn>(
        GetProcAddress(dll, "ZR_Unity_ReadTargetPixels"));
    if (attachTex == nullptr || createTarget == nullptr || getHandle == nullptr
        || getResource == nullptr || releaseTarget == nullptr || readPixels == nullptr) {
        std::printf("FAILED: missing export(s)\n");
        return 2;
    }

    // ---- 宿主设备（扮演 Unity）--------------------------------------------------
    ComPtr<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory2(0u, IID_PPV_ARGS(&factory)))) {
        return 2;
    }
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0u; i < 8u; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        if (factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        DXGI_ADAPTER_DESC1 desc {};
        candidate->GetDesc1(&desc);
        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u) {
            adapter = candidate;
            std::printf("adapter: %ls\n", desc.Description);
            break;
        }
    }
    ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)))) {
        std::printf("FAILED: D3D12CreateDevice\n");
        return 2;
    }
    Gpu gpu;
    if (!gpu.Init(device.Get())) {
        return 2;
    }

    // 接入用的"探针纹理"（与 B-UNITY-02 自证程序同一手法）
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC probeDesc {};
    probeDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    probeDesc.Width = 64u;
    probeDesc.Height = 64u;
    probeDesc.DepthOrArraySize = 1u;
    probeDesc.MipLevels = 1u;
    probeDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    probeDesc.SampleDesc.Count = 1u;
    probeDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    ComPtr<ID3D12Resource> probeTex;
    if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &probeDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&probeTex)))) {
        return 2;
    }
    if (attachTex(static_cast<void*>(probeTex.Get())) != ZR_UNITY_OK) {
        std::printf("FAILED: AttachDeviceFromTexture\n");
        return 2;
    }
    if (createTarget(width, height) != ZR_UNITY_OK) {
        std::printf("FAILED: CreateSharedTarget(%u,%u)\n", width, height);
        return 2;
    }
    uint64_t handle = 0u;
    void* ownRaw = nullptr;
    if (getHandle(&handle) != ZR_UNITY_OK || getResource(&ownRaw) != ZR_UNITY_OK
        || ownRaw == nullptr) {
        std::printf("FAILED: handle/resource\n");
        return 2;
    }
    auto* resOwn = static_cast<ID3D12Resource*>(ownRaw);
    ComPtr<ID3D12Resource> resImported;
    if (FAILED(device->OpenSharedHandle(
            reinterpret_cast<HANDLE>(static_cast<uintptr_t>(handle)), IID_PPV_ARGS(&resImported)))) {
        std::printf("FAILED: OpenSharedHandle\n");
        return 2;
    }

    // 对照：普通（非共享堆）纹理，同规格同格式
    D3D12_RESOURCE_DESC plainDesc {};
    plainDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    plainDesc.Width = width;
    plainDesc.Height = height;
    plainDesc.DepthOrArraySize = 1u;
    plainDesc.MipLevels = 1u;
    plainDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    plainDesc.SampleDesc.Count = 1u;
    plainDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    ComPtr<ID3D12Resource> resPlain;
    if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &plainDesc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&resPlain)))) {
        std::printf("FAILED: plain control texture\n");
        return 2;
    }

    // ---- 描述 / 脚印 / 分配（根因证据）------------------------------------------
    Footprint fpOwn;
    Footprint fpImp;
    Footprint fpPlain;
    GetFootprint(device.Get(), resOwn, &fpOwn);
    GetFootprint(device.Get(), resImported.Get(), &fpImp);
    GetFootprint(device.Get(), resPlain.Get(), &fpPlain);
    std::printf("\n---- descriptors ----\n");
    PrintAlloc(device.Get(), resOwn, "own");
    PrintAlloc(device.Get(), resImported.Get(), "imported");
    PrintAlloc(device.Get(), resPlain.Get(), "plain");
    const uint64_t assumed = static_cast<uint64_t>(width) * 4u;
    std::printf("\n---- footprints (GetCopyableFootprints) ----\n");
    std::printf("assumed width*4: %llu B/row, %llu B total (width*4*height)\n",
        static_cast<unsigned long long>(assumed),
        static_cast<unsigned long long>(assumed * height));
    std::printf("own      RowPitch=%6u (x%.3f) rows=%u rowBytes=%llu total=%llu\n",
        static_cast<unsigned>(fpOwn.placed.Footprint.RowPitch),
        static_cast<double>(fpOwn.placed.Footprint.RowPitch) / static_cast<double>(assumed),
        static_cast<unsigned>(fpOwn.numRows), static_cast<unsigned long long>(fpOwn.rowSizeBytes),
        static_cast<unsigned long long>(fpOwn.totalBytes));
    std::printf("imported RowPitch=%6u (x%.3f) rows=%u rowBytes=%llu total=%llu\n",
        static_cast<unsigned>(fpImp.placed.Footprint.RowPitch),
        static_cast<double>(fpImp.placed.Footprint.RowPitch) / static_cast<double>(assumed),
        static_cast<unsigned>(fpImp.numRows), static_cast<unsigned long long>(fpImp.rowSizeBytes),
        static_cast<unsigned long long>(fpImp.totalBytes));
    std::printf("plain    RowPitch=%6u (x%.3f) rows=%u rowBytes=%llu total=%llu\n",
        static_cast<unsigned>(fpPlain.placed.Footprint.RowPitch),
        static_cast<double>(fpPlain.placed.Footprint.RowPitch) / static_cast<double>(assumed),
        static_cast<unsigned>(fpPlain.numRows),
        static_cast<unsigned long long>(fpPlain.rowSizeBytes),
        static_cast<unsigned long long>(fpPlain.totalBytes));

    // ---- 图案 + 三组整面往返 ----------------------------------------------------
    std::vector<uint8_t> pattern;
    FillPattern(&pattern, fpOwn.placed.Footprint.RowPitch, width, height);

    struct Case {
        const char* label;
        ID3D12Resource* writer;
        ID3D12Resource* reader;
        Footprint fpWrite;
    };
    Case cases[] = {
        {"plain    -> plain    (non-shared control)", resPlain.Get(), resPlain.Get(), fpPlain},
        {"own      -> own      (ZRender-side self read)", resOwn, resOwn, fpOwn},
        {"own      -> imported (Unity-side read) ", resOwn, resImported.Get(), fpOwn},
        {"imported -> own      (reverse direction) ", resImported.Get(), resOwn, fpImp},
    };
    int failures = 0;
    std::vector<uint8_t> gotGood; // 最近一次"own -> imported"的正确回读（供错行距推演复用）
    uint32_t pitchGood = 0u;
    for (const Case& c : cases) {
        if (!WriteTexture(gpu, c.writer, c.fpWrite, pattern,
                fpOwn.placed.Footprint.RowPitch, 0u, height)) {
            std::printf("FAILED: write (%s)\n", c.label);
            return 2;
        }
        std::vector<uint8_t> got;
        Footprint fpRead {};
        if (!ReadTexture(gpu, c.reader, &got, &fpRead)) {
            std::printf("FAILED: read (%s)\n", c.label);
            return 2;
        }
        RowReport rep;
        Analyze(got, fpRead.placed.Footprint.RowPitch, width, height, &rep);
        std::printf("\n---- %s ----\n", c.label);
        std::printf("  rows: total=%u good=%u zero=%u untouched=%u bad=%u (read RowPitch=%u)\n",
            height, rep.goodRows, rep.zeroRows, rep.poisonRows,
            height - rep.goodRows - rep.zeroRows - rep.poisonRows,
            static_cast<unsigned>(fpRead.placed.Footprint.RowPitch));
        PrintShape(rep, height, width);
        if (rep.goodRows != height) {
            ++failures;
        }
        if (c.reader == resImported.Get()) {
            gotGood = got;
            pitchGood = fpRead.placed.Footprint.RowPitch;
        }
    }

    // ---- 产品路径：ZR_Unity_ReadTargetPixels 读同一张图案 -------------------------
    // 调用方缓冲故意用 width*4 + 64 的行距：验证函数按调用方行距逐行写，
    // 而不是"整块 memcpy 紧凑布局"。
    if (!gotGood.empty()) {
        const uint32_t dstPitch = static_cast<uint32_t>(assumed) + 64u;
        std::vector<uint8_t> dst(static_cast<size_t>(dstPitch) * height, 0x77u);
        uint32_t rw = 0u;
        uint32_t rh = 0u;
        const int rc = readPixels(dst.data(), dstPitch, &rw, &rh);
        RowReport rep;
        Analyze(dst, dstPitch, width, height, &rep);
        std::printf("\n---- product API: ZR_Unity_ReadTargetPixels (dstPitch=%u) ----\n", dstPitch);
        std::printf("  rc=%d size=%ux%u rows: total=%u good=%u zero=%u bad=%u\n", rc, rw, rh, height,
            rep.goodRows, rep.zeroRows, height - rep.goodRows - rep.zeroRows);
        PrintShape(rep, height, width);
        if (rc != 0 || rw != width || rh != height || rep.goodRows != height) {
            ++failures;
        }
    }

    // ---- 复刻 B-UNITY-02 那条回读：屏障 StateBefore 谎报 COMMON -------------------
    // ZRender 渲染完把纹理留在 PIXEL_SHADER_RESOURCE（给 Unity 采样）。自证程序里
    // ReadbackTexture 却写死 `prevState = COMMON`，于是屏障的 StateBefore 与实际不符。
    // 这一组就是"用错误的前态去读同一张纹理"的现场数字。
    {
        if (!WriteTexture(gpu, resOwn, fpOwn, pattern, fpOwn.placed.Footprint.RowPitch, 0u,
                height)) {
            std::printf("FAILED: write (psr case)\n");
            return 2;
        }
        // 把纹理推进到 PSR（ZRender 帧末屏障干的事）
        if (gpu.Begin()) {
            Transition(gpu, resOwn, D3D12_RESOURCE_STATE_COMMON,
                D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            gpu.ExecuteAndWait();
        }
        std::vector<uint8_t> got;
        Footprint fpRead {};
        if (!ReadTexture(gpu, resImported.Get(), &got, &fpRead,
                D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
                    | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                true)) {
            std::printf("FAILED: read (psr case)\n");
            return 2;
        }
        RowReport rep;
        Analyze(got, fpRead.placed.Footprint.RowPitch, width, height, &rep);
        std::printf("\n---- own -> imported, barrier StateBefore lies COMMON ----\n");
        std::printf("  rows: total=%u good=%u zero=%u untouched=%u bad=%u\n", height, rep.goodRows,
            rep.zeroRows, rep.poisonRows, height - rep.goodRows - rep.zeroRows - rep.poisonRows);
        PrintShape(rep, height, width);
        // 归还到 COMMON，后面几段还要用
        if (gpu.Begin()) {
            Transition(gpu, resOwn,
                D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                D3D12_RESOURCE_STATE_COMMON);
            gpu.ExecuteAndWait();
        }
    }

    // ---- 错行距推演：把正确回读的数据分别按 width*4 / (RowPitch/8) 解释 -----------
    // 这一组回答"假设行距会怎么坏"，是根因的另一半证据：
    //   Pa = width*4    → 非 256 对齐宽度上 Pa < Pr，逐行错位累积（剪切）
    //   Pa = Pr/8       → 每 8 行才回到一次行边界 = 报告里的"1/8 行正确"
    if (!gotGood.empty()) {
        struct Assumed {
            const char* label;
            uint32_t pitch;
        };
        Assumed assumedList[2] = {
            {"Pa = width*4", static_cast<uint32_t>(assumed)},
            {"Pa = RowPitch/8", pitchGood / 8u},
        };
        for (const Assumed& a : assumedList) {
            if (a.pitch == 0u) {
                continue;
            }
            std::vector<uint8_t> reshaped(static_cast<size_t>(a.pitch) * height, 0x77u);
            size_t copied = 0u;
            for (uint32_t y = 0; y < height; ++y) {
                const size_t srcOff = static_cast<size_t>(y) * a.pitch;
                if (srcOff + a.pitch > gotGood.size()) {
                    break;
                }
                std::memcpy(reshaped.data() + static_cast<size_t>(y) * a.pitch,
                    gotGood.data() + srcOff, a.pitch);
                ++copied;
            }
            RowReport rep;
            Analyze(reshaped, a.pitch, width, height, &rep);
            std::printf("\n---- interpret with %s = %u (real RowPitch=%u, ratio x%.3f) ----\n", a.label,
                a.pitch, pitchGood,
                static_cast<double>(pitchGood) / static_cast<double>(a.pitch));
            std::printf("  rows: total=%u good=%u zero=%u untouched=%u bad=%u (rows covered=%zu)\n",
                height, rep.goodRows, rep.zeroRows, rep.poisonRows,
                height - rep.goodRows - rep.zeroRows - rep.poisonRows, copied);
            PrintShape(rep, height, width);
        }
    }

    // ---- "8 倍行距"推演：写侧按 8×P 落盘、读侧按 P 扫描 -------------------------
    // 这一组回答"1/8 行正确、其余为 0"这个签名的唯一可能机制：内容只落在每 8 行
    // 的第一行上（其余 7 行没被写过 → 读到 0）。若真是"假设行距差 1.039 倍"，
    // 得到的是剪切（上一条 Pa=width*4），不会是 1/8 + 其余为 0。
    if (!gotGood.empty()) {
        const uint32_t pitch8 = pitchGood * 8u;
        std::vector<uint8_t> sparse(static_cast<size_t>(pitch8) * height, 0u);
        for (uint32_t y = 0; y < height; ++y) {
            const size_t srcOff = static_cast<size_t>(y) * pitchGood;
            if (srcOff >= gotGood.size()) {
                break;
            }
            // 最后一行只有 rowSizeBytes 有效字节（totalBytes = pitch*(h-1) + rowSize）
            size_t bytes = pitchGood;
            if (srcOff + bytes > gotGood.size()) {
                bytes = gotGood.size() - srcOff;
            }
            std::memcpy(sparse.data() + static_cast<size_t>(y) * pitch8,
                gotGood.data() + srcOff, bytes);
        }
        RowReport rep;
        Analyze(sparse, pitchGood, width, height, &rep);
        std::printf("\n---- writer pitch = 8x (%u) / reader %u : 1-in-8 signature ----\n", pitch8, pitchGood);
        std::printf("  rows: total=%u good=%u zero=%u untouched=%u bad=%u\n", height, rep.goodRows,
            rep.zeroRows, rep.poisonRows, height - rep.goodRows - rep.zeroRows - rep.poisonRows);
        PrintShape(rep, height, width);
    }

    // ---- 单行探针：整面清零后只写第 0 行，看内容"复制"到了哪些行 -----------------
    {
        std::vector<uint8_t> zero;
        zero.assign(static_cast<size_t>(fpOwn.placed.Footprint.RowPitch) * height, 0u);
        std::vector<uint8_t> oneRow;
        oneRow.assign(static_cast<size_t>(fpOwn.placed.Footprint.RowPitch), 0u);
        for (uint32_t x = 0; x < width; ++x) {
            uint8_t* px = oneRow.data() + static_cast<size_t>(x) * 4u;
            px[0] = static_cast<uint8_t>(x & 0xFFu);
            px[1] = static_cast<uint8_t>((x >> 8) & 0xFFu);
            px[2] = 0xEEu; // 标记行：R=0xEE 表示"第 0 行内容"
            px[3] = 255u;
        }
        if (WriteTexture(gpu, resOwn, fpOwn, zero, fpOwn.placed.Footprint.RowPitch, 0u, height)
            && WriteTexture(gpu, resOwn, fpOwn, oneRow, fpOwn.placed.Footprint.RowPitch, 0u, 1u)) {
            std::vector<uint8_t> got;
            Footprint fpRead {};
            if (ReadTexture(gpu, resOwn, &got, &fpRead)) {
                std::printf("\n---- row-0s alone (write 1 row at dstY=0, read all) ----\n  rows containing row-0 content:");
                uint32_t hits = 0u;
                for (uint32_t y = 0; y < height; ++y) {
                    const uint8_t* row = got.data()
                        + static_cast<size_t>(y) * fpRead.placed.Footprint.RowPitch;
                    if (row[2] == 0xEEu) {
                        std::printf(" %u", y);
                        ++hits;
                    }
                }
                std::printf("  (count=%u)\n", hits);
            }
        }
    }

    // ---- 渲染后的真实纹理：own / imported 两条回读路径 ----------------------------
    // 前面的图案是"拷贝写进去的"，这一组换成"ZRender 渲染出来的"，是 B-UNITY-02
    // 报告 1/8 时的同一现场。两条路径分开跑，谁的锅一目了然。
    {
        auto renderFrame = reinterpret_cast<ZrUnityRenderFrameFn>(
            GetProcAddress(dll, "ZR_Unity_RenderFrame"));
        if (renderFrame != nullptr) {
            for (int i = 0; i < 4; ++i) {
                renderFrame();
            }
            const D3D12_RESOURCE_STATES srvState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
                | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
            ID3D12Resource* readers[5] = {resOwn, resImported.Get(), resImported.Get(),
                resImported.Get(), resImported.Get()};
            const char* labels[5] = {"rendered own-side read          ",
                "rendered imported read (PSR)    ",
                "rendered imported read (lie COMMON)",
                "rendered imported read (@1280x720)",
                "rendered imported read (own->COMMON first)"};
            for (int i = 0; i < 5; ++i) {
                if (i == 3) {
                    // 再多跑 4 帧：若 1/8 是"渲染还没完成"，这一组会变干净
                    for (int k = 0; k < 4; ++k) {
                        renderFrame();
                    }
                }
                if (i == 4) {
                    // 先把 ZRender 自己那个对象（真正被渲染的那个）落回 COMMON，
                    // 再通过 imported 读：用来分离"状态"与"对象身份"两个变量。
                    if (gpu.Begin()) {
                        Transition(gpu, resOwn,
                            D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
                                | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                            D3D12_RESOURCE_STATE_COMMON);
                        gpu.ExecuteAndWait();
                    }
                }
                std::vector<uint8_t> got;
                Footprint fpRead {};
                if (!ReadTexture(gpu, readers[i], &got, &fpRead,
                        (i == 4) ? D3D12_RESOURCE_STATE_COMMON : srvState, i == 2)) {
                    std::printf("FAILED: rendered read (%s)\n", labels[i]);
                    return 2;
                }
                const uint32_t pitch = fpRead.placed.Footprint.RowPitch;
                uint32_t zeroRows = 0u;
                std::printf("\n---- %s----\n  zero-row ranges:", labels[i]);
                for (uint32_t y = 0; y < height;) {
                    const uint8_t* row = got.data() + static_cast<size_t>(y) * pitch;
                    bool all = true;
                    for (uint32_t x = 0; x < width * 4u; ++x) {
                        if (row[x] != 0u) {
                            all = false;
                            break;
                        }
                    }
                    if (!all) {
                        ++y;
                        continue;
                    }
                    uint32_t end = y;
                    while (end + 1u < height) {
                        const uint8_t* r2 = got.data() + static_cast<size_t>(end + 1u) * pitch;
                        bool a2 = true;
                        for (uint32_t x = 0; x < width * 4u; ++x) {
                            if (r2[x] != 0u) {
                                a2 = false;
                                break;
                            }
                        }
                        if (!a2) {
                            break;
                        }
                        ++end;
                    }
                    std::printf(" %u-%u", y, end);
                    zeroRows += end - y + 1u;
                    y = end + 1u;
                }
                const uint8_t* px = got.data();
                std::printf("\n  zeroRows=%u/%u  corner BGRA=(%u,%u,%u,%u)\n", zeroRows, height,
                    static_cast<unsigned>(px[0]), static_cast<unsigned>(px[1]),
                    static_cast<unsigned>(px[2]), static_cast<unsigned>(px[3]));
                if (i == 0 || i == 1) {
                    const uint32_t probeRows[6] = {0u, 1u, 8u, 9u, 240u, 479u};
                    std::printf("  sample rows (x=0 and x=%u):", width / 2u);
                    for (uint32_t k = 0; k < 6u; ++k) {
                        const uint32_t y = probeRows[k];
                        if (y >= height) {
                            continue; // 探针行只对 480 高的目标有意义
                        }
                        const uint8_t* row = got.data() + static_cast<size_t>(y) * pitch;
                        const uint8_t* p0 = row;
                        const uint8_t* p1 = row + static_cast<size_t>(width / 2u) * 4u;
                        std::printf("  y=%u [%u,%u,%u,%u]/[%u,%u,%u,%u]", y,
                            static_cast<unsigned>(p0[0]), static_cast<unsigned>(p0[1]),
                            static_cast<unsigned>(p0[2]), static_cast<unsigned>(p0[3]),
                            static_cast<unsigned>(p1[0]), static_cast<unsigned>(p1[1]),
                            static_cast<unsigned>(p1[2]), static_cast<unsigned>(p1[3]));
                    }
                    std::printf("\n");
                }
            }
            // ---- 产品 API：渲染后的整面回读（修复后的正路）--------------------
            {
                // 上一组实验把 resOwn 留在了 COMMON；产品 API 依赖模块自己记的状态，
                // 这里先把真实状态还原成 PSR，避免"实验污染"影响下一段判定。
                if (gpu.Begin()) {
                    Transition(gpu, resOwn, D3D12_RESOURCE_STATE_COMMON,
                        D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
                            | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                    gpu.ExecuteAndWait();
                }
                std::vector<uint8_t> dst(static_cast<size_t>(width) * 4u * height, 0x77u);
                uint32_t rw = 0u;
                uint32_t rh = 0u;
                const int rc = readPixels(dst.data(), width * 4u, &rw, &rh);
                const uint32_t pitch = width * 4u;
                uint32_t zeroRows = 0u;
                for (uint32_t y = 0u; y < height; ++y) {
                    const uint8_t* row = dst.data() + static_cast<size_t>(y) * pitch;
                    bool all = true;
                    for (uint32_t x = 0u; x < width * 4u; ++x) {
                        if (row[x] != 0u) {
                            all = false;
                            break;
                        }
                    }
                    if (all) {
                        ++zeroRows;
                    }
                }
                const uint8_t* px = dst.data();
                std::printf("\n---- product API on rendered target: rc=%d size=%ux%u zeroRows=%u/%u "
                            "corner BGRA=(%u,%u,%u,%u) ----\n",
                    rc, rw, rh, zeroRows, height, static_cast<unsigned>(px[0]),
                    static_cast<unsigned>(px[1]), static_cast<unsigned>(px[2]),
                    static_cast<unsigned>(px[3]));
                if (rc != 0 || rw != width || rh != height || zeroRows != 0u) {
                    ++failures;
                }
            }
        }
    }

    releaseTarget();
    FreeLibrary(dll);
    std::printf("\n%s (%ux%u)\n", failures == 0 ? "PROBE OK: pixel-identical" : "PROBE FAILED",
        width, height);
    return failures == 0 ? 0 : 1;
}
