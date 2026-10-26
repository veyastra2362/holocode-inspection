using System;
using UnityEngine;

namespace Inspection
{
    /// <summary>
    /// 问题优先级。字符串值与 Go 后端、React 管理端完全一致。
    /// </summary>
    public enum IssuePriority
    {
        Low,
        Medium,
        High
    }

    /// <summary>问题处理状态。</summary>
    public enum IssueStatus
    {
        Open,
        InProgress,
        Resolved
    }

    /// <summary>
    /// 枚举与线上字符串的转换。集中在一处，避免三端出现 "InProgress" / "in_progress" 不一致。
    /// </summary>
    public static class IssueEnums
    {
        public static string ToWire(IssuePriority p)
        {
            switch (p)
            {
                case IssuePriority.Low: return "low";
                case IssuePriority.Medium: return "medium";
                case IssuePriority.High: return "high";
                default: throw new ArgumentOutOfRangeException(nameof(p), p, null);
            }
        }

        public static string ToWire(IssueStatus s)
        {
            switch (s)
            {
                case IssueStatus.Open: return "open";
                case IssueStatus.InProgress: return "in_progress";
                case IssueStatus.Resolved: return "resolved";
                default: throw new ArgumentOutOfRangeException(nameof(s), s, null);
            }
        }

        public static bool TryParsePriority(string value, out IssuePriority priority)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "low": priority = IssuePriority.Low; return true;
                case "medium": priority = IssuePriority.Medium; return true;
                case "high": priority = IssuePriority.High; return true;
                default: priority = IssuePriority.Medium; return false;
            }
        }

        public static bool TryParseStatus(string value, out IssueStatus status)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "open": status = IssueStatus.Open; return true;
                case "in_progress": status = IssueStatus.InProgress; return true;
                case "resolved": status = IssueStatus.Resolved; return true;
                default: status = IssueStatus.Open; return false;
            }
        }
    }

    /// <summary>
    /// 一条巡检问题。
    /// JsonUtility 使用字段名做序列化，因此字段名严格对应后端 JSON：
    /// id / title / description / priority / status / position{x,y,z} / createdAt / updatedAt。
    /// </summary>
    [Serializable]
    public class IssueData
    {
        public string id;
        public string title;
        public string description;
        /// <summary>"low" / "medium" / "high"</summary>
        public string priority = "medium";
        /// <summary>"open" / "in_progress" / "resolved"</summary>
        public string status = "open";
        public PositionData position = new PositionData();
        /// <summary>ISO8601（UTC）。Unity 侧只用于展示，时间是后端权威。</summary>
        public string createdAt;
        public string updatedAt;
    }

    /// <summary>
    /// Date.parse 的等价实现（Unity 的 DateTime.Parse 对 ISO8601 带毫秒/时区的支持
    /// 依赖运行时的区域设置，行为不够稳定）。这里只解析后端一定会返回的
    /// "yyyy-MM-ddTHH:mm:ss[.fffffff]Z" 或带 ±hh:mm 偏移的形式。
    /// </summary>
    public static class IsoTime
    {
        /// <summary>解析失败返回 false（调用方应做降级显示，而不是抛异常）。</summary>
        public static bool TryParse(string iso, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(iso) || iso.Length < 19)
            {
                return false;
            }

            if (!DateTime.TryParse(iso.Substring(0, 19), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var baseTime))
            {
                return false;
            }

            var value = DateTime.SpecifyKind(baseTime, DateTimeKind.Utc);
            var rest = iso.Substring(19);

            // 毫秒/亚秒
            var dot = rest.IndexOf('.');
            if (dot == 0)
            {
                var end = 1;
                while (end < rest.Length && char.IsDigit(rest[end])) end++;
                var frac = rest.Substring(1, end - 1);
                if (frac.Length > 7) frac = frac.Substring(0, 7);
                var ticks = long.Parse(frac.PadRight(7, '0'));
                value = value.AddTicks(ticks);
                rest = rest.Substring(end);
            }

            // 时区偏移
            if (rest.StartsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                utc = value;
                return true;
            }
            if ((rest.StartsWith("+") || rest.StartsWith("-")) && rest.Length >= 6)
            {
                if (!int.TryParse(rest.Substring(1, 2), out var hh) || !int.TryParse(rest.Substring(4, 2), out var mm))
                {
                    return false;
                }
                var offset = new TimeSpan(hh, mm, 0);
                utc = rest[0] == '+' ? value - offset : value + offset;
                return true;
            }

            // 没有时区信息：按 UTC 处理（后端始终返回 UTC）
            utc = value;
            return true;
        }

        /// <summary>格式化为本地时间 "MM-dd HH:mm:ss"；解析失败时原样返回。</summary>
        public static string ToLocalDisplay(string iso)
        {
            if (!TryParse(iso, out var utc))
            {
                return string.IsNullOrEmpty(iso) ? "—" : iso;
            }
            return utc.ToLocalTime().ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Unity 世界坐标。三端字段名一致：x / y / z。</summary>
    [Serializable]
    public class PositionData
    {
        public float x;
        public float y;
        public float z;

        public PositionData() { }

        public PositionData(Vector3 v)
        {
            Set(v);
        }

        public void Set(Vector3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }

        public PositionData Clone()
        {
            return new PositionData { x = x, y = y, z = z };
        }

        public override string ToString()
        {
            // 保留两位小数，便于在 UI 上与 React 管理端显示一致
            return string.Format("({0:0.00}, {1:0.00}, {2:0.00})", x, y, z);
        }
    }

    /// <summary>
    /// 上报问题的请求体。只包含后端允许客户端写入的字段：
    /// 状态由后端强制为 open，id 与时间戳由后端生成。
    /// </summary>
    [Serializable]
    public class CreateIssueRequest
    {
        public string title;
        public string description;
        public string priority;
        public PositionData position;
    }
}
