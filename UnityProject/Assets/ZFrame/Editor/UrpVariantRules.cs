using System;
using System.Collections.Generic;

namespace ZFrame.Editor
{
    /// <summary>
    /// URP shader 变体剥离的纯判定逻辑（TASK-A-R9-16）。
    ///
    /// 刻意不引用 UnityEditor.Build / UnityEngine：本类只做「给定一组已启用的
    /// 关键字，是否应剥离」的确定性判定，供 EditMode 单测直接调用。真正的
    /// IPreprocessShaders 适配层见 UrpVariantStripper.cs。
    ///
    /// ===== 判据来源（本工程实测核对，非照抄任务简报候选表）=====
    /// 锁定环境：Unity 2022.3.62f3c1 + URP（manifest.json / packages-lock.json
    /// 声明 14.0.11；本机 Library/PackageCache 目录为 14.0.12）+ Windows x64
    /// (780M 核显 / D3D12) 与 Android IL2CPP arm64。内置管线 / HDRP 不适用。
    ///
    /// 资产事实（逐个打开核对）：
    ///  * Assets/Settings/Pipeline/URP_Low.asset / URP_Medium.asset / URP_High.asset
    ///      - m_AdditionalLightsRenderingMode: 1 (PerPixel) 三档一致
    ///      - m_AdditionalLightShadowsSupported: 0 三档一致
    ///      - m_SupportsLightLayers: 0 三档一致
    ///      - m_ReflectionProbeBlending: 0 / m_ReflectionProbeBoxProjection: 0 三档一致
    ///      - m_SupportsLightCookies: 1 / m_MixedLightingSupported: 1
    ///        / m_EnableLODCrossFade: 1 / m_MainLightShadowsSupported: 1（这些必须保留）
    ///      - m_ShadowCascadeCount: Low=1 / Medium=2 / High=2（CASCADE 必须保留，见下）
    ///      - m_SoftShadowsSupported: Low=0 / Medium=0 / High=1（SOFT 必须保留，见下）
    ///      - m_PrefilteringModeForwardPlus: 0 / m_PrefilteringModeDeferredRendering: 0
    ///      - m_PrefilterDebugKeywords: 1 / m_PrefilterWriteRenderingLayers: 1
    ///  * Assets/Settings/Pipeline/URP_{Low,Medium,High}_Renderer.asset
    ///      - m_RenderingMode: 0 (Forward)、m_UseNativeRenderPass: 0
    ///      - m_RendererFeatures: [] —— 无 SSAO / Decal / ScreenSpaceShadows / RenderObjects
    ///  * Assets/Settings/Pipeline/Vol_{Low,Medium,High}.asset
    ///      - Low: components: []（无后处理）
    ///      - Medium: Bloom
    ///      - High: Bloom + ColorAdjustments
    ///    => UberPost 的 _BLOOM_* / _TONEMAP_* / _HDR_GRADING 等为必需，列入白名单保护。
    ///  * ProjectSettings/QualitySettings.asset
    ///      - 三档各自 customRenderPipeline 绑定 URP_Low/Medium/High，
    ///        m_PerPlatformDefaultQuality 全为 1，excludedTargetPlatforms: []。
    ///        ZFrame.View.RenderPipelineSelector 注释亦确认 tier matrix 运行期按画质档切换。
    ///      - 结论：三档都可能在同一包内被激活，剥离必须取「三档并集都不需要」
    ///        的保守交集，不能按「当前激活档」剥。
    ///  * Assets/UniversalRenderPipelineGlobalSettings.asset
    ///      - m_StripUnusedVariants: 1（URP 自带 ShaderScriptableStripper 已开启）
    ///      - m_StripUnusedPostProcessingVariants: 0（后处理变体未被 URP 剥）
    ///      - m_ShaderVariantLogLevel: 0（不产出变体日志，构建日志解析路径不可得）
    ///  * ProjectSettings/GraphicsSettings.asset
    ///      - m_AlwaysIncludedShaders 第 8 项 guid 0406db5a… = URP
    ///        Shaders/Particles/ParticlesUnlit.shader（A-AND-06 12288 变体的直接来源）
    ///  * 关键字精确名来自 URP Runtime/UniversalRenderPipelineCore.cs :: ShaderKeywordStrings
    ///    与 Shaders/Particles/ParticlesUnlit.shader、Shaders/PostProcessing/UberPost.shader
    ///    的 #pragma 行，未凭空拼写。
    ///
    /// ===== 与简报候选表的差异（核对后推翻，故意不照抄）=====
    ///  * 简报把 _MAIN_LIGHT_SHADOWS_CASCADE 列为候选：本工程三档
    ///    m_MainLightShadowsSupported=1 且 Medium/High cascade=2，运行期切档会用到；
    ///    URP 自带 ShaderBuildPreprocessor.cs:646-648 亦注明「Cascade count can be
    ///    changed at runtime, so include both of them」。故 CASCADE 不剥（本表无此规则）。
    ///  * 简报把 _WRITE_RENDERING_LAYERS 列为候选：核对后成立（三档
    ///    m_SupportsLightLayers=0 + renderer features 空），但 URP 自带 prefilter
    ///    m_PrefilterWriteRenderingLayers=1 已在处理，本表保留作为显式可审计补充。
    ///  * 简报把 DEBUG_DISPLAY 列为候选：仅在「非 Development 正式构建」成立，
    ///    故设 ReleaseBuildOnly=true，Development 构建保留。
    ///
    /// 本剥离器是独立于 URP 自带 stripper 的「可配置 / 可测 / 默认关闭」补充层；
    /// 多条规则与 URP 预过滤重叠，不宣称能据此进一步降低 12288（未验证，见施工报告）。
    /// </summary>
    public static class UrpVariantRules
    {
        /// <summary>一条剥离规则：命中该关键字的组合可剥离，并附可审计判据。</summary>
        public sealed class StripRule
        {
            public string Keyword { get; }
            public string Rationale { get; }

            /// <summary>
            /// true = 仅正式（非 Development）构建生效；Development 构建保留该组合。
            /// 用于 DEBUG_DISPLAY 这类只在调试构建有意义的分支。
            /// </summary>
            public bool ReleaseBuildOnly { get; }

            public StripRule(string keyword, string rationale, bool releaseBuildOnly = false)
            {
                Keyword = keyword;
                Rationale = rationale;
                ReleaseBuildOnly = releaseBuildOnly;
            }
        }

        /// <summary>
        /// 白名单保护：Volume / UberPost 必需关键字。命中任一即 Keep，优先级高于
        /// StripRules —— 防止将来误加规则剥掉 Bloom / ColorAdjustments 导致后处理
        /// 变粉红或消失。来源：UberPost.shader 的 #pragma 列表 + Vol_Medium(High).asset。
        /// 三档 URP 必需的光照关键字（_MAIN_LIGHT_SHADOWS / _MAIN_LIGHT_SHADOWS_CASCADE /
        /// _ADDITIONAL_LIGHTS / _SHADOWS_SOFT / _LIGHT_COOKIES / LOD_FADE_CROSSFADE 等）
        /// 不在 StripRules 内，天然 Keep，无需列入本白名单。
        /// </summary>
        public static readonly string[] ProtectedKeywords =
        {
            "_DISTORTION", "_CHROMATIC_ABERRATION",
            "_BLOOM_LQ", "_BLOOM_HQ", "_BLOOM_LQ_DIRT", "_BLOOM_HQ_DIRT",
            "_HDR_GRADING", "_TONEMAP_ACES", "_TONEMAP_NEUTRAL",
            "_FILM_GRAIN", "_DITHERING", "_GAMMA_20",
            "_LINEAR_TO_SRGB_CONVERSION", "_USE_FAST_SRGB_LINEAR_CONVERSION",
            "HDR_INPUT", "HDR_ENCODING",
        };

        /// <summary>
        /// 核对后确认「URP_Low/Medium/High 三档并集都用不到」的关键字规则表。
        /// 这是可配置规则集：增删条目即可改变剥离行为（通过规则数组，或通过
        /// UrpVariantStripper 的逐条 EditorPrefs 开关）。每条都带判据。
        /// </summary>
        public static readonly StripRule[] StripRules =
        {
            new StripRule("_ADDITIONAL_LIGHTS_VERTEX",
                "三档 m_AdditionalLightsRenderingMode=1(PerPixel)，无 PerVertex；URP m_PrefilteringModeAdditionalLight=3(SelectPixelAndOff) 印证"),
            new StripRule("_ADDITIONAL_LIGHT_SHADOWS",
                "三档 m_AdditionalLightShadowsSupported=0"),
            new StripRule("_LIGHT_LAYERS",
                "三档 m_SupportsLightLayers=0"),
            new StripRule("_WRITE_RENDERING_LAYERS",
                "三档 m_SupportsLightLayers=0 且 URP_*_Renderer.asset 的 m_RendererFeatures=[]（无 Decal/SSAO 等需要 rendering layers 的 feature）"),
            new StripRule("_REFLECTION_PROBE_BLENDING",
                "三档 m_ReflectionProbeBlending=0"),
            new StripRule("_REFLECTION_PROBE_BOX_PROJECTION",
                "三档 m_ReflectionProbeBoxProjection=0"),
            new StripRule("_FORWARD_PLUS",
                "三档 Renderer m_RenderingMode=0(Forward)、m_PrefilteringModeForwardPlus=0"),
            new StripRule("_SCREEN_SPACE_OCCLUSION",
                "三档 m_RendererFeatures=[] 无 SSAO feature；URP asset 的 m_PrefilterSSAO*=1 / m_PrefilteringModeScreenSpaceOcclusion=0"),
            new StripRule("_MAIN_LIGHT_SHADOWS_SCREEN",
                "三档 m_RendererFeatures=[] 无 ScreenSpaceShadows feature"),
            new StripRule("_DBUFFER_MRT1",
                "三档无 Decal renderer feature；URP m_PrefilterDBufferMRT1=1 已预过滤"),
            new StripRule("_DBUFFER_MRT2",
                "三档无 Decal renderer feature；URP m_PrefilterDBufferMRT2=1 已预过滤"),
            new StripRule("_DBUFFER_MRT3",
                "三档无 Decal renderer feature；URP m_PrefilterDBufferMRT3=1 已预过滤"),
            new StripRule("_DECAL_NORMAL_BLEND_LOW",
                "三档无 Decal renderer feature"),
            new StripRule("_DECAL_NORMAL_BLEND_MEDIUM",
                "三档无 Decal renderer feature"),
            new StripRule("_DECAL_NORMAL_BLEND_HIGH",
                "三档无 Decal renderer feature"),
            new StripRule("_DECAL_LAYERS",
                "三档无 Decal renderer feature"),
            new StripRule("_RENDER_PASS_ENABLED",
                "三档 m_UseNativeRenderPass=0 且为 Forward（非 Deferred）"),
            new StripRule("_DEFERRED_STENCIL",
                "三档 m_PrefilteringModeDeferredRendering=0（Forward only），无 Deferred renderer"),
            new StripRule("_DEFERRED_FIRST_LIGHT",
                "三档 m_PrefilteringModeDeferredRendering=0（Forward only），无 Deferred renderer"),
            new StripRule("_CASTING_PUNCTUAL_LIGHT_SHADOW",
                "三档 m_AdditionalLightShadowsSupported=0，无 punctual light shadow caster 变体"),
            new StripRule("DEBUG_DISPLAY",
                "正式构建无调试显示；GlobalSettings m_StripDebugVariants=1 + 三档 m_PrefilterDebugKeywords=1",
                releaseBuildOnly: true),
        };

        /// <summary>判定结果：是否剥离 + 可审计理由。</summary>
        public readonly struct Decision
        {
            public bool Strip { get; }
            public string Reason { get; }

            public Decision(bool strip, string reason)
            {
                Strip = strip;
                Reason = reason;
            }
        }

        /// <summary>
        /// 纯函数：给定「一个 shader 变体被启用的关键字集合」，判断是否应剥离。
        ///
        /// 约定：
        ///  * enabledKeywords 为 null / 空 → 一律 Keep（空关键字边界，绝不剥空组合）。
        ///  * 关键字比较大小写敏感（StringComparison.Ordinal）：Unity 关键字为大写，
        ///    小写串视为不同关键字、不命中 —— 防止误伤。
        ///  * 白名单保护优先于剥离规则；三档必需的 Volume 关键字不会被剥。
        ///  * platform 仅作证据字段参与记录。核对结论是三档在两个目标平台
        ///    （Windows x64 / Android arm64）都打包且 QualitySettings 没有平台排除，
        ///    故规则刻意「不按平台分叉」；Evaluate 对两平台返回一致结果（有单测锁定）。
        ///  * isDevelopmentBuild 对 ReleaseBuildOnly 规则生效（DEBUG_DISPLAY 分支）。
        ///  * rules = null 时用默认 StripRules；传入自定义数组可完整替换规则（供测试 / 配置）。
        ///  * disabledRuleKeywords = null 时全部规则启用；否则其中列出的关键字对应规则跳过
        ///    （UrpVariantStripper 用它实现逐条 EditorPrefs 开关）。
        ///  * shaderName 不参与判定，仅作为证据维度保留（本层不按 shader 白名单豁免，
        ///    豁免统一由 ProtectedKeywords 表达，保持纯函数最小）。
        /// </summary>
        public static Decision Evaluate(
            string shaderName,
            IReadOnlyList<string> enabledKeywords,
            string platform,
            bool isDevelopmentBuild,
            IReadOnlyList<StripRule> rules = null,
            ISet<string> disabledRuleKeywords = null)
        {
            if (enabledKeywords == null || enabledKeywords.Count == 0)
            {
                return new Decision(false, "空关键字组合：不剥离");
            }

            // 1) 白名单保护优先：Volume / 后处理必需组合一律保留。
            foreach (string keyword in enabledKeywords)
            {
                if (keyword == null)
                {
                    continue;
                }

                foreach (string protectedKeyword in ProtectedKeywords)
                {
                    if (string.Equals(keyword, protectedKeyword, StringComparison.Ordinal))
                    {
                        return new Decision(false,
                            "白名单保护（Volume/后处理必需）：" + protectedKeyword);
                    }
                }
            }

            // 2) 剥离规则：大小写敏感、精确匹配。
            IReadOnlyList<StripRule> effectiveRules = rules ?? StripRules;
            foreach (StripRule rule in effectiveRules)
            {
                if (rule == null || rule.Keyword == null)
                {
                    continue;
                }

                if (disabledRuleKeywords != null && disabledRuleKeywords.Contains(rule.Keyword))
                {
                    continue;
                }

                if (rule.ReleaseBuildOnly && isDevelopmentBuild)
                {
                    continue;
                }

                foreach (string keyword in enabledKeywords)
                {
                    if (string.Equals(keyword, rule.Keyword, StringComparison.Ordinal))
                    {
                        return new Decision(true,
                            "命中规则 [" + rule.Keyword + "]：" + rule.Rationale);
                    }
                }
            }

            // 3) 三档必需且未命中任何剥离规则（如 _MAIN_LIGHT_SHADOWS /
            //    _MAIN_LIGHT_SHADOWS_CASCADE / _ADDITIONAL_LIGHTS / _SHADOWS_SOFT /
            //    _LIGHT_COOKIES / LOD_FADE_CROSSFADE）一律保留。
            return new Decision(false, "未命中剥离规则（三档必需或不适用）：保留");
        }
    }
}
