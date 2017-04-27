using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LisconVT.Domain.Models;
using System.Collections.Generic;
using LisconVT.Domain.Helpers;
using System.Text;

namespace LisconVT.Domain.Tests
{
    [TestClass]
    public class MDVRTests
    {
        [TestMethod]
        public void GetString()
        {
            var bytes = new byte[] { 0x00, 0x0f, 0x01, 0x0f, 0x02, 0x0f };
            bytes = MdvrMessageHelper.Escape(bytes);
            string messageString = MdvrMessageHelper.GetString(bytes);
            Assert.AreEqual(",#\u000f", messageString, true);
        }

        [TestMethod]
        public void GetBytes()
        {
            string messageString = ",#\u000f";
            var bytes = MdvrMessageHelper.GetBytes(messageString);
            bytes = MdvrMessageHelper.Escape(bytes, false);
            var expected = new byte[] { 0x00, 0x0f, 0x01, 0x0f, 0x02, 0x0f };
            CollectionAssert.AreEqual(expected, bytes);
        }
    }
}
