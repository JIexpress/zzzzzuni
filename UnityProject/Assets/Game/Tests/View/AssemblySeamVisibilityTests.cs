// AssemblySeamVisibilityTests.cs —— TASK-B-R10-01：产品程序集里的测试接缝必须**不是 public**。
//
// 为什么要有这条用例：把接缝从 public 收成 internal 之后，下一次有人图方便再改回 public
// （"测试调不到就放开"），代码照样能编过、功能照样对，只有交付包悄悄变脏。
// 这里把"接缝不得是公开 API"变成一条会红的守卫：IsPublic == true 即失败。
//
// 与 IVT 的分工：本用例**能调用**接缝，靠的是 Game/View/AssemblyInfo.cs 的 InternalsVisibleTo；
// 如果哪天 IVT 写错，编译期就会红（比运行时断言更早）。

using System;
using System.Reflection;
using Game.View.UI;
using NUnit.Framework;
using ZFrame.View;

namespace Game.View.Tests
{
    public sealed class AssemblySeamVisibilityTests
    {
        private const BindingFlags AnyMethod = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static void AssertInternal(Type owner, string methodName, bool isStatic)
        {
            MethodInfo m = owner.GetMethod(methodName, AnyMethod);
            Assert.IsNotNull(m, owner.FullName + "." + methodName + " 必须仍然存在（接缝被删也要显式改本用例）");
            Assert.IsFalse(m.IsPublic, owner.FullName + "." + methodName + " 不得是 public（交付包里不该有测试 API）");
            Assert.IsTrue(m.IsAssembly, owner.FullName + "." + methodName + " 应为 internal（测试靠 InternalsVisibleTo 访问）");
            Assert.AreEqual(isStatic, m.IsStatic, owner.FullName + "." + methodName + " 的静态性被改了");
        }

        [Test]
        public void UiStaticResetSeams_AreInternal()
        {
            AssertInternal(typeof(UIRoot), "ResetForTests", true);
            AssertInternal(typeof(UIManager), "ResetForTests", true);
            AssertInternal(typeof(SettingsEntry), "ResetForTests", true);
        }

        [Test]
        public void PlayModeClickSeams_AreInternal()
        {
            AssertInternal(typeof(MainMenuView), "ReplayPrimaryForTests", false);
            AssertInternal(typeof(PausePanelView), "InvokeResumeForTests", false);
        }
    }
}
