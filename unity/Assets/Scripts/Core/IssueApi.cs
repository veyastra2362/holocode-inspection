using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Inspection
{
    /// <summary>一次 API 调用的结果。成功时 Data 有值，失败时 ErrorMessage 一定可读。</summary>
    public class ApiResult<T>
    {
        public bool Success;
        public T Data;
        /// <summary>给用户看的中文错误信息（含 HTTP 状态或后端 error 文案）。</summary>
        public string ErrorMessage = string.Empty;
        /// <summary>HTTP 状态码；网络层失败（连不上/超时）时为 0。</summary>
        public long HttpStatus;
        /// <summary>后端错误码（BAD_REQUEST / NOT_FOUND / …），便于分支处理。</summary>
        public string ErrorCode = string.Empty;

        public static ApiResult<T> Ok(T data)
        {
            return new ApiResult<T> { Success = true, Data = data, HttpStatus = 200 };
        }

        public static ApiResult<T> Fail(string message, long status = 0, string code = "")
        {
            return new ApiResult<T>
            {
                Success = false,
                ErrorMessage = message,
                HttpStatus = status,
                ErrorCode = code
            };
        }
    }

    /// <summary>
    /// Go 后端信封的解析结果：data 保留为原始 JSON 文本，
    /// 因为 JsonUtility 无法直接表示“可能是对象也可能是数组”的字段。
    /// </summary>
    public class Envelope
    {
        public bool Ok;
        public string Error;
        public string Code;
        public string DataJson;

        /// <summary>解析 {"ok":..,"error":..,"code":..,"data":..}。</summary>
        public static Envelope Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // 注意：不能用 JsonUtility.FromJson 解析标量字段。
            // 这里不用它是因为 JSON 里可能出现会被解析器误解的字符（例如未转义控制符），
            // 而且 ok/error/code 用扫描即可拿到，行为更容易预测和测试。
            var okRaw = JsonScan.ValueOf(text, "ok");
            if (okRaw == null)
            {
                return null; // 连 ok 都没有，说明不是本后端的响应
            }

            return new Envelope
            {
                Ok = string.Equals(okRaw.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                Error = JsonScan.AsString(JsonScan.ValueOf(text, "error")),
                Code = JsonScan.AsString(JsonScan.ValueOf(text, "code")),
                DataJson = JsonScan.ValueOf(text, "data")
            };
        }

        /// <summary>把失败响应转成用户可读的错误。</summary>
        public ApiResult<T> ToFailure<T>(long status)
        {
            var msg = string.IsNullOrEmpty(Error) ? "后端返回失败" : Error;
            return ApiResult<T>.Fail(msg, status, Code ?? string.Empty);
        }
    }

    [Serializable]
    internal class ArrayWrapper<T>
    {
        public T[] items;
    }

    /// <summary>
    /// Go 后端的 HTTP 客户端。
    /// 抽象成接口是为了让上报流程可以在 EditMode 测试里注入假实现
    /// （验证“一次点击只建一个标记”“取消后移除未保存标记”等症状）。
    /// </summary>
    public interface IIssueApi
    {
        string BaseUrl { get; }
        IEnumerator CreateIssue(CreateIssueRequest request, Action<ApiResult<IssueData>> onDone);
        IEnumerator FetchIssues(Action<ApiResult<List<IssueData>>> onDone);
        IEnumerator UpdateStatus(string id, IssueStatus status, Action<ApiResult<IssueData>> onDone);
    }

    public class IssueApi : IIssueApi
    {
        public const string DefaultBaseUrl = "http://localhost:8080";
        private const int TimeoutSeconds = 10;

        public string BaseUrl { get; }

        public IssueApi(string baseUrl)
        {
            BaseUrl = NormalizeBaseUrl(baseUrl);
        }

        /// <summary>允许用户填 "localhost:8080"、末尾带斜杠、前后带空格等情况。</summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }
            return url.TrimEnd('/');
        }

        public IEnumerator CreateIssue(CreateIssueRequest request, Action<ApiResult<IssueData>> onDone)
        {
            var url = BaseUrl + "/api/issues";
            var body = JsonUtility.ToJson(request);
            ApiResult<IssueData> result = null;
            yield return SendRaw(url, "POST", body, text =>
            {
                var env = Envelope.Parse(text);
                if (env == null)
                {
                    return ApiResult<IssueData>.Fail("后端响应格式不正确（无法解析 JSON）", 200, "BAD_RESPONSE");
                }
                if (!env.Ok)
                {
                    return env.ToFailure<IssueData>(200);
                }
                return ParseIssue(env.DataJson, 200);
            }, r => result = r);
            onDone?.Invoke(result ?? ApiResult<IssueData>.Fail("请求未完成"));
        }

        public IEnumerator FetchIssues(Action<ApiResult<List<IssueData>>> onDone)
        {
            var url = BaseUrl + "/api/issues";
            ApiResult<List<IssueData>> result = null;
            yield return SendRaw(url, "GET", null, text =>
            {
                var env = Envelope.Parse(text);
                var parsed = env == null
                    ? ApiResult<IssueData[]>.Fail("后端响应格式不正确（无法解析 JSON）", 200, "BAD_RESPONSE")
                    : ParseArray(env);

                if (!parsed.Success)
                {
                    return ApiResult<List<IssueData>>.Fail(parsed.ErrorMessage, parsed.HttpStatus, parsed.ErrorCode);
                }
                return ApiResult<List<IssueData>>.Ok(new List<IssueData>(parsed.Data));
            }, r => result = r);
            onDone?.Invoke(result ?? ApiResult<List<IssueData>>.Fail("请求未完成"));
        }

        public IEnumerator UpdateStatus(string id, IssueStatus status, Action<ApiResult<IssueData>> onDone)
        {
            if (string.IsNullOrEmpty(id))
            {
                onDone?.Invoke(ApiResult<IssueData>.Fail("缺少问题 ID"));
                yield break;
            }

            // 用 POST /api/issues/{id}/status 而不是 PATCH：后端两者都支持，
            // 但某些平台/代理对 PATCH 支持不佳，这里选兼容性更好的。
            var url = BaseUrl + "/api/issues/" + UnityWebRequest.EscapeURL(id) + "/status";
            var body = "{\"status\":\"" + IssueEnums.ToWire(status) + "\"}";
            ApiResult<IssueData> result = null;
            yield return SendRaw(url, "POST", body, text =>
            {
                var env = Envelope.Parse(text);
                if (env == null)
                {
                    return ApiResult<IssueData>.Fail("后端响应格式不正确（无法解析 JSON）", 200, "BAD_RESPONSE");
                }
                if (!env.Ok)
                {
                    return env.ToFailure<IssueData>(200);
                }
                return ParseIssue(env.DataJson, 200);
            }, r => result = r);
            onDone?.Invoke(result ?? ApiResult<IssueData>.Fail("请求未完成"));
        }

        // ---- 解析辅助（有 EditMode 单元测试覆盖） ----

        public static ApiResult<IssueData> ParseIssue(string dataJson, long status)
        {
            if (string.IsNullOrEmpty(dataJson) || dataJson == "null")
            {
                return ApiResult<IssueData>.Fail("后端未返回问题数据", status, "BAD_RESPONSE");
            }
            IssueData issue = null;
            try
            {
                issue = JsonUtility.FromJson<IssueData>(dataJson);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IssueApi] 解析 issue 失败：" + e.Message);
            }
            if (issue == null || string.IsNullOrEmpty(issue.id))
            {
                return ApiResult<IssueData>.Fail("后端返回的问题数据无法解析（缺少 id）", status, "BAD_RESPONSE");
            }
            return ApiResult<IssueData>.Ok(issue);
        }

        /// <summary>JsonUtility 不支持顶层数组，这里给数组加一层 {"items": ...} 再解析。</summary>
        public static ApiResult<IssueData[]> ParseArray(Envelope env)
        {
            if (env == null)
            {
                return ApiResult<IssueData[]>.Fail("后端响应格式不正确", 200, "BAD_RESPONSE");
            }
            if (!env.Ok)
            {
                return env.ToFailure<IssueData[]>(200);
            }
            if (string.IsNullOrEmpty(env.DataJson) || env.DataJson == "null")
            {
                return ApiResult<IssueData[]>.Ok(Array.Empty<IssueData>());
            }
            if (!env.DataJson.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return ApiResult<IssueData[]>.Fail("后端返回的问题列表不是数组", 200, "BAD_RESPONSE");
            }
            try
            {
                var wrapper = JsonUtility.FromJson<ArrayWrapper<IssueData>>("{\"items\":" + env.DataJson + "}");
                return ApiResult<IssueData[]>.Ok(wrapper?.items ?? Array.Empty<IssueData>());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IssueApi] 解析列表失败：" + e.Message);
                return ApiResult<IssueData[]>.Fail("后端返回的问题列表无法解析", 200, "BAD_RESPONSE");
            }
        }

        // ---- 请求发送 ----

        private IEnumerator SendRaw<T>(string url, string method, string body, Func<string, ApiResult<T>> parse, Action<ApiResult<T>> onDone)
        {
            using (var req = new UnityWebRequest(url, method))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = TimeoutSeconds;
                req.SetRequestHeader("Accept", "application/json");
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.SetRequestHeader("Content-Type", "application/json");
                }

                yield return req.SendWebRequest();

                var status = req.responseCode;
                var text = req.downloadHandler != null ? req.downloadHandler.text : null;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    // 4xx/5xx 时后端仍然返回 JSON 信封，优先展示其中的 error 文案。
                    var env = Envelope.Parse(text);
                    string message;
                    string code = env != null ? env.Code : string.Empty;

                    if (env != null && !string.IsNullOrEmpty(env.Error))
                    {
                        message = env.Error;
                    }
                    else if (req.result == UnityWebRequest.Result.ConnectionError)
                    {
                        message = $"无法连接后端 {BaseUrl}，请确认 Go 服务已启动";
                    }
                    else if (req.result == UnityWebRequest.Result.DataProcessingError || status >= 500)
                    {
                        message = $"后端处理失败（HTTP {status}）";
                    }
                    else
                    {
                        message = req.error ?? "未知网络错误";
                    }

                    Debug.LogWarning($"[IssueApi] {method} {url} 失败：status={status} result={req.result} msg={message}");
                    onDone?.Invoke(ApiResult<T>.Fail(message, status, code ?? string.Empty));
                    yield break;
                }

                onDone?.Invoke(parse(text));
            }
        }
    }
}
