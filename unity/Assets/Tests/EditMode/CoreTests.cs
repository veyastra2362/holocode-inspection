using System;
// Unity Test Framework（EditMode）单元测试。
//
// 运行方式：Unity Editor → Window → General → Test Runner → EditMode → Run All
// 覆盖的是最容易写错、且不需要真实场景也能验证的部分：
//   1. 后端响应信封 / 顶层数组的解析（JsonUtility 的限制 + 自定义扫描）
//   2. 枚举与线缆字符串的互转
//   3. BaseUrl 规范化
//   4. 必需程序集标记
using NUnit.Framework;
using UnityEngine;

namespace Inspection.Tests
{
    public class JsonScanTests
    {
        [Test]
        public void ExtractsStringValue()
        {
            Assert.AreEqual("\"hello\"", JsonScan.ValueOf("{\"a\":\"hello\"}", "a"));
        }

        [Test]
        public void ExtractsObjectValue()
        {
            var json = "{\"ok\":true,\"data\":{\"x\":1,\"y\":2}}";
            Assert.AreEqual("{\"x\":1,\"y\":2}", JsonScan.ValueOf(json, "data"));
        }

        [Test]
        public void ExtractsArrayValue()
        {
            var json = "{\"ok\":true,\"data\":[{\"id\":\"1\"},{\"id\":\"2\"}],\"total\":2}";
            Assert.AreEqual("[{\"id\":\"1\"},{\"id\":\"2\"}]", JsonScan.ValueOf(json, "data"));
        }

        [Test]
        public void IgnoresNestedKeyWithSameName()
        {
            // 关键用例：data 里也有一个 "data" 字段，绝不能取到内层的
            var json = "{\"ok\":true,\"data\":{\"data\":\"inner\",\"id\":\"abc\"}}";
            Assert.AreEqual("{\"data\":\"inner\",\"id\":\"abc\"}", JsonScan.ValueOf(json, "data"));
        }

        [Test]
        public void HandlesBracesInsideStrings()
        {
            // 标题里带花括号和转义引号时，不能被误判为结构结束
            var json = "{\"ok\":true,\"data\":{\"title\":\"机柜 {A3} \\\"异常\\\"\"},\"total\":1}";
            var data = JsonScan.ValueOf(json, "data");
            Assert.AreEqual("{\"title\":\"机柜 {A3} \\\"异常\\\"\"}", data);
        }

        [Test]
        public void ReturnsNullForMissingKey()
        {
            Assert.IsNull(JsonScan.ValueOf("{\"ok\":true}", "data"));
            Assert.IsNull(JsonScan.ValueOf(null, "data"));
            Assert.IsNull(JsonScan.ValueOf("{\"ok\":true}", null));
        }

        [Test]
        public void HandlesWhitespaceAroundColon()
        {
            Assert.AreEqual("1.5", JsonScan.ValueOf("{\"data\"  :   1.5 }", "data"));
        }

        [Test]
        public void UnclosedBracketReturnsNull()
        {
            Assert.IsNull(JsonScan.ValueOf("{\"data\":[1,2", "data"));
        }
    }

    public class EnvelopeTests
    {
        [Test]
        public void ParsesSuccessEnvelope()
        {
            var env = Envelope.Parse("{\"ok\":true,\"data\":{\"id\":\"iss_1\",\"title\":\"t\"}");
            Assert.IsNotNull(env);
            Assert.IsTrue(env.Ok);
            Assert.AreEqual("{\"id\":\"iss_1\",\"title\":\"t\"}", env.DataJson);
        }

        [Test]
        public void ParsesErrorEnvelope()
        {
            var env = Envelope.Parse("{\"ok\":false,\"code\":\"BAD_REQUEST\",\"error\":\"title is required\"}");
            Assert.IsNotNull(env);
            Assert.IsFalse(env.Ok);
            Assert.AreEqual("BAD_REQUEST", env.Code);
            var failure = env.ToFailure<IssueData>(400);
            Assert.IsFalse(failure.Success);
            Assert.AreEqual("title is required", failure.ErrorMessage);
            Assert.AreEqual(400, failure.HttpStatus);
        }

        [Test]
        public void NullOrGarbageReturnsNull()
        {
            Assert.IsNull(Envelope.Parse(null));
            Assert.IsNull(Envelope.Parse(""));
            Assert.IsNull(Envelope.Parse("<html>502</html>"));
        }
    }

    public class ParseArrayTests
    {
        [Test]
        public void ParsesIssueArray()
        {
            var body = "{\"ok\":true,\"data\":[" +
                       "{\"id\":\"a\",\"title\":\"t1\",\"description\":\"\",\"priority\":\"high\",\"status\":\"open\",\"position\":{\"x\":1,\"y\":2,\"z\":3},\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\"}," +
                       "{\"id\":\"b\",\"title\":\"t2\",\"description\":\"d\",\"priority\":\"low\",\"status\":\"resolved\",\"position\":{\"x\":0,\"y\":0,\"z\":0},\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\"}" +
                       "],\"total\":2}";
            var env = Envelope.Parse(body);
            var result = IssueApi.ParseArray(env);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(2, result.Data.Length);
            Assert.AreEqual("a", result.Data[0].id);
            Assert.AreEqual(1f, result.Data[0].position.x);
            Assert.AreEqual("resolved", result.Data[1].status);
        }

        [Test]
        public void EmptyArrayIsOk()
        {
            var env = Envelope.Parse("{\"ok\":true,\"data\":[],\"total\":0}");
            var result = IssueApi.ParseArray(env);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.Data.Length);
        }

        [Test]
        public void NonArrayDataFails()
        {
            var env = Envelope.Parse("{\"ok\":true,\"data\":{\"id\":\"a\"}}");
            var result = IssueApi.ParseArray(env);
            Assert.IsFalse(result.Success);
        }

        [Test]
        public void ParseIssueRequiresId()
        {
            var ok = IssueApi.ParseIssue("{\"id\":\"x\",\"title\":\"t\"}", 200);
            Assert.IsTrue(ok.Success);
            Assert.AreEqual("x", ok.Data.id);

            var bad = IssueApi.ParseIssue("{\"title\":\"t\"}", 200);
            Assert.IsFalse(bad.Success);
        }
    }

    public class JsonStringTests
    {
        [Test]
        public void UnescapesQuotesAndBackslashes()
        {
            Assert.AreEqual("机柜 \"A3\" 异常", JsonScan.AsString("\"机柜 \\\"A3\\\" 异常\""));
            Assert.AreEqual("a\\b", JsonScan.AsString("\"a\\\\b\""));
        }

        [Test]
        public void UnescapesNewlineAndUnicode()
        {
            Assert.AreEqual("第一行\n第二行", JsonScan.AsString("\"第一行\\n第二行\""));
            Assert.AreEqual("A", JsonScan.AsString("\"\\u0041\""));
        }

        [Test]
        public void ReturnsInputWhenNotQuoted()
        {
            Assert.AreEqual("123", JsonScan.AsString("123"));
            Assert.AreEqual("true", JsonScan.AsString("true"));
            Assert.IsNull(JsonScan.AsString(null));
        }

        [Test]
        public void FindStringEndSkipsEscapedQuotes()
        {
            var s = "\"a\\\"b\"";
            Assert.AreEqual(s.Length - 1, JsonScan.FindStringEnd(s, 0));
        }
    }

    public class IsoTimeTests
    {
        [Test]
        public void ParsesUtcWithFraction()
        {
            Assert.IsTrue(IsoTime.TryParse("2026-10-26T07:11:03.0900071Z", out var utc));
            Assert.AreEqual(2026, utc.Year);
            Assert.AreEqual(7, utc.Hour);
            Assert.AreEqual(90, (int)(utc.Millisecond));
        }

        [Test]
        public void ParsesUtcWithoutFraction()
        {
            Assert.IsTrue(IsoTime.TryParse("2026-10-26T07:11:03Z", out var utc));
            Assert.AreEqual(DateTimeKind.Utc, utc.Kind);
            Assert.AreEqual(3, utc.Second);
        }

        [Test]
        public void HandlesPositiveOffset()
        {
            // 19:11:03+08:00 == 11:11:03Z
            Assert.IsTrue(IsoTime.TryParse("2026-10-26T19:11:03+08:00", out var utc));
            Assert.AreEqual(11, utc.Hour);
        }

        [Test]
        public void GarbageFallsBackGracefully()
        {
            Assert.IsFalse(IsoTime.TryParse("not-a-date", out _));
            Assert.IsFalse(IsoTime.TryParse("", out _));
            Assert.IsFalse(IsoTime.TryParse(null, out _));
            Assert.AreEqual("—", IsoTime.ToLocalDisplay(null));
        }
    }

    public class EnumTests
    {
        [Test]
        public void PriorityWireFormatMatchesBackend()
        {
            Assert.AreEqual("low", IssueEnums.ToWire(IssuePriority.Low));
            Assert.AreEqual("medium", IssueEnums.ToWire(IssuePriority.Medium));
            Assert.AreEqual("high", IssueEnums.ToWire(IssuePriority.High));
        }

        [Test]
        public void StatusWireFormatMatchesBackend()
        {
            Assert.AreEqual("open", IssueEnums.ToWire(IssueStatus.Open));
            Assert.AreEqual("in_progress", IssueEnums.ToWire(IssueStatus.InProgress));
            Assert.AreEqual("resolved", IssueEnums.ToWire(IssueStatus.Resolved));
        }

        [Test]
        public void ParsesCaseInsensitively()
        {
            Assert.IsTrue(IssueEnums.TryParsePriority(" HIGH ", out var p));
            Assert.AreEqual(IssuePriority.High, p);
            Assert.IsFalse(IssueEnums.TryParsePriority("urgent", out _));
            Assert.IsTrue(IssueEnums.TryParseStatus("in_progress", out var s));
            Assert.AreEqual(IssueStatus.InProgress, s);
            Assert.IsFalse(IssueEnums.TryParseStatus("done", out _));
        }

        [Test]
        public void PositionDataRoundTrips()
        {
            var d = new PositionData(new Vector3(1.25f, 0.8f, 2.4f));
            CollectionAssert.AreEqual(new[] { 1.25f, 0.8f, 2.4f }, new[] { d.x, d.y, d.z });
            var v = d.ToVector3();
            Assert.AreEqual(2.4f, v.z, 1e-5f);
            Assert.AreEqual("(1.25, 0.80, 2.40)", d.ToString());
        }
    }

    public class BaseUrlTests
    {
        [TestCase(null, "http://localhost:8080")]
        [TestCase("", "http://localhost:8080")]
        [TestCase("   ", "http://localhost:8080")]
        [TestCase("192.168.1.10:8080", "http://192.168.1.10:8080")]
        [TestCase("http://localhost:8080/", "http://localhost:8080")]
        [TestCase(" https://api.example.com// ", "https://api.example.com")]
        public void Normalizes(string input, string expected)
        {
            Assert.AreEqual(expected, IssueApi.NormalizeBaseUrl(input));
        }
    }

    public class SceneBoundsTests
    {
        [Test]
        public void AcceptsCoordinatesInsideRoom()
        {
            Assert.IsTrue(IssuePlacementController.IsInsideScene(new Vector3(0, 1, 0)));
            Assert.IsTrue(IssuePlacementController.IsInsideScene(new Vector3(-8, 3.5f, 10)));
        }

        [Test]
        public void RejectsFarAwayCoordinates()
        {
            // 历史数据来自 AR 真实世界坐标（很大）时，不应在场景里凭空生成标记
            Assert.IsFalse(IssuePlacementController.IsInsideScene(new Vector3(500, 1, 0)));
            Assert.IsFalse(IssuePlacementController.IsInsideScene(new Vector3(0, -99, 0)));
        }
    }
}


