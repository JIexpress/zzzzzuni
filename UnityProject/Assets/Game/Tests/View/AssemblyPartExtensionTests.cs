// AssemblyPartExtensionTests.cs —— 零件扩展 EditMode 用例 (TASK-A-TECH-05)。
// 覆盖：连接点类型匹配矩阵扩展（FuelLine 只接 FuelPort/结构面，异类拒绝）/
// 新零件工厂（结构梁/燃料罐/分离器/燃油管：几何、连接点、质量、组件）/
// 燃料罐消耗质量数（SetFuel/Tick/干质下限/自动熄火）/ Kind 序列化身份。
// 物理行为（质量-加速度、分离冲量）在 PlayMode 用例取证。

using System.Collections.Generic;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.Tests
{
    public sealed class AssemblyPartExtensionTests
    {
        private readonly List<Object> _owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.DestroyImmediate(owned);
                }
            }

            _owned.Clear();
        }

        private T Track<T>(T owned) where T : Object
        {
            _owned.Add(owned);
            return owned;
        }

        [Test]
        public void Compatibility_FuelLineOnlyDocksFuelPort_OrStructural()
        {
            // 专用燃油对：双向可接。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelLine, AttachmentPointType.FuelPort));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelPort, AttachmentPointType.FuelLine));
            // 结构面仍然万能（A-TECH-02 语义不变）。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelLine, AttachmentPointType.Structural));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelPort, AttachmentPointType.Structural));
            // 异类专用接口互斥（类型不匹配拒绝吸附的判据）。
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.FuelLine, AttachmentPointType.ThrusterMount));
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.FuelLine, AttachmentPointType.WheelHub));
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.FuelPort, AttachmentPointType.ThrusterMount));
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.FuelPort, AttachmentPointType.WheelHub));
            // 同类可对接（A-TECH-02 规则：类型匹配即可）——燃油管对接管、
            // 燃油口对接口都是合法耦合。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelLine, AttachmentPointType.FuelLine));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.FuelPort, AttachmentPointType.FuelPort));
            // 既有矩阵不回归。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.Structural, AttachmentPointType.Structural));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.ThrusterMount, AttachmentPointType.ThrusterMount));
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.ThrusterMount, AttachmentPointType.WheelHub));
        }

        [Test]
        public void Factory_CreatesNewPartKinds_WithExpectedLayout()
        {
            AssemblyPart beam = Track(AssemblyPartFactory.CreateStructuralBeam("t-beam", Vector3.zero));
            Assert.AreEqual(AssemblyPartKind.StructuralBeam, beam.Kind);
            Assert.AreEqual(0.8f, beam.Body.mass, 1e-5f);
            Assert.AreEqual(2, beam.Points.Count, "梁两端各一个结构面");
            Assert.AreEqual(new Vector3(0f, 0f, AssemblyPartFactory.BeamHalfLength), beam.Points[0].transform.localPosition);
            Assert.AreEqual(AttachmentPointType.Structural, beam.Points[0].Type);

            AssemblyPart tank = Track(AssemblyPartFactory.CreateFuelTank("t-tank", Vector3.zero));
            Assert.AreEqual(AssemblyPartKind.FuelTank, tank.Kind);
            Assert.AreEqual(3f, tank.Body.mass, 1e-5f, "罐体质量 = 干质 1 + 燃料 2");
            Assert.AreEqual(3, tank.Points.Count, "罐体两端结构面 + 一个燃油口");
            Assert.AreEqual(AttachmentPointType.FuelPort, tank.Points[2].Type, "第三个点必须是燃油口");
            Assert.IsNotNull(tank.GetComponent<AssemblyFuelTank>(), "燃料罐必须挂 AssemblyFuelTank");

            AssemblyPart separator = Track(AssemblyPartFactory.CreateSeparator("t-sep", Vector3.zero));
            Assert.AreEqual(AssemblyPartKind.Separator, separator.Kind);
            AssemblySeparator module = separator.GetComponent<AssemblySeparator>();
            Assert.IsNotNull(module, "分离器必须挂 AssemblySeparator");
            Assert.Greater(module.ImpulseNs, 0f, "默认必须带分离冲量");
            Assert.AreEqual(2, separator.Points.Count);

            AssemblyPart line = Track(AssemblyPartFactory.CreateFuelLine("t-line", Vector3.zero));
            Assert.AreEqual(AssemblyPartKind.FuelLine, line.Kind);
            Assert.AreEqual(2, line.Points.Count);
            Assert.AreEqual(AttachmentPointType.FuelLine, line.Points[0].Type);
            Assert.AreEqual(AttachmentPointType.FuelLine, line.Points[1].Type);
        }

        [Test]
        public void FuelTank_ConsumptionTracksBodyMass_WithDryFloor()
        {
            AssemblyPart tankPart = Track(AssemblyPartFactory.CreateFuelTank("t-tank", Vector3.zero,
                fuelMassKg: 2f, dryMassKg: 1f));
            AssemblyFuelTank tank = tankPart.GetComponent<AssemblyFuelTank>();

            Assert.AreEqual(2f, tank.FuelKg, 1e-5f);
            Assert.AreEqual(3f, tankPart.Body.mass, 1e-5f, "满载质量 = 干质 + 燃料");

            tank.SetFuel(0.8f);
            Assert.AreEqual(1.8f, tankPart.Body.mass, 1e-5f, "SetFuel 必须同步 Rigidbody.mass");

            tank.StartBurn();
            Assert.IsTrue(tank.IsBurning);
            for (int i = 0; i < 10; i++)
            {
                tank.Tick(0.1f); // 1 s @ 0.5 kg/s → 消耗 0.5 kg
            }

            Assert.AreEqual(0.3f, tank.FuelKg, 1e-4f, "1 秒消耗 0.5 kg");
            Assert.AreEqual(1.3f, tankPart.Body.mass, 1e-4f, "body.mass 必须随燃料下降");

            // 烧干自动熄火 + 干质下限。
            tank.SetFuel(0.05f);
            for (int i = 0; i < 10 && tank.IsBurning; i++)
            {
                tank.Tick(0.1f);
            }

            Assert.IsFalse(tank.IsBurning, "烧干必须自动熄火");
            Assert.AreEqual(0f, tank.FuelKg, 1e-4f);
            Assert.AreEqual(1f, tankPart.Body.mass, 1e-4f, "干质是质量下限");
        }

        [Test]
        public void FuelTank_SetFuelClamps_AndStopBurnKeepsFuel()
        {
            AssemblyPart tankPart = Track(AssemblyPartFactory.CreateFuelTank("t-tank", Vector3.zero,
                fuelMassKg: 2f, dryMassKg: 1f));
            AssemblyFuelTank tank = tankPart.GetComponent<AssemblyFuelTank>();

            tank.SetFuel(99f);
            Assert.AreEqual(2f, tank.FuelKg, 1e-5f, "超量程钳到容量");
            tank.SetFuel(-5f);
            Assert.AreEqual(0f, tank.FuelKg, 1e-5f, "负值钳到 0");

            tank.SetFuel(1.5f);
            tank.StartBurn();
            tank.StopBurn();
            tank.Tick(10f);
            Assert.AreEqual(1.5f, tank.FuelKg, 1e-5f, "熄火后燃料保持");
            Assert.AreEqual(2.5f, tankPart.Body.mass, 1e-5f);
        }

        [Test]
        public void PartKind_IsMetadataOnly_AndDefaultsToCube()
        {
            var go = Track(new GameObject("t-part", typeof(Rigidbody)));
            AssemblyPart part = go.AddComponent<AssemblyPart>();
            Assert.AreEqual(AssemblyPartKind.Cube, part.Kind, "未标注零件默认是方块（场景既有零件兼容）");

            part.SetKind(AssemblyPartKind.StructuralBeam);
            Assert.AreEqual(AssemblyPartKind.StructuralBeam, part.Kind);
            // 元数据不改变物理与连接点。
            Assert.AreEqual(0, part.Points.Count);
            Assert.AreEqual(1f, part.Body.mass, 1e-5f);
        }

        [Test]
        public void Separator_DetachWithoutJoints_IsSafeNoOp()
        {
            AssemblyPart sepPart = Track(AssemblyPartFactory.CreateSeparator("t-sep", Vector3.zero));
            AssemblySeparator separator = sepPart.GetComponent<AssemblySeparator>();

            List<AssemblyJoint> severed = separator.Detach();
            Assert.AreEqual(0, severed.Count, "无关节时分离必须是安全空操作");
            Assert.IsNull(sepPart.Points[0].Occupant);
        }
    }
}
