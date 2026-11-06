// =============================================================================
// scene_draw.hlsl —— Unity 场景提交着色器（TASK-B-UNITY-03）
//
// 与 first_draw.hlsl 的关系：**不改** first_draw.hlsl。那份是 --draw 与
// B-UNITY-02 的内嵌基线（单测逐字节比对它，且 --draw 的常量布局是 96B），
// 场景提交需要"逐物件世界矩阵 + 逐物件颜色"，常量布局因此多两块，独立成
// 本文件（内嵌副本在 zrhi_d3d12_unity_target.cpp 的 kSceneHlsl）。
//
// 根签名约定的 b0 常量布局（176B，D3D 行主序存储）：
//   row_major float4x4 mvp      // 64B：proj * view * world
//   row_major float4x4 world    // 64B：model（法线要用它的 3x3，见下）
//   float4 lightDir             // 16B：xyz = 指向光源的方向（世界空间，已归一化）
//   float4 lightColor           // 16B：rgb = 光色
//   float4 albedo               // 16B：rgb = 物件基色（Unity sharedMaterial 色）
//
// 顶点输入与 first_draw.hlsl 完全一致（v1 固定布局，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 为什么法线要用 world 矩阵：Unity 的顶点法线是**模型空间**的，顶点数据按
// mesh 缓存（同一 mesh 被多个 transform 复用），所以法线只能在 VS 里随物件
// 变换——否则所有旋转过的物件都会照出面黑的背光面。
// =============================================================================

cbuffer CBufSceneDraw : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 world;
    float4 lightDir;
    float4 lightColor;
    float4 albedo;
};

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = mul(mvp, float4(input.pos, 1.0f));
    // 法线转到世界空间（旋转 + 均匀缩放；非均匀缩放由 PS 的 normalize 兜住量级）
    output.normal = mul((float3x3)world, input.normal);
    output.uv = input.uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 l = normalize(lightDir.xyz);
    // 双面光照：地面 Plane 与部分资产的绕序不保证，背光面给半强度而不是全黑
    float ndl = saturate(dot(n, l));
    // 环境项抬到 0.45：ZRender 没有 URP 的球谐环境光，纯 Lambert 会把背光面压成
    // 剪影（实测胶囊/角色发黑），这里用固定环境项兜住"能看清"这条底线。
    float3 ambient = float3(0.45f, 0.47f, 0.52f);
    float3 lit = lightColor.rgb * ndl;
    return float4(albedo.rgb * (ambient + lit), 1.0f);
}
