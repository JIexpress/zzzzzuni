// ZMesh.cs —— .zmesh v1 格式常量/头定义 + 托管读取器（TASK-B-R4-01）。
//
// 头结构体与 Native/zrender/zmesh.h 的 ZmeshHeader 对齐（两侧各一份并注释）：
//   magic 'Z''M''S''H'(4B) | version u32=1 | vertexCount u32 | indexCount u32 | flags u32
// 均为 POD、显式固定字段、无 padding；字节大小断言两侧都有
// （C++ static_assert==20 / 本文件 Marshal.SizeOf==20 断言见 ZMeshReader.Read）。
// 本文件 magic 用小端 u32 0x48534D5A 表示（'Z'|'M'<<8|'S'<<16|'H'<<24），
// 与 C++ 侧 char[4]="ZMSH" 字节序等值。

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZFrame.Render
{
    /// <summary>.zmesh v1 格式定义（小端）。</summary>
    public static class ZMeshFormat
    {
        /// <summary>magic "ZMSH" 的小端 u32 表示。</summary>
        public const uint Magic = 0x48534D5Au;

        /// <summary>当前格式版本。</summary>
        public const uint Version = 1u;

        /// <summary>flags bit0：含法线列（float3×N）。</summary>
        public const uint FlagNormals = 1u;

        /// <summary>flags bit1：含 uv0 列（float2×N）。</summary>
        public const uint FlagUv0 = 2u;

        /// <summary>头字节数（两侧断言对齐）。</summary>
        public const int HeaderSize = 20;
    }

    /// <summary>
    /// .zmesh 头（POD，显式固定字段，无 padding；与 zmesh.h 的 ZmeshHeader
    /// 字节布局一致——C# 侧以 uint magic 等值表示 char[4]）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ZmeshHeader
    {
        public uint Magic;
        public uint Version;
        public uint VertexCount;
        public uint IndexCount;
        public uint Flags;
    }

    /// <summary>读回的 .zmesh 数据（SoA 列式）。</summary>
    public sealed class ZMeshData
    {
        public uint VertexCount;
        public uint IndexCount;
        public bool HasNormals;
        public bool HasUv;
        /// <summary>float3 交错：x,y,z 连续（长度 3N）。</summary>
        public float[] Positions;
        /// <summary>float3 交错（长度 3N）；无法线列为 null。</summary>
        public float[] Normals;
        /// <summary>float2 交错（长度 2N）；无 uv0 列为 null。</summary>
        public float[] Uv0;
        /// <summary>u32 索引（长度 M）。</summary>
        public uint[] Indices;
    }

    /// <summary>.zmesh 读取器（测试与工具用读回校验；格式与 ZMeshBaker 写入侧互为镜像）。</summary>
    public static class ZMeshReader
    {
        /// <summary>
        /// 读取并校验 .zmesh；任何格式不符（magic/版本/flags/尺寸）抛
        /// <see cref="InvalidDataException"/>。
        /// </summary>
        public static ZMeshData Read(string path)
        {
            // 头大小断言：与 C++ 侧 static_assert(sizeof(ZmeshHeader)==20) 对齐。
            if (Marshal.SizeOf(typeof(ZmeshHeader)) != ZMeshFormat.HeaderSize)
            {
                throw new InvalidDataException(
                    $"ZmeshHeader 托管布局 {Marshal.SizeOf(typeof(ZmeshHeader))}B != 约定 {ZMeshFormat.HeaderSize}B");
            }

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < ZMeshFormat.HeaderSize)
            {
                throw new InvalidDataException("文件小于 .zmesh 头（20B）");
            }

            var header = new ZmeshHeader
            {
                Magic = reader.ReadUInt32(),
                Version = reader.ReadUInt32(),
                VertexCount = reader.ReadUInt32(),
                IndexCount = reader.ReadUInt32(),
                Flags = reader.ReadUInt32(),
            };

            if (header.Magic != ZMeshFormat.Magic)
            {
                throw new InvalidDataException($"坏 magic：0x{header.Magic:X8}");
            }
            if (header.Version != ZMeshFormat.Version)
            {
                throw new InvalidDataException($"不支持的版本：{header.Version}");
            }
            if ((header.Flags & ~(ZMeshFormat.FlagNormals | ZMeshFormat.FlagUv0)) != 0u)
            {
                throw new InvalidDataException($"未知 flags 位：0x{header.Flags:X8}");
            }

            long bodySize = 12L * header.VertexCount;
            if ((header.Flags & ZMeshFormat.FlagNormals) != 0u)
            {
                bodySize += 12L * header.VertexCount;
            }
            if ((header.Flags & ZMeshFormat.FlagUv0) != 0u)
            {
                bodySize += 8L * header.VertexCount;
            }
            bodySize += 4L * header.IndexCount;
            if (stream.Length - ZMeshFormat.HeaderSize != bodySize)
            {
                throw new InvalidDataException(
                    $"体尺寸不一致：声明 {bodySize}B，实际 {stream.Length - ZMeshFormat.HeaderSize}B");
            }

            var data = new ZMeshData
            {
                VertexCount = header.VertexCount,
                IndexCount = header.IndexCount,
                HasNormals = (header.Flags & ZMeshFormat.FlagNormals) != 0u,
                HasUv = (header.Flags & ZMeshFormat.FlagUv0) != 0u,
                Positions = ReadFloats(reader, 3u * header.VertexCount),
                Normals = (header.Flags & ZMeshFormat.FlagNormals) != 0u
                    ? ReadFloats(reader, 3u * header.VertexCount)
                    : null,
                Uv0 = (header.Flags & ZMeshFormat.FlagUv0) != 0u
                    ? ReadFloats(reader, 2u * header.VertexCount)
                    : null,
                Indices = ReadUInt32s(reader, header.IndexCount),
            };
            return data;
        }

        private static float[] ReadFloats(BinaryReader reader, uint count)
        {
            var values = new float[count];
            for (uint i = 0; i < count; i++)
            {
                values[i] = reader.ReadSingle();
            }
            return values;
        }

        private static uint[] ReadUInt32s(BinaryReader reader, uint count)
        {
            var values = new uint[count];
            for (uint i = 0; i < count; i++)
            {
                values[i] = reader.ReadUInt32();
            }
            return values;
        }
    }
}
