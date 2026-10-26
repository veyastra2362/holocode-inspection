// 支持“真正跑起来”的 UnityEngine 行为模拟（仅用于离线验证，不是交付物）。
// 与 .verify/UnityCompileCheck/Stubs 的区别：这里对象树、组件、事件、协程都是真实可用的。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public float magnitude => (float)Math.Sqrt(x * x + y * y);
        public float sqrMagnitude => x * x + y * y;
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0);
        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
        public override string ToString() => $"({x},{y})";
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 up => new Vector3(0, 1, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public static Vector3 right => new Vector3(1, 0, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                var m = magnitude;
                return m < 1e-6f ? zero : new Vector3(x / m, y / m, z / m);
            }
        }
        public static Vector3 operator *(Vector3 a, float f) => new Vector3(a.x * f, a.y * f, a.z * f);
        public static Vector3 operator *(float f, Vector3 a) => a * f;
        public static Vector3 operator /(Vector3 a, float f) => new Vector3(a.x / f, a.y / f, a.z / f);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static bool operator ==(Vector3 a, Vector3 b) => Math.Abs(a.x - b.x) < 1e-6f && Math.Abs(a.y - b.y) < 1e-6f && Math.Abs(a.z - b.z) < 1e-6f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object o) => o is Vector3 v && v == this;
        public override int GetHashCode() => 0;
        public override string ToString() => $"({x},{y},{z})";
    }

    public struct Quaternion
    {
        public static Quaternion identity => default;
        public static Quaternion Euler(float x, float y, float z) => default;
        public static Quaternion FromToRotation(Vector3 a, Vector3 b) => default;
        public static Vector3 operator *(Quaternion q, Vector3 v) => v;
        public Vector3 eulerAngles => default;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; a = 1; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1, 1);
        public static Color clear => new Color(0, 0, 0, 0);
        public override string ToString() => $"RGBA({r:0.00},{g:0.00},{b:0.00},{a:0.00})";
    }

    /// <summary>模拟 Unity 的“已销毁”语义：Destroy 后对象 == null 成立。</summary>
    public class Object
    {
        private static readonly List<Object> All = new List<Object>();
        internal bool Destroyed;
        public string name { get; set; }

        internal Object()
        {
            All.Add(this);
        }

        public static void Destroy(Object o)
        {
            if (o == null) return;
            o.Destroyed = true;
            if (o is GameObject go) go.DestroyTree();
        }

        public static void Destroy(Object o, float t) => Destroy(o);
        public static void DestroyImmediate(Object o) => Destroy(o);
        public static T FindObjectOfType<T>() where T : Object => All.OfType<T>().FirstOrDefault(o => !o.Destroyed);

        public static bool operator ==(Object a, Object b)
        {
            var an = a is null || a.Destroyed;
            var bn = b is null || b.Destroyed;
            if (an && bn) return true;
            if (an || bn) return false;
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b) => !(a == b);
        public static implicit operator bool(Object o) => !(o == null);
        public override bool Equals(object o) => o is Object other ? this == other : ReferenceEquals(this, o);
        public override int GetHashCode() => base.GetHashCode();
    }

    public class Component : Object
    {
        internal GameObject _go;
        public GameObject gameObject => _go;
        public Transform transform => _go != null ? _go.transform : null;

        public T GetComponent<T>() => _go != null ? _go.GetComponent<T>() : default;
        public T GetComponentInParent<T>() => _go != null ? _go.GetComponentInParent<T>() : default;
        public T GetComponentInChildren<T>() => _go != null ? _go.GetComponentInChildren<T>() : default;
        public T[] GetComponentsInChildren<T>() => _go != null ? _go.GetComponentsInChildren<T>() : Array.Empty<T>();
        public T AddComponent<T>() where T : Component => _go.AddComponent<T>();
        public string tag { get => _go != null ? _go.tag : null; set { if (_go != null) _go.tag = value; } }
    }

    public class Behaviour : Component
    {
        public bool enabled { get; set; } = true;
    }

    public class MonoBehaviour : Behaviour
    {
        private readonly List<IEnumerator> _routines = new List<IEnumerator>();
        internal void PumpRoutines() { }

        public Coroutine StartCoroutine(IEnumerator routine)
        {
            if (routine == null) return null;
            var c = new Coroutine { Routine = routine };
            _routines.Add(routine);
            return c;
        }

        public void StopCoroutine(Coroutine c) { }
        public void StopAllCoroutines() => _routines.Clear();

        /// <summary>由测试驱动盘推进协程（模拟 Unity 每帧推进一次）。</summary>
        internal bool StepRoutines()
        {
            var alive = false;
            for (var i = _routines.Count - 1; i >= 0; i--)
            {
                var r = _routines[i];
                bool moved;
                try { moved = r.MoveNext(); }
                catch (Exception e) { Console.WriteLine("  协程异常: " + e); moved = false; }
                if (!moved) _routines.RemoveAt(i);
                else alive = true;
            }
            return alive;
        }

        internal bool HasRoutines => _routines.Count > 0;

        /// <summary>测试用：推进协程并返回是否仍有未完成协程。</summary>
        public bool StepRoutinesPublic() => StepRoutines();

        /// <summary>测试用：推进所有待执行协程（包含 yield return 子协程的语义）。</summary>
        public void RunPendingPublic()
        {
            var routines = _routines.ToList();
            _routines.Clear();
            foreach (var r in routines) DrivePublic(r);
        }

        private static void DrivePublic(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            var guard = 0;
            while (stack.Count > 0 && guard++ < 5000)
            {
                var top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception e) { Console.WriteLine("  协程异常: " + e.Message); return; }
                if (!moved) { stack.Pop(); continue; }
                if (top.Current is IEnumerator child) stack.Push(child);
            }
        }
    }

    public class Coroutine { internal IEnumerator Routine; }

    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Vector3 localScale { get; set; } = Vector3.one;
        public Vector3 eulerAngles { get; set; }
        public Quaternion rotation { get; set; }
        public Quaternion localRotation { get; set; }
        public Transform parent { get; set; }
        private readonly List<Transform> _children = new List<Transform>();
        public int childCount => _children.Count;

        public void SetParent(Transform p, bool worldPositionStays)
        {
            if (parent != null) parent._children.Remove(this);
            parent = p;
            if (p != null) p._children.Add(this);
        }

        public Transform Find(string n) => _children.FirstOrDefault(c => c.gameObject.name == n);
    }

    public class GameObject : Object
    {
        private readonly List<Component> _components = new List<Component>();
        public Transform transform { get; private set; }
        public int layer { get; set; }
        public bool isStatic { get; set; }
        public string tag { get; set; } = "Untagged";
        private bool _active = true;
        public bool activeSelf => _active;
        public void SetActive(bool v) => _active = v;

        public GameObject()
        {
            transform = new Transform { _go = this };
        }

        public GameObject(string name) : this() { this.name = name; }

        public GameObject(string name, params Type[] types) : this(name)
        {
            foreach (var t in types)
            {
                var c = (Component)Activator.CreateInstance(t);
                AddComponentInstance(c);
                // 模拟 Unity：带 UI 组件的对象会自动获得 RectTransform，且 transform 即为该实例
                if (t == typeof(RectTransform) && c is RectTransform rt)
                {
                    transform = rt;
                }
            }
        }

        internal void AddComponentInstance(Component c)
        {
            c._go = this;
            _components.Add(c);
        }

        public Component AddComponent(Type t)
        {
            var c = (Component)Activator.CreateInstance(t, true);
            AddComponentInstance(c);
            return c;
        }

        public T AddComponent<T>() where T : Component
        {
            var c = (T)Activator.CreateInstance(typeof(T), true);
            AddComponentInstance(c);
            return c;
        }

        public T GetComponent<T>()
        {
            foreach (var c in _components)
            {
                if (c is T t) return t;
            }
            return default;
        }

        public T GetComponentInParent<T>() => GetComponent<T>();

        public T GetComponentInChildren<T>()
        {
            var own = GetComponent<T>();
            if (own != null) return own;
            foreach (var child in AllChildren())
            {
                var c = child.GetComponent<T>();
                if (c != null) return c;
            }
            return default;
        }

        public T[] GetComponentsInChildren<T>()
        {
            var list = new List<T>();
            var own = GetComponent<T>();
            if (own != null) list.Add(own);
            foreach (var child in AllChildren())
            {
                var c = child.GetComponent<T>();
                if (c != null) list.Add(c);
            }
            return list.ToArray();
        }

        private IEnumerable<GameObject> AllChildren() => WalkChildren(transform);

        private static IEnumerable<GameObject> WalkChildren(Transform t)
        {
            var kids = ChildrenOf(t);
            foreach (var child in kids)
            {
                yield return child.gameObject;
                foreach (var g in WalkChildren(child)) yield return g;
            }
        }

        private static List<Transform> ChildrenOf(Transform t)
        {
            var field = typeof(Transform).GetField("_children",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return new List<Transform>((List<Transform>)field.GetValue(t));
        }

        internal void DestroyTree()
        {
            foreach (var g in WalkChildren(transform).ToList())
            {
                g.Destroyed = true;
            }
        }

        public static GameObject CreatePrimitive(PrimitiveType t)
        {
            var go = new GameObject(t.ToString());
            go.AddComponent<MeshRenderer>();
            go.AddComponent<BoxCollider>();
            return go;
        }
    }

    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public class Renderer : Component
    {
        public Material sharedMaterial { get; set; }
        public Material material { get; set; }
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        public void SetPropertyBlock(MaterialPropertyBlock b) { _block.CopyFrom(b); }
        public void GetPropertyBlock(MaterialPropertyBlock b) { b.CopyFrom(_block); }
    }

    public class MeshRenderer : Renderer { }

    public class MaterialPropertyBlock
    {
        public Color Color = Color.white;
        public void SetColor(string name, Color c) => Color = c;
        public Color GetColor(string name) => Color;
        internal void CopyFrom(MaterialPropertyBlock o) => Color = o.Color;
    }

    public class Material : Object
    {
        public Material() { }
        public Material(Shader s) { }
        public Color color { get; set; } = Color.white;
    }

    public class Shader : Object
    {
        public static Shader Find(string name) => new Shader();
    }

    public class Collider : Component { }
    public class BoxCollider : Collider { }

    public class Camera : Behaviour
    {
        private readonly Func<Vector3, Ray> _rayFactory;
        public Camera() { }
        public Camera(Func<Vector3, Ray> rayFactory) { _rayFactory = rayFactory; }
        public float fieldOfView { get; set; } = 60;
        public float nearClipPlane { get; set; }
        public float farClipPlane { get; set; }
        public CameraClearFlags clearFlags { get; set; }
        public Color backgroundColor { get; set; }
        public Func<Vector3, Ray> RayFactory { get; set; }
        public Ray ScreenPointToRay(Vector3 pos)
        {
            var f = RayFactory ?? _rayFactory;
            return f != null ? f(pos) : default;
        }
    }

    public enum CameraClearFlags { Skybox, SolidColor, Depth, Nothing }

    public struct Ray
    {
        public Vector3 origin;
        public Vector3 direction;
        public Ray(Vector3 o, Vector3 d) { origin = o; direction = d; }
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
        public Collider collider;
    }

    /// <summary>可编程的物理世界：测试用 Physics.Register 注入“射线会命中什么”。</summary>
    public static class Physics
    {
        public static readonly List<RaycastHit> Scene = new List<RaycastHit>();

        public static bool Raycast(Ray ray, out RaycastHit hit, float distance, int layerMask, QueryTriggerInteraction q)
        {
            hit = Scene.Where(h => h.distance <= distance).OrderBy(h => h.distance).FirstOrDefault();
            return hit.collider != null;
        }

        public static RaycastHit[] RaycastAll(Ray ray, float distance, int layerMask, QueryTriggerInteraction q)
            => Scene.Where(h => h.distance <= distance).OrderBy(h => h.distance).ToArray();
    }

    public enum QueryTriggerInteraction { UseGlobal, Ignore, Collide }

    public struct LayerMask
    {
        public int value;
        public static implicit operator int(LayerMask m) => m.value;
        public static implicit operator LayerMask(int v) => new LayerMask { value = v };
    }

    public class Light : Behaviour
    {
        public LightType type { get; set; }
        public float intensity { get; set; }
    }

    public enum LightType { Spot, Directional, Point, Area }

    public class AudioListener : Behaviour { }

    public static class Debug
    {
        public static readonly List<string> Logs = new List<string>();
        public static void Log(object msg) => Logs.Add("LOG " + msg);
        public static void LogWarning(object msg) => Logs.Add("WARN " + msg);
        public static void LogError(object msg) => Logs.Add("ERROR " + msg);
    }

    public static class Time
    {
        public static float deltaTime => 1f / 60f;
        public static float unscaledTime => 0f;
    }

    public static class Application
    {
        public static string persistentDataPath => System.IO.Path.GetTempPath();
        public static int targetFrameRate { get; set; }
        public static RuntimePlatform platform { get; set; } = RuntimePlatform.WindowsPlayer;
    }

    public enum RuntimePlatform { WindowsPlayer, WindowsEditor, Android, IPhonePlayer, LinuxPlayer, OSXPlayer }

    public static class QualitySettings { public static int vSyncCount { get; set; } }

    public static class Resources
    {
        public static T GetBuiltinResource<T>(string path) where T : Object => Activator.CreateInstance<T>();
    }

    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Abs(float v) => Math.Abs(v);
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float DeltaAngle(float a, float b) => b - a;
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj) => MiniJson.Serialize(obj);
        public static T FromJson<T>(string json) => (T)MiniJson.Deserialize(typeof(T), json);
    }

    public class WaitForSecondsRealtime : IEnumerator
    {
        private float _remaining;
        private bool _done;
        public WaitForSecondsRealtime(float s) { _remaining = s; }
        public object Current => null;
        public bool MoveNext()
        {
            if (_done) return false;
            _remaining -= 0.1f;
            if (_remaining <= 0) { _done = true; return false; }
            return true;
        }
        public void Reset() { }
    }

    public static class Input
    {
        public static bool GetMouseButton(int b) => false;
        public static bool GetMouseButtonDown(int b) => false;
        public static bool GetMouseButtonUp(int b) => false;
        public static bool GetKey(KeyCode k) => false;
        public static bool GetKeyDown(KeyCode k) => false;
        public static float GetAxisRaw(string n) => 0f;
        public static Vector3 mousePosition => default;
    }

    public enum KeyCode { W, A, S, D, UpArrow, DownArrow, LeftArrow, RightArrow, LeftShift, RightShift, Escape, F5, F9, F10, F11 }

    public class WaitForEndOfFrame : System.Collections.IEnumerator
{
    public object Current => null;
    public bool MoveNext() => false;
    public void Reset() { }
}

public static class ScreenCapture
{
    public static void CaptureScreenshot(string path) { }
}


public static class Screen { public static int width => 1920; public static int height => 1080; }

    [AttributeUsage(AttributeTargets.Field)] public class TooltipAttribute : Attribute { public TooltipAttribute(string t) { } }
    [AttributeUsage(AttributeTargets.Field)] public class HeaderAttribute : Attribute { public HeaderAttribute(string h) { } }
    [AttributeUsage(AttributeTargets.Class)] public class RequireComponent : Attribute { public RequireComponent(Type t) { } }
    [AttributeUsage(AttributeTargets.Class)] public class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int o) { } }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public class SerializeField : Attribute { }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 pivot { get; set; }
        public Vector2 anchoredPosition { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Vector2 offsetMin { get; set; }
        public Vector2 offsetMax { get; set; }
    }
}

namespace UnityEngine.Events
{
    public delegate void UnityAction();
    public class UnityEvent
    {
        private readonly List<UnityAction> _listeners = new List<UnityAction>();
        public void AddListener(UnityAction a) => _listeners.Add(a);
        public void RemoveAllListeners() => _listeners.Clear();
        public void Invoke() { foreach (var l in _listeners.ToList()) l(); }
    }
}

namespace UnityEngine.EventSystems
{
    public class EventSystem : UnityEngine.Behaviour
    {
        public static EventSystem current;
        public bool PointerOverUI { get; set; }
        public bool IsPointerOverGameObject() => PointerOverUI;
        public void SetSelectedGameObject(UnityEngine.GameObject go) { }
    }
    public class StandaloneInputModule : UnityEngine.Behaviour { }
    public class GraphicRaycaster : UnityEngine.Behaviour { }
}

namespace UnityEngine.UI
{
    using UnityEngine.Events;

    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }
    public enum HorizontalWrapMode { Wrap, Overflow }
    public enum VerticalWrapMode { Truncate, Overflow }

    public class Font : UnityEngine.Object { }

    public class Graphic : UnityEngine.Behaviour
    {
        public UnityEngine.Color color { get; set; } = UnityEngine.Color.white;
        public bool raycastTarget { get; set; }
    }

    public class Text : Graphic
    {
        public Font font { get; set; }
        public int fontSize { get; set; }
        public string text { get; set; } = string.Empty;
        public TextAnchor alignment { get; set; }
        public HorizontalWrapMode horizontalOverflow { get; set; }
        public VerticalWrapMode verticalOverflow { get; set; }
        public bool supportRichText { get; set; }
        public UnityEngine.RectTransform rectTransform => (UnityEngine.RectTransform)transform;
    }

    public class Image : Graphic { }

    public class Selectable : UnityEngine.Behaviour
    {
        public bool interactable { get; set; } = true;
        public Graphic targetGraphic { get; set; }
    }

    public class Button : Selectable
    {
        public readonly UnityEvent onClick = new UnityEvent();
        public void Click() { if (interactable) onClick.Invoke(); }
    }

    public class InputField : Selectable
    {
        public enum LineType { SingleLine, MultiLineSubmit, MultiLineNewline }
        public Text textComponent { get; set; }
        public Graphic placeholder { get; set; }
        public int characterLimit { get; set; }
        public LineType lineType { get; set; }
        public string text { get; set; } = string.Empty;
        public bool isFocused { get; private set; }
        public void ActivateInputField() => isFocused = true;
        public void DeactivateInputField() => isFocused = false;
    }

    public class Canvas : UnityEngine.Behaviour
    {
        public RenderMode renderMode { get; set; }
    }

    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }

    public class CanvasScaler : UnityEngine.Behaviour
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public ScaleMode uiScaleMode { get; set; }
        public UnityEngine.Vector2 referenceResolution { get; set; }
        public float matchWidthOrHeight { get; set; }
    }

    public class HorizontalLayoutGroup : UnityEngine.Behaviour
    {
        public float spacing { get; set; }
        public bool childForceExpandWidth { get; set; }
        public bool childForceExpandHeight { get; set; }
        public bool childControlWidth { get; set; }
        public bool childControlHeight { get; set; }
    }
}

namespace UnityEngine.Networking
{
    public class UploadHandler : IDisposable { public void Dispose() { } }
    public class DownloadHandler : IDisposable
    {
        public virtual string text { get; set; }
        public void Dispose() { }
    }

    public class UploadHandlerRaw : UploadHandler { public byte[] Data; public UploadHandlerRaw(byte[] d) { Data = d; } }
    public class DownloadHandlerBuffer : DownloadHandler { }

    /// <summary>yield return 时的“已完成”操作。</summary>
    public class AsyncOperation : System.Collections.IEnumerator
    {
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }

    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }

        /// <summary>测试注入的“服务器”：url+method -> (status, body, result)。</summary>
        public static Func<string, string, string, (long status, string body, Result result)> Server;

        public UnityWebRequest(string url, string method) { Url = url; Method = method; }
        public string Url { get; }
        public string Method { get; }
        public UploadHandler uploadHandler { get; set; }
        public DownloadHandler downloadHandler { get; set; }
        public int timeout { get; set; }
        public long responseCode { get; private set; }
        public Result result { get; private set; }
        public string error { get; private set; }
        private string _body;
        public void SetRequestHeader(string n, string v)
        {
            if (string.Equals(n, "Content-Type", StringComparison.OrdinalIgnoreCase)) _contentType = v;
        }
        public string Body => _body;
        private string _contentType;

        /// <summary>真实 HTTP：当 Server 桩为 null 时，用 HttpClient 真正访问网络。</summary>
        private static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        /// <summary>
        /// 同步完成请求（Unity 中返回 AsyncOperation，yield return 它时框架会自动推进；
        /// 这里用自定义 AsyncOperation 模拟同样语义）。
        /// </summary>
        public AsyncOperation SendWebRequest()
        {
            if (Server != null)
            {
                var resp = Server(Url, Method, _body);
                responseCode = resp.status;
                result = resp.result;
                downloadHandler = new DownloadHandlerBuffer { text = resp.body ?? string.Empty };
                if (resp.result != Result.Success) error = "request failed";
                return new AsyncOperation();
            }

            // 真实网络请求
            try
            {
                using (var req = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(Method), Url))
                {
                    var payload = _body;
                    if (payload == null && uploadHandler is UploadHandlerRaw raw)
                    {
                        payload = System.Text.Encoding.UTF8.GetString(raw.Data);
                    }
                    if (payload != null)
                    {
                        req.Content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8,
                            _contentType ?? "application/json");
                    }
                    var res = Http.Send(req);
                    responseCode = (long)res.StatusCode;
                    downloadHandler = new DownloadHandlerBuffer { text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                    result = res.IsSuccessStatusCode ? Result.Success : Result.ProtocolError;
                    if (!res.IsSuccessStatusCode) error = "HTTP " + responseCode;
                }
            }
            catch (Exception ex)
            {
                responseCode = 0;
                result = Result.ConnectionError;
                error = ex.Message;
                downloadHandler = new DownloadHandlerBuffer { text = string.Empty };
            }
            return new AsyncOperation();
        }
        public void Dispose() { }
        public static string EscapeURL(string s) => s;
    }
}












