// zrender.cpp —— 渲染图模块占位实现（TASK-B-R0-01）
#include "zrender/zrender.h"

extern "C" {

const char* ZREN_GetVersion(void)
{
    return ZR_VERSION_STRING; // 由 CMake 注入，单一事实来源
}

} // extern "C"
