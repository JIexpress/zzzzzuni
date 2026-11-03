// ZrFrameGenInputs.cs —— FG 输入（depth/MV）交接校验的托管入口（TASK-B-R9-02）。
// 只负责"把 Unity 侧纹理句柄 + 尺寸 + 格式"翻译成原生体检参数，并回读结论；
// 不创建设备、不碰 FFX——真正的 FG 接线属后续任务。

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>
    /// 交接校验错误码（与 zplugin.h 的 ZR_FG_INPUT_* 同值）。
    /// </summary>
    public enum ZrFrameGenInputError
    {
        /// <summary>通过。</summary>
        Ok = 0,

        /// <summary>参数为空。</summary>
        Null = -1,

        /// <summary>结构体版本（StructSize）不符。</summary>
        Abi = -2,

        /// <summary>纹理句柄为空。</summary>
        TexturePtr = -3,

        /// <summary>尺寸非法（0 / 超上限）。</summary>
        Size = -4,

        /// <summary>depth 与 MV 尺寸不一致。</summary>
        Mismatch = -5,

        /// <summary>格式不在白名单。</summary>
        Format = -6
    }

    /// <summary>交接校验结论（可断言）。</summary>
    public readonly struct ZrFrameGenInputsReport
    {
        internal ZrFrameGenInputsReport(bool accepted, int width, int height, int depthBytesPerPixel, int mvBytesPerPixel)
        {
            Accepted = accepted;
            Width = width;
            Height = height;
            DepthBytesPerPixel = depthBytesPerPixel;
            MvBytesPerPixel = mvBytesPerPixel;
        }

        /// <summary>是否可交给后续 FG 接线。</summary>
        public bool Accepted { get; }

        /// <summary>通过的公共宽度（失败时为 0）。</summary>
        public int Width { get; }

        /// <summary>通过的公共高度（失败时为 0）。</summary>
        public int Height { get; }

        /// <summary>深度每像素字节数（失败时为 0）。</summary>
        public int DepthBytesPerPixel { get; }

        /// <summary>运动矢量每像素字节数（失败时为 0）。</summary>
        public int MvBytesPerPixel { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return "accepted=" + Accepted + " " + Width + "x" + Height
                + " depthBpp=" + DepthBytesPerPixel + " mvBpp=" + MvBytesPerPixel;
        }
    }

    /// <summary>
    /// depth/MV 交接校验：把纹理句柄与格式交给原生做非空/尺寸/格式体检。
    /// 纹理格式按 <see cref="GraphicsFormat"/> 自动判别，判别不出时返回
    /// <see cref="ZrFrameGenInputError.Format"/>（不做猜测）。
    /// </summary>
    public static class ZrFrameGenInputs
    {
        /// <summary>按纹理自身格式校验（URP 的 _CameraDepthTexture / _MotionVectorTexture 走这条）。</summary>
        public static ZrFrameGenInputError Validate(Texture depth, Texture motionVectors, out ZrFrameGenInputsReport report,
            uint maxDimension = 0u)
        {
            if (depth == null || motionVectors == null)
            {
                report = new ZrFrameGenInputsReport(false, 0, 0, 0, 0);
                return ZrFrameGenInputError.Null;
            }

            return Validate(
                depth.GetNativeTexturePtr(), depth.width, depth.height, ToNativeFormat(depth.graphicsFormat),
                motionVectors.GetNativeTexturePtr(), motionVectors.width, motionVectors.height, ToNativeFormat(motionVectors.graphicsFormat),
                out report, maxDimension);
        }

        /// <summary>按显式句柄/尺寸/格式校验（调用方已知格式时用这条，例如深度附件的 fmt=Depth）。</summary>
        public static ZrFrameGenInputError Validate(IntPtr depth, int depthWidth, int depthHeight, ZrFgTexFormatNative depthFormat,
            IntPtr motionVectors, int mvWidth, int mvHeight, ZrFgTexFormatNative mvFormat,
            out ZrFrameGenInputsReport report, uint maxDimension = 0u)
        {
            var desc = new ZrFgInputDescNative
            {
                StructSize = (uint)Marshal.SizeOf<ZrFgInputDescNative>(),
                Depth = depth,
                DepthWidth = depthWidth < 0 ? 0u : (uint)depthWidth,
                DepthHeight = depthHeight < 0 ? 0u : (uint)depthHeight,
                DepthFormat = (uint)depthFormat,
                MotionVectors = motionVectors,
                MvWidth = mvWidth < 0 ? 0u : (uint)mvWidth,
                MvHeight = mvHeight < 0 ? 0u : (uint)mvHeight,
                MvFormat = (uint)mvFormat,
                MaxDimension = maxDimension
            };

            int rc = ZrFrameGenNative.ValidateInputs(ref desc, out ZrFgInputReportNative native);
            report = new ZrFrameGenInputsReport(
                native.Accepted != 0u,
                (int)native.Width,
                (int)native.Height,
                (int)native.DepthBytesPerPixel,
                (int)native.MvBytesPerPixel);
            return (ZrFrameGenInputError)rc;
        }

        /// <summary>GraphicsFormat → 原生白名单格式；判别不出返回 Unknown（交给原生拒绝）。</summary>
        internal static ZrFgTexFormatNative ToNativeFormat(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R32_SFloat:
                    return ZrFgTexFormatNative.R32SFloat;
                case GraphicsFormat.D32_SFloat:
                case GraphicsFormat.D32_SFloat_S8_UInt:
                    return ZrFgTexFormatNative.D32SFloat;
                case GraphicsFormat.R16G16_SFloat:
                    return ZrFgTexFormatNative.R16G16SFloat;
                default:
                    return ZrFgTexFormatNative.Unknown;
            }
        }
    }
}
