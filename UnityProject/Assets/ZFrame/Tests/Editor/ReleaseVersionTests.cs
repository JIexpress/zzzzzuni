// =============================================================================
// ReleaseVersionTests.cs —— 发布元数据（version / build / versionCode）单测（TASK-A-AND-07）
//
// 覆盖三件事：
//   ① 注入值正确：本次构建的 versionName = "<version>.<build+1>"，versionCode 独立取值；
//   ② 自增写回正确：构建成功后 build 与 versionCode 各自 +1、互不派生、格式/编码合规；
//   ③ 单调性守卫有牙：versionCode <= 1（含漏字段的 0）必须抛错——改回"静默继续"就变红。
// 另加一条守门测试：仓库里真实的 Tools/version.json 不得再次倒挂（> 旧 MonoDev 0.1.0.22）。
// =============================================================================
using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Editor;

namespace ZFrame.Editor.Tests
{
    public sealed class ReleaseVersionTests
    {
        private const string SampleJson =
            "{\n  \"version\": \"0.1.0\",\n  \"build\": 23,\n  \"versionCode\": 2\n}\n";

        [Test]
        public void Parse_ReadsVersionBuildAndVersionCode()
        {
            ReleaseVersion v = ReleaseVersion.Parse(SampleJson);
            Assert.AreEqual("0.1.0", v.Version);
            Assert.AreEqual(23, v.Build);
            Assert.AreEqual(2, v.VersionCode);
        }

        [Test]
        public void Parse_MissingVersionCode_BecomesZero_AndGuardRejectsIt()
        {
            // 漏字段 = 0：守卫必须拒绝（不是静默当作 1 继续构建）。
            ReleaseVersion v = ReleaseVersion.Parse("{\"version\": \"0.1.0\", \"build\": 23}");
            Assert.AreEqual(0, v.VersionCode);
            var ex = Assert.Throws<InvalidOperationException>(() => v.ValidateForBuild());
            StringAssert.Contains("versionCode", ex.Message);
        }

        [Test]
        public void Parse_RejectsMissingVersionOrNegativeBuild()
        {
            Assert.Throws<FormatException>(() => ReleaseVersion.Parse("{\"build\": 23, \"versionCode\": 2}"));
            Assert.Throws<FormatException>(() => ReleaseVersion.Parse("{\"version\": \"0.1.0\", \"build\": -1, \"versionCode\": 2}"));
            Assert.Throws<FormatException>(() => ReleaseVersion.Parse(string.Empty));
        }

        [Test]
        public void BundleVersion_UsesNextBuildNumber()
        {
            // 注入值语义与历史一致：落盘 build = 已消耗号，本次占用 build+1。
            Assert.AreEqual("0.1.0.24", ReleaseVersion.Parse(SampleJson).BundleVersion);
            Assert.AreEqual(
                "0.1.0.23",
                ReleaseVersion.Parse("{\"version\": \"0.1.0\", \"build\": 22, \"versionCode\": 2}").BundleVersion);
        }

        [Test]
        public void Advance_IncrementsBuildAndVersionCodeIndependently()
        {
            ReleaseVersion next = ReleaseVersion.Parse(SampleJson).Advance();
            Assert.AreEqual("0.1.0", next.Version, "version 主段不参与自增");
            Assert.AreEqual(24, next.Build);
            Assert.AreEqual(3, next.VersionCode);

            // versionCode 不从 build 派生：build 很大时 versionCode 仍按自己的步长走。
            ReleaseVersion v = ReleaseVersion.Parse("{\"version\": \"0.1.0\", \"build\": 100, \"versionCode\": 2}");
            Assert.AreEqual(3, v.Advance().VersionCode);
        }

        [TestCase(1)]
        [TestCase(0)]
        [TestCase(-5)]
        public void ValidateForBuild_RejectsRegressedVersionCode(int versionCode)
        {
            ReleaseVersion v = new ReleaseVersion("0.1.0", 23, versionCode);
            var ex = Assert.Throws<InvalidOperationException>(() => v.ValidateForBuild());
            StringAssert.Contains("versionCode", ex.Message);
            StringAssert.Contains("Tools/version.json", ex.Message);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(1000)]
        public void ValidateForBuild_AcceptsVersionCodeFromTwo(int versionCode)
        {
            Assert.DoesNotThrow(() => new ReleaseVersion("0.1.0", 23, versionCode).ValidateForBuild());
        }

        [Test]
        public void Save_WritesUtf8NoBomLf_AndRoundTrips()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "zframe_release_version_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ReleaseVersion.Parse(SampleJson).Advance().Save(path);

                byte[] bytes = File.ReadAllBytes(path);
                Assert.IsFalse(
                    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    "version.json 不得带 BOM");
                string text = new UTF8Encoding(false).GetString(bytes);
                Assert.IsFalse(text.Contains("\r"), "version.json 必须是 LF");

                ReleaseVersion reloaded = ReleaseVersion.Load(path);
                Assert.AreEqual("0.1.0", reloaded.Version);
                Assert.AreEqual(24, reloaded.Build);
                Assert.AreEqual(3, reloaded.VersionCode);
                StringAssert.Contains("\"versionCode\": 3", text);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void RepoVersionJson_PassesGuard_AndNextVersionNameBeatsMonoDev22()
        {
            string path = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "Tools", "version.json"));
            ReleaseVersion v = ReleaseVersion.Load(path);

            Assert.DoesNotThrow(() => v.ValidateForBuild(), "仓库 version.json 的 versionCode 不得倒退");
            Assert.GreaterOrEqual(v.VersionCode, 2, "已发布包用过 versionCode 1，下一个合法值 >= 2");
            Assert.GreaterOrEqual(v.Build + 1, 23, "下次 versionName 必须超过旧 MonoDev 包 0.1.0.22（防倒挂）");
        }
    }
}
