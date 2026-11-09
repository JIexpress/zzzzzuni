using System.Collections.Generic;
using Game.Domain.World;
using UnityEngine;

namespace Game.View.World
{
    /// <summary>
    /// TASK-A-WORLD-01：运行时浮点原点（floating origin）。4km 默认阈值。
    ///
    /// 职责边界（刻意很小）：
    ///  - 世界坐标的权威值是 <see cref="OriginOffset"/>（double）+ 物体当前的 Unity 坐标；
    ///    本类只负责「什么时候把原点挪过去」和「把要挪的平移量分摊到注册物体上」。
    ///  - **只有 Register 过的 Transform 会被平移**；UI、宿主、未被登记的东西一律不碰
    ///    （相机是参照物，单独处理，绝不在注册表里被二次平移）。
    ///  - 不做自动挂载：现有场景（像素证据用例、SCN_Game）一个都不受影响。需要时手动
    ///    加组件或由后续任务装配；未挂载即完全无行为。
    ///  - 无相机 / 空注册表 / 阈值非法时：静默不动作，可任意重复调用（幂等、不抛）。
    ///
    /// 数学全部委托 <see cref="WorldOriginMath"/>（纯函数、EditMode 覆盖），本类只管状态与
    /// Transform 平移，所以「先 double 相减再转 float」的铁律只有一份实现。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldOrigin : MonoBehaviour
    {
        /// <summary>默认重定位阈值：4000m（4km）。</summary>
        public const float DefaultRebaseThresholdMeters = 4000f;

        [SerializeField]
        [Tooltip("相机相对原点的距离达到该值（米）时重定位；≤0 视为非法配置，永不重定位。")]
        private float _rebaseThresholdMeters = DefaultRebaseThresholdMeters;

        [SerializeField]
        [Tooltip("是否每帧自检。关闭后只能手动调用 Rebase/ForceRebase（测试/调试用）。")]
        private bool _autoRebase = true;

        private readonly List<Transform> _registered = new List<Transform>();
        private Camera _targetCamera;
        private WorldPosition _originOffset = WorldPosition.Zero;

        /// <summary>重定位阈值（米）。运行时可改。</summary>
        public float RebaseThresholdMeters
        {
            get => _rebaseThresholdMeters;
            set => _rebaseThresholdMeters = value;
        }

        /// <summary>是否每帧自检。</summary>
        public bool AutoRebase
        {
            get => _autoRebase;
            set => _autoRebase = value;
        }

        /// <summary>当前浮点原点（世界坐标，double）。Unity 坐标 = 世界坐标 - 本值。</summary>
        public WorldPosition OriginOffset => _originOffset;

        /// <summary>累计重定位次数（诊断/测试）。</summary>
        public int RebaseCount { get; private set; }

        /// <summary>当前注册的 Transform 数量。</summary>
        public int RegisteredCount => _registered.Count;

        /// <summary>显式指定的相机；为空时退回 <see cref="Camera.main"/>。</summary>
        public Camera TargetCamera
        {
            get => _targetCamera;
            set => _targetCamera = value;
        }

        private void Update()
        {
            if (_autoRebase)
            {
                Rebase();
            }
        }

        /// <summary>登记一个需要随原点平移的物体（重复登记幂等）。</summary>
        public void Register(Transform target)
        {
            if (target == null || _registered.Contains(target))
            {
                return;
            }

            _registered.Add(target);
        }

        /// <summary>取消登记（不存在的项静默忽略）。</summary>
        public void Unregister(Transform target)
        {
            if (target == null)
            {
                return;
            }

            _registered.Remove(target);
        }

        /// <summary>清空注册表。</summary>
        public void ClearRegistered()
        {
            _registered.Clear();
        }

        /// <summary>世界坐标 → 当前原点下的 Unity 坐标。</summary>
        public Vector3 WorldToUnity(WorldPosition world) => WorldOriginMath.ToUnity(world, _originOffset);

        /// <summary>Unity 坐标 → 世界坐标。</summary>
        public WorldPosition UnityToWorld(Vector3 unityPosition) => WorldOriginMath.ToWorld(unityPosition, _originOffset);

        /// <summary>相机当前的世界坐标（double）。无相机返回原点偏移本身。</summary>
        public WorldPosition CameraWorldPosition()
        {
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return _originOffset;
            }

            return UnityToWorld(cam.transform.position);
        }

        /// <summary>
        /// 按阈值判定后重定位。返回是否真的发生了平移。
        /// 无相机 / 未达阈值 / 阈值非法 / 已在原点 → false（可重复调用）。
        /// </summary>
        public bool Rebase()
        {
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return false;
            }

            WorldPosition cameraWorld = UnityToWorld(cam.transform.position);
            if (!WorldOriginMath.ShouldRebase(cameraWorld, _originOffset, _rebaseThresholdMeters))
            {
                return false;
            }

            return ApplyRebase(cam, cameraWorld);
        }

        /// <summary>测试/调试：忽略阈值强制重定位（相机已在原点时仍是安全的 no-op）。</summary>
        public bool ForceRebase()
        {
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return false;
            }

            return ApplyRebase(cam, UnityToWorld(cam.transform.position));
        }

        /// <summary>
        /// 平移执行体：
        ///  delta = ToUnity(cameraWorld, offset)（double 相减后转 float）
        ///  注册物体 position -= delta；相机 position -= delta；offset += delta（double）
        /// 于是注册物体的世界坐标不变、相机到物体的相对位置不变、相机回到原点附近。
        /// </summary>
        private bool ApplyRebase(Camera cam, WorldPosition cameraWorld)
        {
            Vector3 delta = WorldOriginMath.ToUnity(cameraWorld, _originOffset);
            if (delta.x == 0f && delta.y == 0f && delta.z == 0f)
            {
                return false;
            }

            Transform cameraTransform = cam.transform;

            for (int i = _registered.Count - 1; i >= 0; i--)
            {
                Transform target = _registered[i];
                if (target == null)
                {
                    _registered.RemoveAt(i);
                    continue;
                }

                // 相机若被误登记也不做二次平移：它是参照物，位置由下面单独处理。
                if (target == cameraTransform)
                {
                    continue;
                }

                target.position -= delta;
            }

            cameraTransform.position -= delta;
            _originOffset += new WorldPosition(delta.x, delta.y, delta.z);
            RebaseCount++;
            return true;
        }

        private Camera ResolveCamera() => _targetCamera != null ? _targetCamera : Camera.main;
    }
}
