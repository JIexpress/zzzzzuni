// WorldOriginPlayModeTests.cs —— TASK-A-WORLD-01：运行时浮点原点的场景级证据。
// 口径：注册两个物体 → 相机推到 5km → Rebase → 世界坐标不变、Unity 坐标被整体平移、
// 相机回到原点附近且相对距离不变、未注册物体一寸不动；空表/无相机时幂等不抛。
// 用后必须销毁所有临时对象（含临时相机），否则会漏到后续像素/场景用例里。

using System.Collections;
using System.Collections.Generic;
using Game.Domain.World;
using Game.View.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.View.PlayMode.Tests
{
    /// <summary>4km 重定位的运行时行为。</summary>
    public sealed class WorldOriginPlayModeTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private GameObject Track(GameObject go)
        {
            _spawned.Add(go);
            return go;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    Object.Destroy(_spawned[i]);
                }
            }

            _spawned.Clear();
        }

        [UnityTest]
        public IEnumerator Rebase_At5km_ConservesWorldPositions_AndRelativeDistance()
        {
            GameObject cameraGo = Track(new GameObject("~WorldOriginCamera"));
            Camera camera = cameraGo.AddComponent<Camera>();
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(5000f, 100f, 0f);

            GameObject objectA = Track(new GameObject("~WorldOriginA"));
            objectA.transform.position = new Vector3(5003f, 100f, 2f);

            GameObject objectB = Track(new GameObject("~WorldOriginB"));
            objectB.transform.position = new Vector3(5020f, 100f, 0f);

            GameObject bystander = Track(new GameObject("~WorldOriginUnregistered"));
            bystander.transform.position = new Vector3(0f, 0f, 0f);

            var host = Track(new GameObject("~WorldOriginHost"));
            var origin = host.AddComponent<WorldOrigin>();
            origin.AutoRebase = false;
            origin.TargetCamera = camera;

            origin.Register(objectA.transform);
            origin.Register(objectB.transform);
            origin.Register(objectA.transform); // 重复登记必须幂等
            Assert.AreEqual(2, origin.RegisteredCount, "重复登记不得重复计数");
            Assert.Less(origin.WorldToUnity(origin.OriginOffset).magnitude, 1e-6f, "初始原点必须为零");

            WorldPosition worldA0 = origin.UnityToWorld(objectA.transform.position);
            WorldPosition worldB0 = origin.UnityToWorld(objectB.transform.position);
            WorldPosition cameraWorld0 = origin.CameraWorldPosition();
            float distanceToA0 = Vector3.Distance(cameraGo.transform.position, objectA.transform.position);
            float distanceToB0 = Vector3.Distance(cameraGo.transform.position, objectB.transform.position);
            Vector3 bystander0 = bystander.transform.position;

            yield return null;

            Assert.IsTrue(origin.Rebase(), "5km 超过 4km 阈值，必须触发重定位");
            Assert.AreEqual(1, origin.RebaseCount, "一次 Rebase 只能记一次");

            // V3：注册物体的世界坐标守恒。
            WorldPosition worldA1 = origin.UnityToWorld(objectA.transform.position);
            WorldPosition worldB1 = origin.UnityToWorld(objectB.transform.position);
            Assert.Less(WorldPosition.Distance(worldA0, worldA1), 1e-3, "A 的世界坐标必须守恒");
            Assert.Less(WorldPosition.Distance(worldB0, worldB1), 1e-3, "B 的世界坐标必须守恒");

            // V4：相机到物体的相对距离守恒，相机回到原点附近。
            Assert.AreEqual(distanceToA0, Vector3.Distance(cameraGo.transform.position, objectA.transform.position), 1e-3f,
                "相机→A 的距离必须不变");
            Assert.AreEqual(distanceToB0, Vector3.Distance(cameraGo.transform.position, objectB.transform.position), 1e-3f,
                "相机→B 的距离必须不变");
            Assert.Less(cameraGo.transform.position.magnitude, 0.01f, "重定位后相机必须归零附近");

            // Unity 坐标被整体平移 5000/100/0：A 3m、B 20m。
            Assert.AreEqual(3f, objectA.transform.position.x, 1e-2f);
            Assert.AreEqual(0f, objectA.transform.position.y, 1e-2f);
            Assert.AreEqual(2f, objectA.transform.position.z, 1e-2f);
            Assert.AreEqual(20f, objectB.transform.position.x, 1e-2f);

            // 相机世界坐标不变；原点落在相机原位置。
            WorldPosition cameraWorld1 = origin.CameraWorldPosition();
            Assert.Less(WorldPosition.Distance(cameraWorld0, cameraWorld1), 1e-3, "cameraWorld 必须不变");
            Assert.Less(WorldPosition.Distance(origin.OriginOffset, new WorldPosition(5000.0, 100.0, 0.0)), 1e-3,
                "OriginOffset 必须等于相机原世界坐标");

            // V5：未注册物体绝不平移。
            Assert.AreEqual(bystander0, bystander.transform.position, "未注册物体不得被平移");

            // 再次调用：已回到原点，必须幂等。
            Assert.IsFalse(origin.Rebase(), "未达阈值时 Rebase 必须返回 false");
            Assert.AreEqual(1, origin.RebaseCount, "幂等调用不得增加计数");
            Assert.AreEqual(bystander0, bystander.transform.position, "幂等调用也不得动未注册物体");

            yield return null;

            // 反向互转：Unity → 世界 → Unity 仍一致。
            Vector3 back = origin.WorldToUnity(origin.UnityToWorld(objectA.transform.position));
            Assert.Less((back - objectA.transform.position).magnitude, 1e-3f, "WorldToUnity/UnityToWorld 必须自洽");
        }

        [UnityTest]
        public IEnumerator AutoRebase_TicksByItself_OverThreshold()
        {
            GameObject cameraGo = Track(new GameObject("~WorldOriginAutoCamera"));
            Camera camera = cameraGo.AddComponent<Camera>();
            cameraGo.transform.position = new Vector3(4200f, 0f, 0f);

            GameObject target = Track(new GameObject("~WorldOriginAutoTarget"));
            target.transform.position = new Vector3(4205f, 0f, 0f);

            var host = Track(new GameObject("~WorldOriginAutoHost"));
            var origin = host.AddComponent<WorldOrigin>();
            Assert.IsTrue(origin.AutoRebase, "默认必须每帧自检");
            origin.TargetCamera = camera;
            origin.Register(target.transform);

            WorldPosition worldBefore = origin.UnityToWorld(target.transform.position);

            yield return null;
            yield return null;

            Assert.AreEqual(1, origin.RebaseCount, "自动模式必须在超过 4km 后重定位一次");
            Assert.Less(cameraGo.transform.position.magnitude, 0.01f, "相机必须被拉回原点");
            Assert.AreEqual(5f, target.transform.position.x, 1e-2f, "物体 Unity 坐标必须整体平移");
            Assert.Less(WorldPosition.Distance(worldBefore, origin.UnityToWorld(target.transform.position)), 1e-3,
                "自动重定位同样必须守恒世界坐标");
        }

        [UnityTest]
        public IEnumerator Rebase_WithoutCameraOrRegistry_IsIdempotentAndSilent()
        {
            GameObject target = Track(new GameObject("~WorldOriginLonely"));
            target.transform.position = new Vector3(17f, 0f, -4f);
            Vector3 target0 = target.transform.position;

            var host = Track(new GameObject("~WorldOriginEmptyHost"));
            var origin = host.AddComponent<WorldOrigin>();
            origin.AutoRebase = false;

            // 情形一：空注册表 + 相机就在原点 → 不动作、不记账。
            GameObject cameraGo = Track(new GameObject("~WorldOriginEmptyCamera"));
            Camera emptyCamera = cameraGo.AddComponent<Camera>();
            cameraGo.transform.position = Vector3.zero;
            origin.TargetCamera = emptyCamera;

            Assert.AreEqual(0, origin.RegisteredCount, "此时必须是空表");
            Assert.IsFalse(origin.Rebase(), "空表 + 无关卡偏移时 Rebase 必须返回 false");
            Assert.IsFalse(origin.ForceRebase(), "相机已在原点时 ForceRebase 必须安全 no-op");
            Assert.AreEqual(0, origin.RebaseCount, "空跑不得记账");
            Assert.AreEqual(WorldPosition.Zero, origin.OriginOffset, "空跑不得移动原点");
            Assert.AreEqual(target0, target.transform.position, "空跑不得平移任何物体");

            // 情形二：无相机（临时关掉场景里所有相机，让 Camera.main 也解析不到）。
            var disabled = new List<Camera>();
            Camera[] cameras = Object.FindObjectsOfType<Camera>();
            foreach (Camera cam in cameras)
            {
                if (cam.enabled)
                {
                    cam.enabled = false;
                    disabled.Add(cam);
                }
            }

            try
            {
                yield return null;
                yield return null;

                Assert.IsNull(Camera.main, "前置条件：本用例里必须解析不到主相机");
                origin.TargetCamera = null;
                origin.Register(target.transform);
                Assert.AreEqual(1, origin.RegisteredCount);

                Assert.IsFalse(origin.Rebase(), "无相机时 Rebase 必须返回 false");
                Assert.IsFalse(origin.ForceRebase(), "无相机时 ForceRebase 必须返回 false");
                Assert.AreEqual(0, origin.RebaseCount, "无相机时不得记账");
                Assert.AreEqual(WorldPosition.Zero, origin.OriginOffset, "无相机时原点不得移动");
                Assert.AreEqual(target0, target.transform.position, "无相机时已注册物体也不得被平移");

                // 可重复调用（幂等、不抛）。
                Assert.IsFalse(origin.Rebase());
                Assert.IsFalse(origin.Rebase());
                Assert.AreEqual(0, origin.RebaseCount);
            }
            finally
            {
                for (int i = 0; i < disabled.Count; i++)
                {
                    if (disabled[i] != null)
                    {
                        disabled[i].enabled = true;
                    }
                }
            }
        }
    }
}
