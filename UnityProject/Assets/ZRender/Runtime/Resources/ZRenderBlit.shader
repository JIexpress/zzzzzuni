// ZRenderBlit.shader —— 把 ZRender 的共享纹理贴到相机目标（TASK-B-UNITY-02）。
//
// 为什么不用 CommandBuffer.Blit(src, dst) 的内置 blit 材质：内置 blit 着色器在
// Player 构建里可能被剥离（没有材质引用它），实测在 D3D12 Player 上会崩在
// UnityPlayer 内部（0xc0000005）。这里自带一个最小着色器，放在 Resources 下保证
// 进包，运行时 Shader.Find 也就不依赖 "Always Included Shaders" 配置。
//
// _FlipY：ZRender 用 D3D 约定渲染（纹理原点在左上），Unity 采样时做一次 Y 翻转
// 才能与屏幕坐标一致。由 C# 侧按需设置（材质属性）。

Shader "ZRender/Blit"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _FlipY ("Flip Y", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _FlipY;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                // D3D 渲染的纹理原点在上：翻转 Y 以匹配 Unity 的屏幕方向
                if (_FlipY > 0.5)
                {
                    o.uv.y = 1.0 - o.uv.y;
                }
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }

    Fallback Off
}
