// zrhi.cpp —— RHI 占位实现（TASK-B-R0-01）
#include "zrhi/zrhi.h"

extern "C" {

const char* ZRHI_GetVersion(void)
{
    return ZR_VERSION_STRING; // 由 CMake 注入，单一事实来源
}

} // extern "C"
