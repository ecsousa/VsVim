﻿using System.Windows.Input;
using NUnit.Framework;
using Vim.UI.Wpf.Implementation;

namespace Vim.UI.Wpf.UnitTest
{
    [TestFixture]
    public class KeyboardDeviceImplTest
    {
        private KeyboardDeviceImpl _deviceImpl;

        [SetUp]
        public void SetUp()
        {
            _deviceImpl = new KeyboardDeviceImpl();
        }

        /// <summary>
        /// Don't throw on the None case
        /// </summary>
        [Test]
        public void IsKeyDown1()
        {
            Assert.IsFalse(_deviceImpl.IsKeyDown(Key.None));
        }
    }
}
