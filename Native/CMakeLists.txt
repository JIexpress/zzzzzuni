# =============================================================================
# ZRender 原生构建顶层（TASK-B-R0-01）
#
# 目标：
#   - 五模块静态库：zkernel / zrhi / zrender_core / zupscaler / zplugin
#   - SHARED 库 zrender.dll（聚合五模块，C ABI 导出清单见 zplugin/zrender.def）
#   - doctest 冒烟测试程序 zrender_test.exe
#
# 产物目录（Native/build/ 已被 .gitignore 排除，不入库）：
#   bin/$(Config)/  -> exe 与 dll
#   lib/$(Config)/  -> 静态库与导入库
# =============================================================================
cmake_minimum_required(VERSION 3.24)

project(ZRender VERSION 0.1.0 LANGUAGES CXX)

set(ZRENDER_VERSION ${PROJECT_VERSION})

# ---- 语言标准与编译选项 ------------------------------------------------------
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
    # /W4 最高实用警告级；/WX 把警告升级为错误以强制"零警告"；
    # /utf-8 源码与执行字符集均为 UTF-8；/MP 多进程并行编译。
    add_compile_options(/W4 /utf-8 /MP /WX)
    # Release 默认即含 /O2，这里显式固化约定（幂等追加）。
    string(APPEND CMAKE_CXX_FLAGS_RELEASE " /O2")
else()
    message(FATAL_ERROR "ZRender 原生构建当前仅支持 MSVC 2022 x64（Windows）")
endif()

# 版本宏单一事实来源：project(... VERSION ...) -> ZR_VERSION_STRING
add_compile_definitions(ZR_VERSION_STRING="${ZRENDER_VERSION}")

# ---- 输出目录 ----------------------------------------------------------------
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin) # exe / dll
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib) # 静态库 / 导入库

# ---- 五模块静态库 ------------------------------------------------------------
# 本任务（B-R0-01）仅放最小占位实现（各模块版本函数），后续任务填充。
# 每个模块对外只暴露一个公共头（Docs/Standards/file-conventions.md §3）。
# 注意：zrender 模块的静态库目标名为 zrender_core，避免与 SHARED 目标 zrender 冲突。
# TASK-B-R2-01：zrhi 增加泛代句柄表实现（语义层其余部分为纯头文件）。
# TASK-B-R2-02：zrhi 并入 D3D12 后端基座（设备/交换链/命令表/映射表；纯 Win32
# SDK，无第三方依赖——d3d12.lib/dxgi.lib 为系统库）。
# TASK-B-R2-03：zrhi 并入 D3D12 资源体系（描述符堆槽位分配器/资源状态表/采样器
# 映射扩展）。
# TASK-B-R4-03：zrhi 并入图形 PSO/运行时着色器编译（d3dcompiler.lib 系统库）。
# ---- TASK-B-R7-01：Vulkan 第二后端（条件编译：VULKAN_SDK 存在才启用）--------------
# 探测顺序：环境变量 VULKAN_SDK → 常见安装路径兜底（不改用户环境变量，仅本配置内
# 探测）。找不到则跳过 Vulkan 目标并打印说明（CI/他机友好；D3D12 路径不受影响）。
set(ZR_VULKAN_SDK_PATH "$ENV{VULKAN_SDK}")
if(NOT ZR_VULKAN_SDK_PATH)
    file(GLOB ZR_VULKAN_SDK_GLOB "C:/VulkanSDK/*")
    foreach(ZR_SDK_CANDIDATE ${ZR_VULKAN_SDK_GLOB})
        if(IS_DIRECTORY "${ZR_SDK_CANDIDATE}/Include/vulkan")
            get_filename_component(ZR_VULKAN_SDK_PATH "${ZR_SDK_CANDIDATE}" ABSOLUTE)
        endif()
    endforeach()
endif()
if(ZR_VULKAN_SDK_PATH AND EXISTS "${ZR_VULKAN_SDK_PATH}/Include/vulkan/vulkan.h"
        AND EXISTS "${ZR_VULKAN_SDK_PATH}/Lib/vulkan-1.lib")
    set(ZR_HAS_VULKAN ON)
    message(STATUS "ZRender Vulkan backend: ENABLED (VULKAN_SDK=${ZR_VULKAN_SDK_PATH})")
else()
    set(ZR_HAS_VULKAN OFF)
    message(STATUS "ZRender Vulkan backend: SKIPPED (VULKAN_SDK not found - "
        "install the Vulkan SDK and reconfigure to enable the vulkan backend)")
endif()

if(ZR_HAS_VULKAN)
    # Vulkan 后端源文件（镜像 d3d12 后端文件组织）
    set(ZR_VULKAN_SOURCES
        zrhi/vulkan/zrhi_vulkan_translate.cpp
        zrhi/vulkan/zrhi_vulkan_device.cpp
        zrhi/vulkan/zrhi_vulkan_commandlist.cpp
    )
    # SPIR-V 着色器（glslc -x hlsl 直接编译现有 .hlsl 源——SM5/SM6 通用子集）：
    # 产物进 bin/shaders_spv/（验证程序按 exe 相对路径加载）
    set(ZR_GLSLC "${ZR_VULKAN_SDK_PATH}/Bin/glslc.exe")
    set(ZR_SPV_DIR ${CMAKE_BINARY_DIR}/bin/shaders_spv)
    file(MAKE_DIRECTORY ${ZR_SPV_DIR})
    set(ZR_SPV_OUTPUTS "")
    # TASK-B-R8-01：宏扩展可选第 4 参起的额外 glslc 旗标（绑定基偏移；既有调用
    # 不传参行为不变）。旗标同时作用于 vs/fs 两步——VS 不引用纹理时纹理基对
    # 其无效果，安全。
    macro(ZR_COMPILE_HLSL_SPV name vsEntry psEntry)
        set(ZR_SPV_EXTRA_FLAGS "")
        foreach(ZR_SPV_FLAG ${ARGN})
            list(APPEND ZR_SPV_EXTRA_FLAGS ${ZR_SPV_FLAG})
        endforeach()
        add_custom_command(
            OUTPUT ${ZR_SPV_DIR}/${name}_vs.spv ${ZR_SPV_DIR}/${name}_fs.spv
            COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=vertex
                -fentry-point=${vsEntry} -O ${ZR_SPV_EXTRA_FLAGS}
                -o ${ZR_SPV_DIR}/${name}_vs.spv
                ${CMAKE_CURRENT_SOURCE_DIR}/shaders/${name}.hlsl
            COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=fragment
                -fentry-point=${psEntry} -O ${ZR_SPV_EXTRA_FLAGS}
                -o ${ZR_SPV_DIR}/${name}_fs.spv
                ${CMAKE_CURRENT_SOURCE_DIR}/shaders/${name}.hlsl
            DEPENDS shaders/${name}.hlsl
            COMMENT "glslc HLSL->SPIR-V: ${name}.hlsl")
        list(APPEND ZR_SPV_OUTPUTS ${ZR_SPV_DIR}/${name}_vs.spv ${ZR_SPV_DIR}/${name}_fs.spv)
    endmacro()
    # 仅顶点着色器变体（TASK-B-R7-02：shadow_depth.hlsl 无像素着色器——depth-only）
    macro(ZR_COMPILE_HLSL_SPV_VS_ONLY name vsEntry)
        add_custom_command(
            OUTPUT ${ZR_SPV_DIR}/${name}_vs.spv
            COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=vertex
                -fentry-point=${vsEntry} -O -o ${ZR_SPV_DIR}/${name}_vs.spv
                ${CMAKE_CURRENT_SOURCE_DIR}/shaders/${name}.hlsl
            DEPENDS shaders/${name}.hlsl
            COMMENT "glslc HLSL->SPIR-V (vs only): ${name}.hlsl")
        list(APPEND ZR_SPV_OUTPUTS ${ZR_SPV_DIR}/${name}_vs.spv)
    endmacro()
    ZR_COMPILE_HLSL_SPV(first_draw VsMain PsMain)
    ZR_COMPILE_HLSL_SPV(pbr_forward VsMain PsMain)
    # TASK-B-R7-02：带纹理 shader 须显式绑定基——glslc HLSL 自动绑定按资源类型独立
    # 计数（t0/s0 均落 binding 0，与 cbuffer b0 冲突）。pbr_shadow 主 PSO：
    # rootCbvCount=4（b0..b3→binding0..3）+ srvTable 1（t0→binding4 / s0→binding5，
    # 与 VK 后端 set0 布局 SAMPLED_IMAGE/SAMPLER 段对位）
    set(ZR_PBR_SHADOW_FLAGS -fubo-binding-base 0 -ftexture-binding-base 4
        -fsampler-binding-base 5)
    add_custom_command(
        OUTPUT ${ZR_SPV_DIR}/pbr_shadow_vs.spv ${ZR_SPV_DIR}/pbr_shadow_fs.spv
        COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=vertex -fentry-point=VsMain -O
            ${ZR_PBR_SHADOW_FLAGS} -o ${ZR_SPV_DIR}/pbr_shadow_vs.spv
            ${CMAKE_CURRENT_SOURCE_DIR}/shaders/pbr_shadow.hlsl
        COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=fragment -fentry-point=PsMain -O
            ${ZR_PBR_SHADOW_FLAGS} -o ${ZR_SPV_DIR}/pbr_shadow_fs.spv
            ${CMAKE_CURRENT_SOURCE_DIR}/shaders/pbr_shadow.hlsl
        DEPENDS shaders/pbr_shadow.hlsl
        COMMENT "glslc HLSL->SPIR-V (bind shifts): pbr_shadow.hlsl")
    list(APPEND ZR_SPV_OUTPUTS ${ZR_SPV_DIR}/pbr_shadow_vs.spv ${ZR_SPV_DIR}/pbr_shadow_fs.spv)
    ZR_COMPILE_HLSL_SPV_VS_ONLY(shadow_depth VsMain)   # TASK-B-R7-02：--shadows 级联 depth-only
    # ---- TASK-B-R8-01：--r5 特性链 SPIR-V（8 着色器文件/16 入口点）----------------
    # 同源硬要求：与 D3D12 侧共用同一批 .hlsl（FXC 运行时编译 / glslc 构建期编译
    # 两编译器消费，零 fork、零源改动——差异仅编译旗标，见各条注释与任务日志）。
    # 绑定基与 VK set0 布局（zrhi_vulkan_device.cpp CreatePipelineState）对位：
    #   b_i → binding i；t_i → binding N+i；s_i → binding N+M+i（N=rootCbvCount
    #   M=srvTableCount）。各 PSO 签名：
    #     pbr_r5    N=5 M=1（b0..b4 主/光/簇/影/GI + t0 阴影图）
    #     hiz       N=1 M=1（b0 texel + t0 源 mip；单 VS 供两个 PS 入口共用）
    #     ssr_trace N=1 M=2（b0 + t0 Hi-Z / t1 场景色）
    #     ssr_blur  N=1 M=1（b0 + t0 trace RT）
    #     composite N=1 M=2（b0 + t0 反射 / t1 场景色）
    #     toon / toon_outline  N=1 M=0（仅 b0；无纹理）
    ZR_COMPILE_HLSL_SPV(pbr_r5 VsMain PsMain
        -fubo-binding-base 0 -ftexture-binding-base 5 -fsampler-binding-base 6)
    ZR_COMPILE_HLSL_SPV(toon VsMain PsMain)
    ZR_COMPILE_HLSL_SPV(toon_outline VsMain PsMain)
    ZR_COMPILE_HLSL_SPV(ssr_trace VsMain PsMain
        -fubo-binding-base 0 -ftexture-binding-base 1 -fsampler-binding-base 3)
    ZR_COMPILE_HLSL_SPV(ssr_blur VsMain PsMain
        -fubo-binding-base 0 -ftexture-binding-base 1 -fsampler-binding-base 2)
    ZR_COMPILE_HLSL_SPV(composite VsMain PsMain
        -fubo-binding-base 0 -ftexture-binding-base 1 -fsampler-binding-base 3)
    # hiz.hlsl：单 VS + 两个 PS 入口（HiZCopyPsMain=mip0 直拷 / HiZMinPsMain=逐级
    # min）→ 三产物（_vs/_copy_fs/_min_fs；test_zrhi_vulkan_spv.cpp 清单对位）
    add_custom_command(
        OUTPUT ${ZR_SPV_DIR}/hiz_vs.spv ${ZR_SPV_DIR}/hiz_copy_fs.spv
            ${ZR_SPV_DIR}/hiz_min_fs.spv
        COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=vertex -fentry-point=VsMain -O
            -o ${ZR_SPV_DIR}/hiz_vs.spv
            ${CMAKE_CURRENT_SOURCE_DIR}/shaders/hiz.hlsl
        COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=fragment -fentry-point=HiZCopyPsMain
            -O -fubo-binding-base 0 -ftexture-binding-base 1 -fsampler-binding-base 2
            -o ${ZR_SPV_DIR}/hiz_copy_fs.spv
            ${CMAKE_CURRENT_SOURCE_DIR}/shaders/hiz.hlsl
        COMMAND ${ZR_GLSLC} -x hlsl -fshader-stage=fragment -fentry-point=HiZMinPsMain
            -O -fubo-binding-base 0 -ftexture-binding-base 1 -fsampler-binding-base 2
            -o ${ZR_SPV_DIR}/hiz_min_fs.spv
            ${CMAKE_CURRENT_SOURCE_DIR}/shaders/hiz.hlsl
        DEPENDS shaders/hiz.hlsl
        COMMENT "glslc HLSL->SPIR-V (Hi-Z dual PS): hiz.hlsl")
    list(APPEND ZR_SPV_OUTPUTS ${ZR_SPV_DIR}/hiz_vs.spv ${ZR_SPV_DIR}/hiz_copy_fs.spv
        ${ZR_SPV_DIR}/hiz_min_fs.spv)
    add_custom_target(vulkan_shaders DEPENDS ${ZR_SPV_OUTPUTS})
endif()

add_library(zkernel      STATIC zkernel/zkernel.cpp)
add_library(zrhi         STATIC
    zrhi/zrhi.cpp
    zrhi/zrhi_handles.cpp
    zrhi/d3d12/zrhi_d3d12_device.cpp
    zrhi/d3d12/zrhi_d3d12_commandlist.cpp
    zrhi/d3d12/zrhi_d3d12_slots.cpp
    zrhi/d3d12/zrhi_d3d12_translate.cpp
    ${ZR_VULKAN_SOURCES}
)
target_link_libraries(zrhi PRIVATE d3d12 dxgi d3dcompiler) # Windows SDK 系统库
if(ZR_HAS_VULKAN)
    # PUBLIC：静态库 zrhi 并入 SHARED zrender.dll 与窗口程序时，消费方链接步骤
    # 也需要 SDK include（vulkan.h）与导入库目录（vulkan-1.lib）
    target_include_directories(zrhi PUBLIC "${ZR_VULKAN_SDK_PATH}/Include")
    target_link_directories(zrhi PUBLIC "${ZR_VULKAN_SDK_PATH}/Lib")
    target_link_libraries(zrhi PUBLIC vulkan-1)
endif()
# TASK-B-R3-01：zrender 并入手写 RenderGraph 纯逻辑层
# （声明/编译/别名/屏障规划与合并；执行经 IZrCommandList，真后端下一任务）。
add_library(zrender_core STATIC
    zrender/zrender.cpp
    zrender/rg_render_graph.cpp
    zrender/rg_export.cpp       # TASK-B-R9-16：RG 编译产物导出（JSON/文本纯函数）
    zrender/gpu_hud.cpp         # TASK-B-R9-16：DrawCall/显存 HUD 统计聚合纯函数
    zrender/devconsole_proto.cpp # TASK-B-R9-16：原生 DevConsole 协议纯逻辑（白名单/信封）
    zrender/renderdoc_capture.cpp # TASK-B-R9-16：RenderDoc 应用内 API 封装（挂接/捕获）
    zrender/zmesh.cpp
    zrender/draw_list.cpp
    zrender/cluster_lighting.cpp
    zrender/shadows.cpp
    zrender/gi_probes.cpp
    zrender/ssr_pass.cpp
) # TASK-B-R4-02: draw_list 并入（共享可见性+sort key+radix 排序+合批）
  # TASK-B-R5-01: cluster_lighting 并入（CPU 簇光源分配+LightData 32B 打包）
  # TASK-B-R5-02: shadows 并入（CascadeSplits 分割+texel snap 级联矩阵+可见性过滤契约）
  # TASK-B-R5-03: gi_probes/ssr_pass 并入（GI 探针辐射缓存+SSR Hi-Z/模糊/RG 声明）
# TASK-B-R9-16：renderdoc_capture.cpp 的第三方头 include 根（renderdoc/renderdoc_app.h）
target_include_directories(zrender_core PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty)
# TASK-B-R6-01：zupscaler 并入超分框架（UpscalerChain 选优/状态机 + Bilinear 基线 +
# FSR 统一运行时后端）。第三方（ffx-fsr）仅消费官方头 + 预编译 dll（运行时
# LoadLibrary），无第三方编译单元 → 无需 /WX 豁免 target；ffx 头在
# fsr_upscaler.cpp 内以 #pragma warning(push) 段隔离（见该文件）。
# TASK-B-R6-02：+ dlss_upscaler（Streamline 骨架，头文件隔离同上）+
# upscaler_caps（DXGI 免设备能力探测）。
# TASK-B-R8-02：+ fg_ffx（FFX FrameGeneration 封装：FG 效果上下文 + 代理交换链 +
# 每帧 Configure/PrepareV2 + Present 包装 + mock 注入点；FG 效果头来自
# ThirdParty/ffx-fsr/framegeneration/include，官方树布局）。
# 注意：zupscaler 链接 d3d12/dxgi/d3dcompiler（Bilinear 基线的自包含 PSO/编译）。
add_library(zupscaler    STATIC
    zupscaler/zupscaler.cpp
    zupscaler/upscaler_chain.cpp
    zupscaler/bilinear_upscaler.cpp
    zupscaler/fsr_upscaler.cpp
    zupscaler/fg_ffx.cpp
    zupscaler/dlss_upscaler.cpp
    zupscaler/upscaler_caps.cpp
)
target_include_directories(zupscaler PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/api/include
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/framegeneration/include
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/upscalers/include
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/streamline/include
)
target_link_libraries(zupscaler PRIVATE d3d12 dxgi d3dcompiler)
add_library(zplugin      STATIC zplugin/zplugin.cpp)

foreach(ZR_TARGET IN ITEMS zkernel zrhi zrender_core zupscaler zplugin)
    # 以 Native/ 为公共 include 根：跨模块引用形如 #include "zkernel/zkernel.h"
    target_include_directories(${ZR_TARGET} PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
    set_target_properties(${ZR_TARGET} PROPERTIES FOLDER "ZRender/Modules")
endforeach()

# zplugin 聚合其余四模块（ZR_GetBuildInfo 引用其版本函数）
target_link_libraries(zplugin
    PUBLIC
        zkernel
        zrhi
        zrender_core
        zupscaler
)

# ---- SHARED zrender.dll ------------------------------------------------------
# 导出符号由 zrender.def 声明，链接器据此从五模块静态库抽取 ZR_* 实现。
add_library(zrender SHARED
    zplugin/dll_main.cpp
    zplugin/zrender.def
)
target_link_libraries(zrender
    PRIVATE
        zkernel
        zrhi
        zrender_core
        zupscaler
        zplugin
)
set_target_properties(zrender PROPERTIES FOLDER "ZRender")

# ---- doctest 冒烟测试 --------------------------------------------------------
enable_testing()

# TASK-B-R1-01：zrender_test 聚合冒烟 + 数据内核单测（doctest main 在 test_smoke）。
# TASK-B-R2-01：增加 RHI 语义层单测（类型/traits/屏障 + 句柄表）。
# TASK-B-R2-02：增加 D3D12 映射表单测（纯查表逻辑，无需 GPU）。
# TASK-B-R2-03：增加槽位分配器/资源状态表单测（纯逻辑，无需 GPU）。
add_executable(zrender_test
    tests/test_smoke.cpp
    tests/test_zkernel_datakernel.cpp
    tests/test_zkernel_migrate.cpp # TASK-B-R8-03：架构型迁移 + chunk 收缩单测（五类场景）
    tests/test_zkernel_parallel.cpp # TASK-B-R9-12：EachParallel 并行查询（等价/负载/禁改防护/压测；std::thread 为 C++17 标准库，MSVC 无需额外链接）
    tests/test_zrhi_types.cpp
    tests/test_zrhi_handles.cpp
    tests/test_zrhi_d3d12_translate.cpp
    tests/test_zrhi_d3d12_slots.cpp
    tests/test_rg_compile.cpp
    tests/test_rg_barriers.cpp
    tests/test_rg_execute.cpp
    tests/test_zmesh.cpp
    tests/test_zmesh_gallery.cpp # TASK-B-R9-13：4 人物真数据 .zmesh 解析（画廊烘焙产物，仓库根相对路径）
    tests/test_draw_list.cpp
    tests/test_zrhi_pso_draw.cpp
    tests/test_cluster_lighting.cpp
    tests/test_shadows.cpp
    tests/test_gi_probes.cpp
    tests/test_ssr_pass.cpp
    tests/test_upscaler.cpp
    tests/test_upscaler_cabi.cpp
    tests/test_fg_ffx.cpp # TASK-B-R8-02：FG 纯函数矩阵 + mock ffx 表注入状态机单测
    tests/test_zrhi_device_stats.cpp # TASK-H-05 F1（ZrDeviceStats 布局护栏 + 失败计数读链）
    tests/test_framegen_inputs.cpp # TASK-B-R9-02（FG 输入 depth/MV 交接校验 C ABI）
    tests/test_zfg_factory_slots.cpp # TASK-R10-03 A-2（DXGI vtable 槽位守卫；CINTERFACE 反算 SDK 布局）
    tests/test_zfg_resize.cpp # TASK-R10-03 A-4（交换链重建：尺寸更新/采集作废/输入重校验）
    tests/test_zfg_dxgi_exports.cpp # TASK-R10-03 A-1（dxgi 导出参数个数表 + .def 覆盖）
    # ---- TASK-R10-05（P2 原生批）------------------------------------------------
    tests/test_zfg_env.cpp # B-2（GetEnvironmentVariableA 安全读取：三态 + 动态量长）
    tests/test_zfg_present_reentry.cpp # B-1（Present 嵌套标志保存/恢复：三层序列）
    tests/test_zfg_input_policy.cpp # A-7（输入消费账目 + 退休引用的 fence 判定）
    tests/test_zfg_host_desc.cpp # A-3（代理建链保留宿主 desc：审计场景逐字段断言）
    tests/test_zfg_fg_shutdown.cpp # B-3/B-7（ZFG_FgShutdown 导出路由 + 空 out 拒绝）
    tests/test_bench_json.cpp # TASK-B-R9-10（bench 回归 --json 单行生成器：转义/格式/单行契约）
    tests/test_rg_export.cpp # TASK-B-R9-16（RG 导出纯函数：JSON/文本/确定性/错误骨架）
    tests/test_gpu_hud.cpp # TASK-B-R9-16（HUD 聚合纯函数：字节估算/滑窗峰值/JSON 格式）
    tests/test_renderdoc_capture.cpp # TASK-B-R9-16（RenderDoc 挂接/捕获错误路径 + 假表转发）
    tests/test_devconsole_proto.cpp # TASK-B-R9-16（DevConsole 白名单解析/参数/信封转义）
    # zfgprobe 的 hook/wrap/dll 三个编译单元并入测试目标（TASK-R10-05 B-3/B-7 需要
    # 直接驱动 HookCreateSwapChainForHwnd / WrapShutdown / ZFG_FgShutdown）。
    # 注意：zfg_dxgi_proxy.cpp **有意不并入**——其 ForwardArgs 会把测试进程内的
    # ::CreateDXGIFactory* 解析劫持到代理转发（TASK-R10-03 W1 的结论，不得回退）。
    zfgprobe/zfg_probe_hook.cpp
    zfgprobe/zfg_probe_wrap.cpp
    zfgprobe/zfg_probe_dll.cpp
) # TASK-B-R4-02 + TASK-B-R4-03（PSO 校验+绘制命令序列）

# ---- Vulkan 专属测试：仅在 SDK 可用时编译 --------------------------------------
# TASK-A-CI-01：这些文件直接 include <vulkan/vulkan.h>（经 zrhi/vulkan/zrhi_vulkan_device.h），
# 无 SDK 时无条件加入会让 zrender_test 目标编译失败（error C1083，云端 CI 实测）。
# 与上面 ZR_VULKAN_SOURCES 的条件保持一致：D3D12 路径不受影响。
if(ZR_HAS_VULKAN)
    target_sources(zrender_test PRIVATE
        tests/test_zrhi_vulkan_translate.cpp
        tests/test_zrhi_vulkan_spv.cpp        # TASK-B-R8-01：--r5 SPIR-V 清单完整性（8 着色器/16 入口）
        tests/test_zrhi_vulkan_result.cpp     # TASK-R10-03 A-5/A-6（VkResult 分类 + 提交/呈现闸）
        tests/test_zrhi_vulkan_recreate.cpp   # TASK-B-R9-09（交换链真重建状态机：尺寸裁决/台账/粘性清除与回粘）
        tests/test_zrhi_vulkan_bindless.cpp   # B-4（四上限守卫 + 容量收缩，纯逻辑矩阵）
        tests/test_zrhi_barrier_truncate.cpp  # B-5（>128 屏障批次截断计数，真 Vulkan 设备）
    )
endif()
  # TASK-B-R5-01（簇光源分配单测：精确断言/不重叠/边界/覆盖）
  # TASK-B-R5-02（CSM 分割/texel snap 稳定/可见性共享单测）
  # TASK-B-R5-03（GI 探针三线性/辐照度/轮转 + SSR mip 链/模糊核/RG 声明 mock 单测）
  # TASK-B-R6-01（能力矩阵 mock 选优/Chain 状态机/MV 布局常量/jitter 序列单测）
  # TASK-B-R6-02（Dlss 门控/三后端选优矩阵/FSR4 provider 版本 mock/C ABI 参数校验）
target_include_directories(zrender_test PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty # #include "doctest/doctest.h"
    # TASK-B-R8-02：test_fg_ffx 直含 ffx 官方头（mock 表类型与 desc 断言）
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/api/include
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/framegeneration/include
)
if(ZR_HAS_VULKAN)
    # Vulkan 映射表单测（test_zrhi_vulkan_translate）需要 SDK 头与导入库
    target_include_directories(zrender_test PRIVATE "${ZR_VULKAN_SDK_PATH}/Include")
    target_link_directories(zrender_test PRIVATE "${ZR_VULKAN_SDK_PATH}/Lib")
    target_link_libraries(zrender_test PRIVATE vulkan-1)
    # TASK-H-05 F1：test_zrhi_device_stats 的 Vulkan 读链用例（真 VkDescriptorPool
    # 耗尽 → GetDeviceStats）与窗口程序同一个开关；SDK 缺失时该段整体不编译，
    # 布局护栏用例（不依赖 Vulkan）仍然生效。
    target_compile_definitions(zrender_test PRIVATE ZR_HAS_VULKAN=1)
    # TASK-B-R8-01：test_zrhi_vulkan_spv 消费 glslc 产物（shaders_spv/）——
    # 测试运行前先确保 16 个 SPIR-V 全部产出（清单完整性用例的前提）
    add_dependencies(zrender_test vulkan_shaders)
endif()
target_link_libraries(zrender_test
    PRIVATE
        zkernel
        zrhi
        zrender_core
        zupscaler
        zplugin
)
set_target_properties(zrender_test PROPERTIES FOLDER "ZRender/Tests")

# TASK-G-63：显式声明用例工作目录 = 仓库根（Native/ 的上级）。ctest 默认把工作
# 目录设为二进制目录 Native/build，会让 test_zrhi_vulkan_spv 的相对路径候选
# （Native/build/bin/shaders_spv/ 等）全部落空、误报"SPIR-V missing"；这里与
# AGENTS.md 规定的"仓库根调用"对齐，使 ctest 与直接运行 exe 走同一条候选路径。
get_filename_component(ZR_TEST_WORKING_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.." REALPATH)
add_test(NAME zrender_test COMMAND zrender_test
         WORKING_DIRECTORY "${ZR_TEST_WORKING_DIR}")

# ---- TASK-B-R1-01：zkernel 基准程序 -------------------------------------------
# 数字进施工报告，不做 pass/fail 判定 → 不注册 ctest；
# RDTSC 周期数仅 Release 有意义 → 其它配置排除默认构建。
add_executable(zkernel_bench tests/zkernel_bench.cpp)
target_link_libraries(zkernel_bench PRIVATE zkernel)
set_target_properties(zkernel_bench PROPERTIES
    FOLDER "ZRender/Tests"
    EXCLUDE_FROM_DEFAULT_BUILD YES
    EXCLUDE_FROM_DEFAULT_BUILD_RELEASE NO
)

# ---- TASK-B-R4-02：DrawList 基准程序 ------------------------------------------
# 10 万项 Add+Sort+Batch 毫秒数进施工报告，不做 pass/fail 判定 → 不注册 ctest；
# 与 zkernel_bench 同策：仅 Release 默认构建。
add_executable(zrender_bench tests/zrender_bench.cpp)
target_link_libraries(zrender_bench PRIVATE zrender_core)
set_target_properties(zrender_bench PROPERTIES
    FOLDER "ZRender/Tests"
    EXCLUDE_FROM_DEFAULT_BUILD YES
    EXCLUDE_FROM_DEFAULT_BUILD_RELEASE NO
)

# ---- TASK-B-R2-02：D3D12 后端窗口验证程序 ---------------------------------------
# 60 帧红/绿/蓝清屏 + 每 10 帧 readback 中心像素校验 + InfoQueue 零错误。
# 需要真实 GPU/WARP：不进 ctest（验收代理亲自运行）；仅 Release 默认构建。
# ---- TASK-B-R6-02：Streamline 导入库（按官方 ABI 声明本地生成）+ 链接自检 ----
# 官方 SDK zip 276 MB 且 release CDN 在本机不可达（见 ThirdParty/streamline/SOURCE.md），
# 故用 lib /def 从官方 sl_core_api.h 的导出名生成导入库（只含 ABI 描述，无 NVIDIA 代码）。
set(SL_DEF ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/streamline/lib/sl_interposer.def)
set(SL_IMPLIB ${CMAKE_BINARY_DIR}/lib/streamline/sl.interposer.lib)
add_custom_command(
    OUTPUT ${SL_IMPLIB}
    COMMAND ${CMAKE_COMMAND} -E make_directory ${CMAKE_BINARY_DIR}/lib/streamline
    COMMAND ${CMAKE_AR} /nologo /def:${SL_DEF} /machine:x64 /out:${SL_IMPLIB}
    DEPENDS ${SL_DEF}
    COMMENT "generate Streamline import library from official ABI def")
add_custom_target(streamline_importlib DEPENDS ${SL_IMPLIB})

# 链接自检：取 SL_API 函数地址强制解析导入符号（不运行——本机无 sl.interposer.dll）
add_executable(streamline_link_check tests/streamline_link_check.cpp)
add_dependencies(streamline_link_check streamline_importlib)
target_include_directories(streamline_link_check PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/streamline/include)
target_link_libraries(streamline_link_check PRIVATE ${SL_IMPLIB})
set_target_properties(streamline_link_check PROPERTIES
    FOLDER "ZRender/Tests"
    EXCLUDE_FROM_DEFAULT_BUILD YES
    EXCLUDE_FROM_DEFAULT_BUILD_RELEASE NO
)

add_executable(zrhi_d3d12_window tests/zrhi_d3d12_window.cpp)
# TASK-B-R3-02：窗口程序帧循环 RG 驱动（ExecuteAliased/split/别名池）→ 链 zrender_core。
# TASK-B-R6-01：--fsr 模式（UpscalerChain + FFX 运行时）→ 链 zupscaler；构建后把
# 第三方 FFX 预编译 dll 拷到 exe 目录（LoadLibrary 搜索路径）。
# TASK-B-R8-02：--fg 模式（FrameGenerator + FG 代理交换链）；--fg 直含
# ffx_api_dx12.h（表面格式映射）→ 显式给 ffx include 目录（zupscaler 的 PRIVATE
# 不传播）；构建后补拷 FG 组件 dll 与 AGS 库（FG 运行时依赖）。
target_include_directories(zrhi_d3d12_window PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/api/include)
target_link_libraries(zrhi_d3d12_window PRIVATE zrhi zrender_core zupscaler zplugin d3d12 dxgi)
# TASK-B-R9-16：--devconsole 的 TCP 控制台（winsock2，只绑 127.0.0.1）
target_link_libraries(zrhi_d3d12_window PRIVATE ws2_32)
if(ZR_HAS_VULKAN)
    # TASK-B-R7-01：--backend vulkan 双后端路径（ZR_HAS_VULKAN 编译开关 + SPIR-V 产物；
    # vulkan.h/导入库经 zrhi 的 PUBLIC 传播已就位，vulkan-1.lib 直接从 zrhi 传递）
    target_compile_definitions(zrhi_d3d12_window PRIVATE ZR_HAS_VULKAN=1)
    add_dependencies(zrhi_d3d12_window vulkan_shaders)
endif()
add_custom_command(TARGET zrhi_d3d12_window POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_fidelityfx_upscaler_dx12.dll
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_fidelityfx_loader_dx12.dll
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_fidelityfx_framegeneration_dx12.dll
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_ags_x64.dll
        $<TARGET_FILE_DIR:zrhi_d3d12_window>
    COMMENT "copy FFX runtime dlls (upscaler + framegeneration + ags) next to zrhi_d3d12_window.exe")
set_target_properties(zrhi_d3d12_window PROPERTIES
    FOLDER "ZRender/Tests"
    EXCLUDE_FROM_DEFAULT_BUILD YES
    EXCLUDE_FROM_DEFAULT_BUILD_RELEASE NO
)

# ---- TASK-B-R9-01 阶段 0：DXGI 交换链探测 hook（spike，默认不构建）-----------
# 目的：实证 Unity 2022.3/D3D12 走哪条 DXGI 交换链创建路径（只记录不替换）。
# 门禁结论为 STOP 时这些目标不进入产品构建：全部 EXCLUDE_FROM_DEFAULT_BUILD，
# 需要复现证据时显式构建：
#   cmake --build Native/build --config Release --target zfg_probe zfg_probe_inject
#   cmake --build Native/build --config Release --target zfg_probe_selftest
# 均不注册 ctest（需要真实 GPU 窗口，由代理手动运行）。
add_library(zfgprobe STATIC zfgprobe/zfg_probe_hook.cpp zfgprobe/zfg_probe_wrap.cpp)
target_include_directories(zfgprobe PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_include_directories(zfgprobe PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/api/include )
target_link_libraries(zfgprobe PRIVATE zupscaler d3d12 dxgi d3dcompiler)
set_target_properties(zfgprobe PROPERTIES
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)

add_library(zfg_probe SHARED zfgprobe/zfg_probe_dll.cpp)
target_link_libraries(zfg_probe PRIVATE zfgprobe zupscaler d3d12 dxgi d3dcompiler)
set_target_properties(zfg_probe PROPERTIES
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)

# ---- TASK-B-R9-03：dxgi.dll 代理（放 Player 输出目录，同目录优先加载）------------
# 代理必须**叫 dxgi.dll**（同名代理），所以绝对不能导入 dxgi.lib——那会解析到它
# 自己。真实实现全部经 GetProcAddress（全路径 System32\dxgi.dll）取得。
# 用静态 CRT（/MT）：本模块在进程启动期就被加载，不依赖目标机装没装 VC 运行时。
add_library(zfg_dxgi SHARED
    zfgprobe/zfg_dxgi_proxy.cpp
    zfgprobe/zfg_dxgi.def
)
target_include_directories(zfg_dxgi PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
set_target_properties(zfg_dxgi PROPERTIES
    OUTPUT_NAME "dxgi"
    MSVC_RUNTIME_LIBRARY "MultiThreaded"
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)

# hook 模块（与 spike 的 zfg_probe.dll 同源）：代理在首次 CreateDXGIFactory* 时
# 惰性加载它；缺失/加载失败时代理只做转发（Unity 照常启动）。
add_library(zfg_hook SHARED zfgprobe/zfg_probe_dll.cpp)
target_link_libraries(zfg_hook PRIVATE zfgprobe zupscaler d3d12 dxgi d3dcompiler)
set_target_properties(zfg_hook PROPERTIES
    OUTPUT_NAME "zfg_hook"
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)

add_executable(zfg_probe_selftest tests/zfg_probe_selftest.cpp)
target_link_libraries(zfg_probe_selftest PRIVATE zfgprobe d3d12 dxgi)
# TASK-R10-05：selftest 新增 wrap 模式阶段（A-3 宿主 desc 对账 / B-3 关停断言）——
# 需要 FFX FG 运行时在 exe 目录（与窗口程序同一份拷贝）
add_custom_command(TARGET zfg_probe_selftest POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_fidelityfx_loader_dx12.dll
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_fidelityfx_framegeneration_dx12.dll
        ${CMAKE_CURRENT_SOURCE_DIR}/ThirdParty/ffx-fsr/bin/amd_ags_x64.dll
        $<TARGET_FILE_DIR:zfg_probe_selftest>
    COMMENT "copy FFX FG runtime dlls next to zfg_probe_selftest.exe")
set_target_properties(zfg_probe_selftest PROPERTIES
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)

# 早期注入启动器：Unity 原生插件是惰性加载的，交换链却在引擎初始化期创建，
# 因此探测/接线必须比引擎更早（细节见 zfg_probe_inject.cpp 头注）。
add_executable(zfg_probe_inject zfgprobe/zfg_probe_inject.cpp)
set_target_properties(zfg_probe_inject PROPERTIES
    FOLDER "ZRender/Spike"
    EXCLUDE_FROM_DEFAULT_BUILD YES
)
