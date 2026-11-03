using NUnit.Framework;
using UnityEngine;
using ZFrame.Debug.Tests.Support;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// MVP assembly registry (TASK-A-M3-01): bindings are registered explicitly
    /// (static dictionary + factory delegates) — no reflection over assemblies.
    /// </summary>
    public sealed class UIBindingTests
    {
        [SetUp]
        public void SetUp()
        {
            UIBinding.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            UIBinding.Reset();
        }

        [Test]
        public void Register_ThenTryGet_ReturnsFactory()
        {
            UIBinding.Register<FakeStatusView>(view => new FakeStatusPresenter((FakeStatusView)view));

            Assert.IsTrue(UIBinding.TryGetFactory(typeof(FakeStatusView), out var factory));
            Assert.IsNotNull(factory(new FakeStatusView()));
        }

        [Test]
        public void TryGetFactory_UnknownView_ReturnsFalse()
        {
            Assert.IsFalse(UIBinding.TryGetFactory(typeof(FakeAltView), out _));
        }

        [Test]
        public void EnsureDefaults_IsIdempotent_AndRegistersSamplePanel()
        {
            UIBinding.EnsureDefaults();
            int countAfterDefaults = UIBinding.Count;
            UIBinding.EnsureDefaults();

            Assert.AreEqual(countAfterDefaults, UIBinding.Count, "defaults must install exactly once");
            Assert.IsTrue(UIBinding.TryGetFactory(typeof(TestPanelView), out var factory));
            var view = new GameObject("binding_probe", typeof(RectTransform)).AddComponent<TestPanelView>();
            try
            {
                Assert.IsInstanceOf<TestPanelPresenter>(factory(view));
            }
            finally
            {
                Object.DestroyImmediate(view.gameObject);
            }
        }
    }
}
