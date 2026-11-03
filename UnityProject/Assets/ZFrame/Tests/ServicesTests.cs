using System;
using NUnit.Framework;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>EditMode tests for <see cref="Services"/>.</summary>
    public class ServicesTests
    {
        private interface IGreeter
        {
            string Greet();
        }

        private sealed class FormalGreeter : IGreeter
        {
            public string Greet() => "good day";
        }

        private sealed class CasualGreeter : IGreeter
        {
            public string Greet() => "yo";
        }

        [TearDown]
        public void TearDown()
        {
            Services.Clear();
        }

        [Test]
        public void Register_Then_Get_ReturnsSameInstance()
        {
            var greeter = new FormalGreeter();
            Services.Register<IGreeter>(greeter);

            Assert.AreSame(greeter, Services.Get<IGreeter>());
        }

        [Test]
        public void Register_ConcreteType_Resolves()
        {
            var greeter = new CasualGreeter();
            Services.Register(greeter);

            Assert.AreSame(greeter, Services.Get<CasualGreeter>());
        }

        [Test]
        public void Register_Duplicate_Throws()
        {
            Services.Register<IGreeter>(new FormalGreeter());

            Assert.Throws<InvalidOperationException>(
                () => Services.Register<IGreeter>(new CasualGreeter()),
                "duplicate registration must throw InvalidOperationException");
        }

        [Test]
        public void Get_Unregistered_ThrowsWithRegisteredList()
        {
            Services.Register<IGreeter>(new FormalGreeter());

            var ex = Assert.Throws<InvalidOperationException>(() => Services.Get<CasualGreeter>());
            StringAssert.Contains("ZFrame.Core.Tests.ServicesTests+IGreeter", ex.Message);
            StringAssert.Contains("CasualGreeter", ex.Message);
        }

        [Test]
        public void TryGet_ReturnsTrueAndValue_WhenRegistered()
        {
            var greeter = new FormalGreeter();
            Services.Register<IGreeter>(greeter);

            bool ok = Services.TryGet(out IGreeter resolved);

            Assert.IsTrue(ok);
            Assert.AreSame(greeter, resolved);
        }

        [Test]
        public void TryGet_ReturnsFalse_WhenUnregistered()
        {
            Assert.IsFalse(Services.TryGet(out IGreeter _));
        }

        [Test]
        public void Unregister_RemovesOnlyThatService()
        {
            Services.Register<IGreeter>(new FormalGreeter());
            Services.Register(new CasualGreeter());

            Services.Unregister<IGreeter>();

            Assert.IsFalse(Services.TryGet(out IGreeter _));
            Assert.IsTrue(Services.TryGet(out CasualGreeter _));
        }

        [Test]
        public void Unregister_Unregistered_IsNoOp()
        {
            Assert.DoesNotThrow(() => Services.Unregister<IGreeter>());
        }

        [Test]
        public void Clear_RemovesEverything()
        {
            Services.Register<IGreeter>(new FormalGreeter());
            Services.Register(new CasualGreeter());

            Services.Clear();

            Assert.IsFalse(Services.TryGet(out IGreeter _));
            Assert.IsFalse(Services.TryGet(out CasualGreeter _));
            CollectionAssert.IsEmpty(Services.GetAllRegistered());
        }

        [Test]
        public void GetAllRegistered_ListsRegisteredTypeNames()
        {
            Services.Register<IGreeter>(new FormalGreeter());

            CollectionAssert.AreEquivalent(
                new[] { "ZFrame.Core.Tests.ServicesTests+IGreeter" },
                Services.GetAllRegistered());
        }
    }
}
