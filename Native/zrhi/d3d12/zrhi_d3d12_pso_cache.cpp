// =============================================================================
// zrhi_d3d12_pso_cache.cpp —— D3D12 PSO 磁盘缓存实现（TASK-B-PSO-01）
//
// 设计要点（详见头文件）：
//   1. 只使用 Windows 官方 ID3D12PipelineLibrary，不自造 PSO 序列化格式；
//      本文件自己的字节只做"外层封皮"（magic/版本/尺寸/校验和/GPU 指纹），
//      用于在把 blob 交给驱动前识别损坏文件。
//   2. 命名 = desc 稳定 FNV-1a 64 哈希的 16 位十六进制（宽字符）；
//      哈希带算法版本前缀，覆盖 D3D12 desc 全部状态位 + 两段着色器字节码
//      + 根签名布局计数。
//   3. 命中/未命中：落盘时同时存 name 列表；启动加载后 name 在列表里 ⇒ 库内
//      必有该 PSO（命中）；不在 ⇒ 本次在库内新建（未命中）。
//   4. 兜底：库不可用 / 调用失败 / 重建后仍失败 → 一律退回
//      ID3D12Device::CreateGraphicsPipelineState，缓存问题绝不影响启动。
//   5. 写盘：唯一临时文件 + MoveFileEx(REPLACE_EXISTING)，多进程并发安全
//      （最后写入者胜出，读到的永远是完整文件）。
// =============================================================================
#include "zrhi/d3d12/zrhi_d3d12_pso_cache.h"

#include <dxgi1_5.h>

#include <cstdio>
#include <cstring>
#include <cwchar>

namespace {

// ---- 缓存文件格式 -----------------------------------------------------------
// 头尺寸固定 72B，写入/读取均按字段显式序列化（不依赖结构体内存布局）。
constexpr char kFileMagic[8] = {'Z', 'P', 'S', 'O', 'C', 'A', '0', '1'};
constexpr uint32_t kFileFormatVersion = 1u;
constexpr uint64_t kFnvOffset = 14695981039346656037ull; // FNV-1a 64 偏移基
constexpr uint64_t kFnvPrime = 1099511628211ull;

// 哈希算法版本前缀：**改动哈希内容/根签名布局/translate 固定状态时必须改这里**，
// 旧缓存会因哈希不同而自然失效（旧文件随后被重建覆盖）。
constexpr char kHashVersion[] = "ZPSO-HASH-v1|translate-v1|rootsig-v1";

// 文件里 name 列表上限（防御损坏文件导致的超大分配）
constexpr uint64_t kMaxNameCount = 1000000ull;
// 读盘上限（防御损坏头导致的超大分配；正常库远小于此）
constexpr uint64_t kMaxCacheFileBytes = 512ull * 1024ull * 1024ull;

struct CacheFileHeader {
    char magic[8];
    uint32_t formatVersion;
    uint32_t headerBytes;
    uint64_t blobBytes;
    uint64_t blobHash;
    uint64_t nameCount;
    uint64_t nameHash;
    uint32_t vendorId;
    uint32_t deviceId;
    uint32_t subSysId;
    uint32_t revision;
    uint32_t reserved0;
    uint32_t reserved1;
};
static_assert(sizeof(CacheFileHeader) == 72, "PSO cache header must be 72 bytes");

uint64_t Fnv1a(uint64_t seed, const void* data, size_t bytes)
{
    const uint8_t* p = static_cast<const uint8_t*>(data);
    uint64_t h = seed;
    for (size_t i = 0; i < bytes; ++i) {
        h ^= static_cast<uint64_t>(p[i]);
        h *= kFnvPrime;
    }
    return h;
}

void HashU32(uint64_t& h, uint32_t value) { h = Fnv1a(h, &value, sizeof(value)); }
void HashU64(uint64_t& h, uint64_t value) { h = Fnv1a(h, &value, sizeof(value)); }
void HashI32(uint64_t& h, int32_t value) { h = Fnv1a(h, &value, sizeof(value)); }
void HashF32(uint64_t& h, float value) { h = Fnv1a(h, &value, sizeof(value)); }
void HashBytes(uint64_t& h, const void* data, size_t bytes)
{
    HashU64(h, static_cast<uint64_t>(bytes));
    if (data != nullptr && bytes > 0u) {
        h = Fnv1a(h, data, bytes);
    }
}

void HashToName(uint64_t hash, wchar_t out[17])
{
    static const wchar_t kHex[] = L"0123456789abcdef";
    for (int i = 15; i >= 0; --i) {
        out[i] = kHex[static_cast<size_t>(hash & 0xFull)];
        hash >>= 4;
    }
    out[16] = L'\0';
}

double QuerySeconds(LARGE_INTEGER start)
{
    static LARGE_INTEGER freq = [] {
        LARGE_INTEGER f {};
        QueryPerformanceFrequency(&f);
        return f;
    }();
    LARGE_INTEGER now {};
    QueryPerformanceCounter(&now);
    if (freq.QuadPart <= 0) {
        return 0.0;
    }
    return static_cast<double>(now.QuadPart - start.QuadPart) * 1000.0
        / static_cast<double>(freq.QuadPart);
}

std::wstring GetModuleDirectory()
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD n = GetModuleFileNameW(nullptr, path.data(),
            static_cast<DWORD>(path.size()));
        if (n == 0) {
            return std::wstring();
        }
        if (n < path.size()) {
            path.resize(n);
            break;
        }
        path.resize(path.size() * 2u);
    }
    const size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos) {
        return std::wstring();
    }
    return path.substr(0, slash);
}

bool EnsureDirectory(const std::wstring& directory)
{
    if (directory.empty()) {
        return false;
    }
    // 逐级创建（跳过盘符前缀）；已存在不算失败
    for (size_t i = 0; i < directory.size(); ++i) {
        if (directory[i] != L'\\' && directory[i] != L'/') {
            continue;
        }
        if (i == 0 || (i == 2 && directory[1] == L':')) {
            continue;
        }
        const std::wstring part = directory.substr(0, i);
        if (!CreateDirectoryW(part.c_str(), nullptr)
            && GetLastError() != ERROR_ALREADY_EXISTS) {
            return false;
        }
    }
    if (!CreateDirectoryW(directory.c_str(), nullptr)
        && GetLastError() != ERROR_ALREADY_EXISTS) {
        return false;
    }
    return true;
}

bool IsDirectoryWritable(const std::wstring& directory)
{
    if (directory.empty()) {
        return false;
    }
    wchar_t name[64] {};
    swprintf(name, 64, L"\\pso_probe_%lu.tmp", static_cast<unsigned long>(GetCurrentProcessId()));
    const std::wstring probe = directory + name;
    const HANDLE h = CreateFileW(probe.c_str(), GENERIC_WRITE, 0u, nullptr, CREATE_ALWAYS,
        FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return false;
    }
    CloseHandle(h); // DELETE_ON_CLOSE：句柄关闭即删除探针文件
    return true;
}

bool WriteAll(HANDLE file, const void* data, size_t bytes)
{
    const uint8_t* p = static_cast<const uint8_t*>(data);
    size_t remaining = bytes;
    while (remaining > 0u) {
        const DWORD chunk = remaining > (1u << 20) ? (1u << 20)
                                                   : static_cast<DWORD>(remaining);
        DWORD written = 0;
        if (!WriteFile(file, p, chunk, &written, nullptr) || written == 0u) {
            return false;
        }
        p += written;
        remaining -= written;
    }
    return true;
}

bool ReadAll(HANDLE file, void* data, size_t bytes)
{
    uint8_t* p = static_cast<uint8_t*>(data);
    size_t remaining = bytes;
    while (remaining > 0u) {
        const DWORD chunk = remaining > (1u << 20) ? (1u << 20)
                                                   : static_cast<DWORD>(remaining);
        DWORD read = 0;
        if (!ReadFile(file, p, chunk, &read, nullptr) || read == 0u) {
            return false;
        }
        p += read;
        remaining -= read;
    }
    return true;
}

const char* StatusText(int status)
{
    switch (status) {
    case 1: return "missing (first run)";
    case 2: return "corrupt: file too small";
    case 3: return "corrupt: bad magic";
    case 4: return "corrupt: format version mismatch";
    case 5: return "corrupt: header size mismatch";
    case 6: return "corrupt: size fields inconsistent";
    case 7: return "corrupt: name table checksum mismatch";
    case 8: return "corrupt: blob checksum mismatch";
    case 9: return "stale: GPU adapter changed";
    default: return "unreadable";
    }
}

} // namespace

// =============================================================================
// 路径解析
// =============================================================================
void ZrD3D12PsoCache::ResolvePath()
{
    m_path.clear();

    wchar_t env[32768] {};
    const DWORD envLen = GetEnvironmentVariableW(L"ZFRAME_PSO_CACHE", env,
        static_cast<DWORD>(sizeof(env) / sizeof(env[0])));
    if (envLen > 0u && envLen < sizeof(env) / sizeof(env[0])) {
        std::wstring value(env, envLen);
        // 基线测量/故障复现：显式禁用缓存
        if (_wcsicmp(value.c_str(), L"0") == 0 || _wcsicmp(value.c_str(), L"off") == 0
            || _wcsicmp(value.c_str(), L"disable") == 0) {
            std::fprintf(stderr, "[zrhi][d3d12][pso-cache] disabled by "
                                 "ZFRAME_PSO_CACHE=%ls\n", value.c_str());
            return;
        }
        m_path = value;
        const size_t slash = m_path.find_last_of(L"\\/");
        if (slash != std::wstring::npos) {
            EnsureDirectory(m_path.substr(0, slash));
        }
        return;
    }

    // 默认：exe 同目录（可写时）；否则 %LOCALAPPDATA%\ZFrame\pso_cache.bin
    const std::wstring exeDir = GetModuleDirectory();
    if (!exeDir.empty() && IsDirectoryWritable(exeDir)) {
        m_path = exeDir + L"\\pso_cache.bin";
        return;
    }

    wchar_t localAppData[32768] {};
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData,
        static_cast<DWORD>(sizeof(localAppData) / sizeof(localAppData[0])));
    std::wstring base;
    if (n > 0u && n < sizeof(localAppData) / sizeof(localAppData[0])) {
        base = std::wstring(localAppData, n) + L"\\ZFrame";
    } else {
        wchar_t temp[MAX_PATH] {};
        if (GetTempPathW(MAX_PATH, temp) > 0u) {
            base = std::wstring(temp) + L"ZFrame";
        }
    }
    if (base.empty()) {
        return; // 无任何可写落点：静默禁用
    }
    EnsureDirectory(base);
    m_path = base + L"\\pso_cache.bin";
}

// =============================================================================
// 初始化：能力探测 + 磁盘加载
// =============================================================================
bool ZrD3D12PsoCache::Initialize(ID3D12Device* device)
{
    if (device == nullptr || m_initialized) {
        return m_library != nullptr;
    }
    m_initialized = true;
    m_device = device;

    LARGE_INTEGER start {};
    QueryPerformanceCounter(&start);

    // ---- GPU 指纹（换卡/换驱动后旧库不可用；这里先拦换卡，驱动由 D3D12 校验）----
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDevice;
    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc {};
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)))
        && SUCCEEDED(dxgiDevice->GetAdapter(&adapter))
        && SUCCEEDED(adapter->GetDesc(&adapterDesc))) {
        m_vendorId = adapterDesc.VendorId;
        m_deviceId = adapterDesc.DeviceId;
        m_subSysId = adapterDesc.SubSysId;
        m_revision = adapterDesc.Revision;
    }

    ResolvePath();
    if (m_path.empty()) {
        m_stats.loadMs = QuerySeconds(start);
        return false;
    }

    // ---- 能力探测：ID3D12Device1（ID3D12PipelineLibrary 的创建入口）--------------
    Microsoft::WRL::ComPtr<ID3D12Device1> device1;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device1)))) {
        std::fprintf(stderr,
            "[zrhi][d3d12][pso-cache] ID3D12Device1 unavailable: "
            "pipeline library unsupported, no-cache path\n");
        m_stats.loadMs = QuerySeconds(start);
        return false;
    }

    // ---- 磁盘加载（损坏/缺失一律不致命）--------------------------------------------
    std::vector<uint64_t> names;
    std::vector<uint8_t> blob;
    bool haveBlob = ReadCacheFile(names, blob);
    if (haveBlob && !blob.empty()) {
        const HRESULT hr = device1->CreatePipelineLibrary(blob.data(), blob.size(),
            IID_PPV_ARGS(&m_library));
        if (FAILED(hr)) {
            // 驱动版本变更 / 库内容与当前设备不兼容：删缓存重建
            std::fprintf(stderr,
                "[zrhi][d3d12][pso-cache] CreatePipelineLibrary(disk blob) failed "
                "hr=0x%08lX: dropping cache and rebuilding\n",
                static_cast<unsigned long>(hr));
            DeleteCacheFile();
            m_library.Reset();
            names.clear();
            blob.clear();
        } else {
            m_names = names;
            m_nameLookup.insert(names.begin(), names.end());
            m_stats.diskLoaded = true;
            m_stats.serializeBytes = static_cast<uint64_t>(blob.size()); // 当前生效库大小
            m_diskBlob = std::move(blob); // 保活：库对象生命周期内不释放反序列化输入
        }
    }

    // ---- 无可用磁盘库 → 空库（首次运行）；空库也失败 → 静默禁用 -------------------
    if (m_library == nullptr && !CreateEmptyLibrary()) {
        m_stats.loadMs = QuerySeconds(start);
        std::fprintf(stderr,
            "[zrhi][d3d12][pso-cache] pipeline library unavailable: "
            "using direct PSO creation (no cache)\n");
        return false;
    }

    m_stats.supported = true;
    m_stats.loadMs = QuerySeconds(start);
    std::fprintf(stderr,
        "[zrhi][d3d12][pso-cache] init: path=%ls loaded=%d names=%llu blob_bytes=%llu "
        "load_ms=%.3f\n",
        m_path.c_str(), m_stats.diskLoaded ? 1 : 0,
        static_cast<unsigned long long>(m_names.size()),
        static_cast<unsigned long long>(m_diskBlob.size()), m_stats.loadMs);
    return true;
}

bool ZrD3D12PsoCache::CreateEmptyLibrary()
{
    Microsoft::WRL::ComPtr<ID3D12Device1> device1;
    if (m_device == nullptr || FAILED(m_device->QueryInterface(IID_PPV_ARGS(&device1)))) {
        m_library.Reset();
        return false;
    }
    m_library.Reset();
    // 官方语义：blob 指针为 nullptr + 尺寸 0 ⇒ 新建空库
    const HRESULT hr = device1->CreatePipelineLibrary(nullptr, 0u, IID_PPV_ARGS(&m_library));
    if (FAILED(hr) || m_library == nullptr) {
        m_library.Reset();
        return false;
    }
    return true;
}

// =============================================================================
// 读盘 + 校验
// =============================================================================
bool ZrD3D12PsoCache::ReadCacheFile(std::vector<uint64_t>& names, std::vector<uint8_t>& blob)
{
    const HANDLE file = CreateFileW(m_path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::fprintf(stderr, "[zrhi][d3d12][pso-cache] cache file: %s\n",
            StatusText(1));
        return false;
    }

    LARGE_INTEGER size {};
    int status = 0;
    if (!GetFileSizeEx(file, &size) || size.QuadPart < static_cast<LONGLONG>(sizeof(CacheFileHeader))) {
        status = 2;
    } else if (static_cast<uint64_t>(size.QuadPart) > kMaxCacheFileBytes) {
        status = 6;
    }

    CacheFileHeader header {};
    if (status == 0 && !ReadAll(file, &header, sizeof(header))) {
        status = 6;
    }
    if (status == 0) {
        if (std::memcmp(header.magic, kFileMagic, sizeof(kFileMagic)) != 0) {
            status = 3;
        } else if (header.formatVersion != kFileFormatVersion) {
            status = 4;
        } else if (header.headerBytes != sizeof(CacheFileHeader)) {
            status = 5;
        } else if (header.nameCount > kMaxNameCount || header.blobBytes > kMaxCacheFileBytes) {
            status = 6;
        } else if (header.nameCount * sizeof(uint64_t) + header.blobBytes
            != static_cast<uint64_t>(size.QuadPart) - sizeof(CacheFileHeader)) {
            status = 6;
        }
    }

    if (status == 0) {
        names.resize(static_cast<size_t>(header.nameCount));
        const size_t namesBytes = names.size() * sizeof(uint64_t);
        if (namesBytes > 0u && !ReadAll(file, names.data(), namesBytes)) {
            status = 7;
        } else if (Fnv1a(kFnvOffset, names.data(), namesBytes) != header.nameHash) {
            status = 7;
        }
    }

    if (status == 0) {
        blob.resize(static_cast<size_t>(header.blobBytes));
        if (!blob.empty() && !ReadAll(file, blob.data(), blob.size())) {
            status = 8;
        } else if (Fnv1a(kFnvOffset, blob.data(), blob.size()) != header.blobHash) {
            status = 8;
        }
    }
    CloseHandle(file);

    if (status == 0
        && (header.vendorId != m_vendorId || header.deviceId != m_deviceId
            || header.subSysId != m_subSysId || header.revision != m_revision)) {
        status = 9;
    }

    if (status != 0) {
        // 任何不可用状态：删掉坏文件，随后用空库重建（下次刷写覆盖）
        std::fprintf(stderr, "[zrhi][d3d12][pso-cache] cache file: %s -> delete & rebuild\n",
            StatusText(status));
        DeleteCacheFile();
        names.clear();
        blob.clear();
        return false;
    }
    return true;
}

void ZrD3D12PsoCache::DeleteCacheFile()
{
    m_diskBlob.clear();
    if (m_path.empty()) {
        return;
    }
    if (!DeleteFileW(m_path.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) {
        std::fprintf(stderr,
            "[zrhi][d3d12][pso-cache] cannot delete cache file (err=%lu); "
            "it will be overwritten on flush\n",
            static_cast<unsigned long>(GetLastError()));
    }
}

// =============================================================================
// 写盘：临时文件 + 原子替换
// =============================================================================
bool ZrD3D12PsoCache::WriteCacheFile(
    const std::vector<uint64_t>& names, const void* blob, size_t blobBytes)
{
    if (m_path.empty() || blob == nullptr || blobBytes == 0u) {
        return false;
    }

    CacheFileHeader header {};
    std::memcpy(header.magic, kFileMagic, sizeof(kFileMagic));
    header.formatVersion = kFileFormatVersion;
    header.headerBytes = static_cast<uint32_t>(sizeof(CacheFileHeader));
    header.blobBytes = static_cast<uint64_t>(blobBytes);
    header.blobHash = Fnv1a(kFnvOffset, blob, blobBytes);
    header.nameCount = static_cast<uint64_t>(names.size());
    header.nameHash = Fnv1a(kFnvOffset, names.data(), names.size() * sizeof(uint64_t));
    header.vendorId = m_vendorId;
    header.deviceId = m_deviceId;
    header.subSysId = m_subSysId;
    header.revision = m_revision;

    // 唯一临时名（pid + tick），避免多进程互相截断；替换本身是原子的
    wchar_t suffix[96] {};
    swprintf(suffix, 96, L".tmp.%lu.%llu",
        static_cast<unsigned long>(GetCurrentProcessId()),
        static_cast<unsigned long long>(GetTickCount64()));
    const std::wstring tempPath = m_path + suffix;

    const HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0u, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::fprintf(stderr,
            "[zrhi][d3d12][pso-cache] write failed: cannot create temp file (err=%lu)\n",
            static_cast<unsigned long>(GetLastError()));
        return false;
    }

    bool ok = WriteAll(file, &header, sizeof(header));
    if (ok && !names.empty()) {
        ok = WriteAll(file, names.data(), names.size() * sizeof(uint64_t));
    }
    if (ok) {
        ok = WriteAll(file, blob, blobBytes);
    }
    CloseHandle(file);

    if (ok) {
        // 原子替换：读者永远看到旧文件或新文件，不会看到半截
        ok = MoveFileExW(tempPath.c_str(), m_path.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
    }
    if (!ok) {
        DeleteFileW(tempPath.c_str());
        std::fprintf(stderr,
            "[zrhi][d3d12][pso-cache] write failed (err=%lu)\n",
            static_cast<unsigned long>(GetLastError()));
    }
    return ok;
}

// =============================================================================
// 哈希：覆盖 D3D12 desc 全状态位 + 着色器字节码 + 根签名布局计数
// =============================================================================
uint64_t ZrD3D12PsoCache::HashPipelineDesc(const D3D12_GRAPHICS_PIPELINE_STATE_DESC& desc,
    const void* vsCode, size_t vsBytes, const void* psCode, size_t psBytes,
    uint32_t rootCbvCount, uint32_t srvTableCount)
{
    uint64_t h = Fnv1a(kFnvOffset, kHashVersion, sizeof(kHashVersion));

    // 根签名布局（D3D12 desc 里只有 root signature 指针，身份无法跨进程哈希；
    // 布局由这两个计数唯一决定 → 进哈希；布局本身改变须提升 kHashVersion）
    HashU32(h, rootCbvCount);
    HashU32(h, srvTableCount);

    // 着色器字节码（VS/PS；DS/HS/GS 本后端恒空，仍纳入以防未来扩展）
    HashBytes(h, vsCode, vsBytes);
    HashBytes(h, psCode, psBytes);
    HashBytes(h, desc.DS.pShaderBytecode, desc.DS.BytecodeLength);
    HashBytes(h, desc.HS.pShaderBytecode, desc.HS.BytecodeLength);
    HashBytes(h, desc.GS.pShaderBytecode, desc.GS.BytecodeLength);

    // 固定管线状态：translate 函数 out{} 零初始化，结构体内存按位哈希等价于逐字段
    // （版本前缀已覆盖编译期/布局变化）
    HashU32(h, desc.NodeMask);
    HashU32(h, static_cast<uint32_t>(desc.Flags));
    h = Fnv1a(h, &desc.BlendState, sizeof(desc.BlendState));
    HashU32(h, desc.SampleMask);
    h = Fnv1a(h, &desc.RasterizerState, sizeof(desc.RasterizerState));
    h = Fnv1a(h, &desc.DepthStencilState, sizeof(desc.DepthStencilState));
    HashU32(h, static_cast<uint32_t>(desc.PrimitiveTopologyType));
    HashU32(h, desc.NumRenderTargets);
    for (UINT i = 0; i < 8u; ++i) {
        HashU32(h, static_cast<uint32_t>(desc.RTVFormats[i]));
    }
    HashU32(h, static_cast<uint32_t>(desc.DSVFormat));
    HashU32(h, desc.SampleDesc.Count);
    HashU32(h, desc.SampleDesc.Quality);
    HashU32(h, desc.IBStripCutValue);

    // 流输出（本后端恒空；含指针，只哈希计数，指针内容为零长度时无意义）
    HashU32(h, desc.StreamOutput.NumEntries);
    HashU32(h, desc.StreamOutput.NumStrides);
    // 内嵌缓存 PSO（本后端不用；若未来启用则一并进哈希）
    HashU64(h, static_cast<uint64_t>(desc.CachedPSO.CachedBlobSizeInBytes));
    if (desc.CachedPSO.pCachedBlob != nullptr && desc.CachedPSO.CachedBlobSizeInBytes > 0u) {
        h = Fnv1a(h, desc.CachedPSO.pCachedBlob, desc.CachedPSO.CachedBlobSizeInBytes);
    }

    // 输入布局（语义名字符串 + 逐元素状态）
    HashU32(h, desc.InputLayout.NumElements);
    for (UINT i = 0; i < desc.InputLayout.NumElements; ++i) {
        const D3D12_INPUT_ELEMENT_DESC& e = desc.InputLayout.pInputElementDescs[i];
        if (e.SemanticName != nullptr) {
            HashBytes(h, e.SemanticName, std::strlen(e.SemanticName));
        }
        HashU32(h, e.SemanticIndex);
        HashU32(h, static_cast<uint32_t>(e.Format));
        HashU32(h, e.InputSlot);
        HashU32(h, e.AlignedByteOffset);
        HashU32(h, static_cast<uint32_t>(e.InputSlotClass));
        HashU32(h, e.InstanceDataStepRate);
    }
    return h;
}

// =============================================================================
// 创建：库优先，任何失败退回设备直接创建
// =============================================================================
HRESULT ZrD3D12PsoCache::CreateGraphicsPipelineState(
    const D3D12_GRAPHICS_PIPELINE_STATE_DESC& desc, const void* vsCode, size_t vsBytes,
    const void* psCode, size_t psBytes, uint32_t rootCbvCount, uint32_t srvTableCount,
    REFIID riid, void** outPipelineState)
{
    if (outPipelineState == nullptr) {
        return E_POINTER;
    }
    *outPipelineState = nullptr;
    if (m_device == nullptr) {
        return E_FAIL;
    }

    LARGE_INTEGER start {};
    QueryPerformanceCounter(&start);

    const uint64_t key = HashPipelineDesc(
        desc, vsCode, vsBytes, psCode, psBytes, rootCbvCount, srvTableCount);
    HRESULT hr = E_FAIL;

    if (m_library != nullptr) {
        wchar_t name[17] {};
        HashToName(key, name);
        bool known = m_nameLookup.find(key) != m_nameLookup.end();

        // 官方库路径：LoadGraphicsPipeline 命中返回缓存 PSO；未命中返回失败
        // （E_INVALIDARG），随后直接创建并 StorePipeline 入册。
        hr = m_library->LoadGraphicsPipeline(name, &desc, riid, outPipelineState);
        if (SUCCEEDED(hr)) {
            ++m_stats.hits;
            if (!known) {
                // 索引外条目（例如其它进程刚写入的库）：按命中处理并补登
                m_names.push_back(key);
                m_nameLookup.insert(key);
                m_dirty = true;
            }
        } else {
            *outPipelineState = nullptr;
            if (known && !m_rebuilt) {
                // 索引声称库里有、驱动却加载失败 → 库内容不可信：
                // 删盘 + 重建空库（一次），防止坏库反复拖慢/报错
                m_rebuilt = true;
                std::fprintf(stderr,
                    "[zrhi][d3d12][pso-cache] known entry failed to load hr=0x%08lX: "
                    "dropping cache and rebuilding empty library\n",
                    static_cast<unsigned long>(hr));
                DeleteCacheFile();
                m_names.clear();
                m_nameLookup.clear();
                m_stats.diskLoaded = false;
                m_dirty = false;
                if (!CreateEmptyLibrary()) {
                    ++m_stats.fallbacks;
                    std::fprintf(stderr,
                        "[zrhi][d3d12][pso-cache] empty library rebuild failed: "
                        "no-cache path\n");
                }
            }

            // 未命中（或重建后）：设备直接创建 —— 这是必须存在的兜底路径
            LARGE_INTEGER compileStart {};
            QueryPerformanceCounter(&compileStart);
            hr = m_device->CreateGraphicsPipelineState(&desc, riid, outPipelineState);
            m_stats.compileMs += QuerySeconds(compileStart);
            if (SUCCEEDED(hr)) {
                if (m_library != nullptr) {
                    ++m_stats.misses;
                    // 本次编译结果入册，退出时随 Serialize 一起落盘
                    LARGE_INTEGER storeStart {};
                    QueryPerformanceCounter(&storeStart);
                    const HRESULT storeHr = m_library->StorePipeline(
                        name, static_cast<ID3D12PipelineState*>(*outPipelineState));
                    m_stats.storeMs += QuerySeconds(storeStart);
                    if (SUCCEEDED(storeHr)) {
                        m_names.push_back(key);
                        m_nameLookup.insert(key);
                        m_dirty = true;
                    } else {
                        // 库拒绝存储：本进程余下全部直接创建（不影响渲染）
                        ++m_stats.fallbacks;
                        std::fprintf(stderr,
                            "[zrhi][d3d12][pso-cache] StorePipeline failed: disabling "
                            "pipeline library for this process\n");
                        DeleteCacheFile();
                        m_library.Reset();
                    }
                } else {
                    ++m_stats.directCreates;
                }
            }
        }
    } else {
        // 无缓存路径（不支持/被禁用/已降级）：直接创建
        LARGE_INTEGER compileStart {};
        QueryPerformanceCounter(&compileStart);
        hr = m_device->CreateGraphicsPipelineState(&desc, riid, outPipelineState);
        m_stats.compileMs += QuerySeconds(compileStart);
        if (SUCCEEDED(hr)) {
            ++m_stats.directCreates;
        }
    }

    m_stats.createMs += QuerySeconds(start);
    return hr;
}

// =============================================================================
// 刷写 + 统计
// =============================================================================
void ZrD3D12PsoCache::Flush()
{
    if (!m_initialized) {
        return;
    }

    if (m_library != nullptr && m_dirty) {
        const SIZE_T bytes = m_library->GetSerializedSize();
        if (bytes > 0u) {
            std::vector<uint8_t> blob(bytes);
            const HRESULT hr = m_library->Serialize(blob.data(), blob.size());
            if (SUCCEEDED(hr)) {
                if (WriteCacheFile(m_names, blob.data(), blob.size())) {
                    m_stats.serializeBytes = static_cast<uint64_t>(blob.size());
                }
            } else {
                std::fprintf(stderr,
                    "[zrhi][d3d12][pso-cache] Serialize failed hr=0x%08lX\n",
                    static_cast<unsigned long>(hr));
            }
        }
        m_dirty = false;
    }

    LogStats("flush");
}

void ZrD3D12PsoCache::LogStats(const char* when) const
{
    // 判据字段：命中 hits / 未命中 misses / 加载耗时 load_ms / 序列化字节 serialize_bytes
    std::fprintf(stderr,
        "[zrhi][d3d12][pso-cache] %s: hits=%u misses=%u fallbacks=%u direct=%u "
        "support=%d loaded=%d load_ms=%.3f create_ms=%.3f compile_ms=%.3f store_ms=%.3f "
        "serialize_bytes=%llu\n",
        when, m_stats.hits, m_stats.misses, m_stats.fallbacks, m_stats.directCreates,
        m_stats.supported ? 1 : 0, m_stats.diskLoaded ? 1 : 0, m_stats.loadMs,
        m_stats.createMs, m_stats.compileMs, m_stats.storeMs,
        static_cast<unsigned long long>(m_stats.serializeBytes));
}
