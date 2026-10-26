# 虚拟现场巡检 · 问题标注（无 AR 设备备选版）

三端最小闭环：**Unity 虚拟现场点击标注 → Go 后端持久化 → React 管理端查看与改状态**。

```
Unity (C#)  ──POST /api/issues──►  Go (SQLite)  ◄──GET/PATCH──  React (TypeScript)
   点击/拖拽/射线放置                 单一数据源                   列表/筛选/改状态
```

> 本题为「无 AR 设备备选版」，不需要 AR 真机；Unity Editor 或桌面设备即可运行。

---

## 1. 使用的版本

| 部分 | 版本 | 说明 |
| --- | --- | --- |
| Unity | **6000.6 LTS（Unity 6.6）**，`ProjectVersion.txt` 为 `6000.6.7f1` | 在 Unity 6.6 中实际导入、编译、跑测试；使用 **旧版 Input Manager**（`activeInputHandler: 0`），无需 Input System 包 |
| C# | .NET Standard 2.1（Unity 默认） | 不使用 C# 9+ 语法，兼容 Unity 的编译器约束 |
| Go | **1.21+**（开发验证用 1.27.3） | 标准库 `net/http` + `modernc.org/sqlite`（纯 Go，**无需 CGO/gcc**） |
| SQLite | 内嵌于 `modernc.org/sqlite` v1.61 | 单文件数据库，无需安装数据库服务 |
| Node.js | **18+**（开发用 24.14） | 仅构建/开发用 |
| React / TypeScript | React **18.3** / TS **5.6** | 构建工具 Vite 5.4 |
| 测试 | `go test`、**Vitest** + Testing Library、Unity Test Framework (NUnit) | 见第 6 节 |

---

## 2. 如何启动

### 2.1 一条命令启动「Go + React」（推荐先跑这个）

```bash
# Windows：双击 start.bat
# macOS / Linux / WSL：
./start.sh
```

脚本会：构建前端 `web/dist` → 编译 `bin/inspection-server.exe` → 在 **http://localhost:8080** 启动
（Go 同时托管 API 和 React 页面，所以浏览器只需要这一个地址，没有跨域问题）。

### 2.2 分别启动（开发模式）

```bash
# 终端 1：后端
cd backend
go run .                      # 默认 :8080，数据库 ./data/inspection.db

# 终端 2：前端（热更新，/api 由 vite 代理到 8080）
cd web
npm install
npm run dev                   # http://localhost:5173
```

后端可用参数/环境变量：

| 参数 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `-addr` | `ADDR` | `:8080` | 监听地址 |
| `-db` | `DB_PATH` | `data/inspection.db` | SQLite 文件路径（目录不存在会自动创建） |
| `-web` | `WEB_DIR` | 自动探测 `../web/dist` | 前端构建产物目录；留空则只提供 API |

### 2.3 Unity 客户端

1. 用 Unity Hub 打开 `unity/` 目录（**Unity 6.6 LTS**；本项目在 `6000.6.7f1` 上验证）。
2. 打开场景 `Assets/Scenes/Inspection.unity`，点 **Play**。
3. 顶部会显示后端连通状态；连不上时左下角会变红并给出提示。
4. 菜单 **Inspection → 运行场景自检**（快捷键 `Ctrl+Shift+T`）可一键验证场景与后端，结果写入 `Logs/selftest.txt`。

> 场景里只有一个空对象 `InspectionBootstrap`，挂着 `SceneBootstrap`。
> 整个虚拟现场（地面/墙/机柜/桥架/控制台）和 UI 都是**运行时用代码生成**的，
> 因此没有 Prefab 引用、没有丢失资源的风险，打开就能跑。

**后端地址**：在 Inspector 里改 `SceneBootstrap → BackendUrl`（默认 `http://localhost:8080`）。
填 `192.168.1.10:8080` 这类不带 `http://` 的写法也会被自动补全。

| 操作 | 按键/鼠标 |
| --- | --- |
| 移动 | `W A S D` / 方向键（`Shift` 加速） |
| 转视角 | **右键拖拽**（中键也可以） |
| 前后 | 滚轮 |
| 放置问题标记 | **左键点击**设备/墙面/地面（松手时才判定，拖拽不会误放） |
| 取消待提交标记 | `ESC` 或表单里的「取消」 |
| 查看已有标记详情 | 左键点击标记本身 |
| 从后端刷新历史标记 | `F5` |
| 截图 / 自动演示 / 自检 | `F9` / `F10` / `F11` |

---

## 3. 后端接口设计

统一响应信封（所有接口，含错误）：

```jsonc
// 成功
{ "ok": true, "data": { ... } }
{ "ok": true, "data": [ ... ], "total": 6 }          // 列表额外带 total
// 失败
{ "ok": false, "code": "BAD_REQUEST", "error": "title is required and must not be blank",
  "fields": { "title": "title is required and must not be blank" } }
```

`code` 取值：`BAD_REQUEST` / `NOT_FOUND` / `BAD_RESPONSE` / `INTERNAL`。
时间戳一律为 **UTC ISO8601**（如 `2026-10-26T07:33:20.6148965Z`），由前端转本地时间显示。

| 方法 | 路径 | 用途 | 成功码 |
| --- | --- | --- | --- |
| `GET` | `/api/health` | 运行状态检查（同时校验数据库可读） | 200 |
| `GET` | `/api/issues` | 问题列表；支持 `?status=open&priority=high` 过滤 | 200 |
| `POST` | `/api/issues` | Unity 上报新问题 | **201** |
| `GET` | `/api/issues/{id}` | 单条详情（调试/联调用） | 200 |
| `PATCH` | `/api/issues/{id}` | 修改状态，body `{"status":"resolved"}` | 200 |
| `POST` | `/api/issues/{id}/status` | 同上（兼容不愿用 PATCH 的客户端） | 200 |
| `DELETE` | `/api/issues/{id}` | 删除（管理端扩展功能） | 200 |

**创建问题** `POST /api/issues`：

```jsonc
// 请求（只有这 4 个字段由客户端提供）
{
  "title": "1 号机柜温度异常",          // 必填，去首尾空格后非空，≤120 字符
  "description": "机柜右侧温度明显偏高",  // 可选，≤2000 字符
  "priority": "high",                    // low | medium | high（缺省 medium，大小写不敏感）
  "position": { "x": 1.25, "y": 0.8, "z": 2.4 }  // 必填，三个有限数值，绝对值 ≤ 1e5
}

// 响应 201
{
  "ok": true,
  "data": {
    "id": "iss_1793000000614_3a639617",   // 后端生成（时间戳 + 随机后缀，唯一）
    "title": "1 号机柜温度异常",
    "description": "机柜右侧温度明显偏高",
    "priority": "high",
    "status": "open",                     // 后端强制 open，客户端无法指定
    "position": { "x": 1.25, "y": 0.8, "z": 2.4 },
    "createdAt": "2026-10-26T07:33:20.6148965Z",
    "updatedAt": "2026-10-26T07:33:20.6148965Z"
  }
}
```

**参数错误**（`400`）会返回所有出错字段：

| 情况 | 返回 |
| --- | --- |
| 标题为空/全空格 | `fields.title = "title is required and must not be blank"` |
| `priority: "urgent"` | `fields.priority = "priority must be one of \"low\", \"medium\", \"high\""` |
| 缺少/非法 `position` | `fields.position = "position is required, e.g. {\"x\":1,\"y\":0,\"z\":2}"` |
| 字段名拼错（`postion`） | `400 invalid JSON body: json: unknown field "postion"`（**不会静默丢弃坐标**） |
| 状态非法 | `400 fields.status = "status must be one of \"open\", \"in_progress\", \"resolved\""` |
| ID 不存在 | `404 {"ok":false,"code":"NOT_FOUND","error":"issue not found"}` |

**CORS**：允许任意来源（`Access-Control-Allow-Origin: *`，含 `PATCH`/`DELETE` 预检），
因此在开发端口 5173、或把 React 部署到别的域名时浏览器都能直接访问。
> 内网演示够用；上公网请把 `withCORS` 里的 `*` 换成白名单。

**静态托管**：非 `/api/*` 的 GET 请求返回 `web/dist`；带扩展名但不存在的资源返回真正的 404
（不会把 `index.html` 当成 JS 返回，避免 MIME 报错导致白屏）。

---

## 4. 已完成的必做项对照

### 4.1 Unity 客户端

| 题目要求 | 实现 | 位置 |
| --- | --- | --- |
| 基础对象搭建虚拟现场（地面/墙/机柜/管线，≥2 个可点击对象） | 程序化生成机房：地面、4 面墙、8 个机柜 + 8 个面板、3 根桥架、控制台、结构柱、警示区，共 **29 个可点击表面**，每台设备带编号（`CAB-A3`）与区域（`A 区`） | `SceneComposer.cs` |
| 移动并转动视角 | 第一人称巡检员：WASD 平移、右键拖拽转视角、滚轮推拉，限制在场地范围内 | `PlayerController.cs` |
| 射线检测点击设备或表面 | `Physics.Raycast` + 命中表面信息（区域/编号/名称） | `IssuePlacementController.TryPlaceAt` |
| 在实际命中位置放置标记 | 标记的**父节点坐标 = hit.point 原值**；可见球体沿表面法线偏移 0.06m 仅作视觉，上报坐标仍用命中点 | `IssuePlacementController.PlaceAt` |
| 填写并提交（标题必填/描述可选/优先级三档/坐标） | uGUI 表单，提交前校验，坐标只读展示 | `InspectionHUD.cs` |
| 成功或失败明确提示 | 顶部提示条：成功绿色「✔ 上报成功：标题（高，open，ID …）」；失败红色含后端原文，并保留标记与已填内容供重试 | `InspectionHUD.OnSubmitFinished` |
| 点击按钮/输入框不误放标记 | ① 每帧用 `EventSystem.IsPointerOverGameObject()` 置位 `PointerOverUI`；② 输入层在 `PointerOverUI` 时既不追踪点击也不转视角；③ 控制器里再拦一次 | `InspectionInput` + `IssuePlacementController` |
| 一次有效点击最多一个待提交标记，取消后移除 | 有 `Pending` 时再次点击只提示「请先提交或取消」；`CancelPending()` 销毁未保存对象 | `IssuePlacementController` |
| **额外的关键点**：点击 vs 拖拽 | 按下后累计位移 > 6px 或按住 > 0.6s 则不算点击——避免「按住左键拖视角后松手」意外放置标记 | `InspectionInput.UpdateClickDetection` |

### 4.2 React 管理端

- 从后端获取列表（`GET /api/issues`），10 秒轮询刷新（页面隐藏时暂停），可手动刷新。
- 显示标题、优先级、状态、坐标、上报时间、描述。
- 状态下拉框可直接改为 `open / in_progress / resolved`：**乐观更新**，失败自动回滚并在该行显示错误。
- **后端不可用不白屏**：区分「网络不通」「非 JSON 响应（反代错误页）」「HTTP 4xx/5xx」，
  显示错误说明 + 排查建议 + 「重试」按钮。脏数据单条过滤，不会整页崩溃。
- 扩展：状态统计卡片（点击即筛选）、关键字搜索、优先级筛选、详情侧栏（复制坐标）、删除、Toast 提示。

### 4.3 Go 后端

健康检查、接收并持久化、列表、按 ID 改状态、唯一 ID（默认 `open`）、参数错误明确、
重启后数据仍在、Unity/React 同一后端同一份数据、浏览器可访问（CORS + 同源托管）——均已实现，见第 3 节。

**持久化**：SQLite（WAL 模式）。表结构见 `backend/store.go` 的 `migrate`。
时间戳、ID、初始状态全部由后端生成；客户端只能写 `title/description/priority/position`。

---

## 5. AR 基础理解（题第 5 节）

**1）如果把虚拟场景标注改成真实 AR，平面检测和点击放置通常需要哪些 AR Foundation 能力？**

- `ARSession` / `XR Origin`（原 AR Session Origin）：驱动 AR 会话与相机跟踪。
- `AR Raycast Manager` + `ARRaycastHit`：对已检测到的平面、或屏幕空间射线做命中检测；
  点屏幕时优先用 `ARRaycastManager.Raycast()`，命中后把物体放到 `hit.pose.position`。
- `AR Plane Manager` + `ARPlane`：平面检测与可视化（水平/垂直平面）。
- `AR Anchor Manager` + `ARAnchor`：把标注“钉”在真实空间，减少设备移动后的漂移。
- 可选增强：`AR Point Cloud Manager`（无平面时的特征点）、`AR Occlusion Manager`（虚实遮挡）、
  `AR Image Tracking Manager`（对着设备铭牌/二维码定位，比纯平面检测更可靠）。
- 另外要处理设备的 AR 能力检测（`ARCoreLoader` / `ARKitLoader`）与权限申请。

**2）为什么只保存 Unity 中的 x/y/z 坐标，通常不能保证下次启动仍对应同一个真实位置？**

因为 AR 的坐标系是**在每次会话中从当前环境重新建立（localize）出来的**，不是一个全局稳定的参考系：

- 设备重启后，AR 会话会重新初始化，世界原点（origin）可能落在不同位置/朝向上；
- 平面检测和跟踪本身有累计误差与漂移，环境光照、纹理特征不足时误差更大；
- 用户从不同位置/角度开始扫描，建出的地图也不一样；
- 所以同一组 (x,y,z) 在不同会话里对应的真实点可能相差几十厘米甚至更多。

正确做法是保存**相对锚点的引用**：把标注挂到 `ARAnchor`/`ARCloudAnchor`（或地图/Marker）上，
或用图像跟踪、二维码把局部坐标系重新对齐到真实世界，再在其局部坐标系里保存偏移量。
更完整的方案还需要保存会话地图并提供重定位（relocalization）。

**3）AR 初始化失败、设备不支持或 Tracking 状态异常时，应用应如何提示用户？**

- **不支持的设备/未安装 AR 服务**：进入应用前就检测（`ARSession.state == Unsupported`），
  给出一次性说明 + 「仍以普通 3D 巡检模式继续」的入口（本备选题的离线场景正好可作为兜底）。
- **权限被拒**：明确告诉用户需要相机权限，并提供跳转系统设置的按钮。
- **初始化失败 / 会话中断（Interrupted）**：在界面上常驻状态条（如「正在识别环境…」「跟踪已中断」），
  暂停标注操作以免保存错误坐标，并提示「请对准地面缓慢移动设备重新扫描」。
- **Tracking 质量差（Limited）**：提示原因（移动过快/光照不足/纹理不足），并把当前坐标标记为
  “精度受限”，禁止或警告在此时提交标注。
- 原则：**状态可见、操作可恢复、失败可降级**，绝不静默失败或保存来源不可靠的坐标。

> 说明：本备选题不要求实现 AR 功能，以上为设计思路。工程里已把“放置”抽象为
> `PlaceAt(worldPoint, worldNormal, surface)`，将来换成 AR 时只需把射线来源换成
> `ARRaycastManager.Raycast`，其余的上报/校验/UI 逻辑可复用。

---

## 6. 测试与验证

| 范围 | 命令 | 结果 |
| --- | --- | --- |
| Go 后端（含重启持久化、CORS、参数校验） | `cd backend && go vet ./... && go test ./... -v` | `go vet` 无告警；**10 个测试函数 / 18 个用例全部通过** |
| React 管理端（渲染/改状态/回滚/错误页/筛选） | `cd web && npm test` | **7 个测试全部通过** |
| Unity Test Runner（EditMode） | 见下方命令 | **46 个用例全部通过** |

Unity 测试与自检都可以**命令行无人值守**执行（无需人工点 Play）：

```bash
# EditMode 单元测试（46 个用例）
"C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" ^
  -batchmode -nographics -projectPath unity -runTests -testPlatform EditMode ^
  -testResults docs/test-output/unity-editmode-results.xml

# 场景自检：真实构建场景、真实物理射线、真实 HTTP（需先启动 Go 后端）
"C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" ^
  -batchmode -projectPath unity ^
  -executeMethod Inspection.EditorTools.InspectionSceneCheck.RunPlayModeSelfTest
# 结果写入 unity/Logs/selftest.txt，并通过退出码反映成败
```

实测场景自检结果（`docs/test-output/unity-scene-selftest.txt`，全部通过）：

```
PASS  场景构建完成                    可点击表面 29 个
PASS  屏幕中心射线命中表面            Pipe_3 @ (0.00, 0.97, -0.18)
PASS  放置标记成功
PASS  标记父节点坐标 = 精确命中点     (0.000, 0.967, -0.178)
PASS  待提交状态已记录
PASS  取消后移除未保存标记
PASS  后端健康（/api/health 返回 ok:true）
合计：通过 8，失败 0
```

其中“标记父节点坐标 = 精确命中点”这一项是在**真实的 Unity 物理系统**里断言的
（`Physics.Raycast` 命中点与标记 transform 位置误差 < 1e-8），不是模拟值。

`docs/unity-scene-preview.png` 是从 Unity 的 `Camera.Render()` 实拍的场景渲染图
（1280×720，画面中可见机房、机柜与一个红色的高优先级标记）。

`docs/` 目录里附了其他真实运行的证据：

- `docs/backend-curl-session.txt`：健康检查、上报（201）、三类参数错误（400）、改状态（200）、404 的完整请求/响应。
- `docs/backend-restart-persistence.txt`：停止服务 → 重启 → 逐条比对，数据完整保留。
- `docs/admin-dashboard.png`：管理端实际渲染截图（Go 托管的生产构建）。
- `docs/test-output/`：`go test -v`、`vitest`、Unity EditMode 结果 XML、场景自检报告的**原始输出**。

---

## 7. 未完成 / 已知问题 / 取舍说明

### 7.1 已经验证到什么程度

Unity 工程是在本机 **Unity 6000.6.7f1 中真实导入、编译、运行**过的：

| 验证方式 | 结果 |
| --- | --- |
| Unity 导入 + 全量脚本编译 | 通过，**0 error / 0 warning** |
| Unity Test Runner（EditMode） | **46 / 46 通过** |
| 场景自检（真实物理射线 + 真实 HTTP） | **8 / 8 通过** |
| 场景渲染实拍 | `docs/unity-scene-preview.png` |

开发早期在没有 Unity 的情况下，我先用 .NET + 手写桩做了一版验证（工具保留在
`docs/unity-verification/`，69 项断言）。后来的真实 Unity 运行证明这套桩**很有价值但不完备**：

- 它抓到了 `GameObject` 当 `MonoBehaviour` 传参、`IReadOnlyList<T>.Any()` 不可用等真实缺陷；
- 但它**没能发现** Unity 独有的两个问题（见 `AI_LOG.md` P-013）：
  1. 编辑模式下 `Object.Destroy()` 会报错且不生效（标记的 Collider 没被移除、测试里标记没被真正删除）；
  2. EditMode 测试里 `StartCoroutine` 的协程不会推进，必须用 `[UnityTest]` + `yield return null`。
     ——这两类问题只有真实 Unity 环境才暴露得出来。

### 7.2 仍未完成的部分

1. **两段 3–5 分钟演示视频未提供**。本机没有录屏/剪辑工具，也无法代替你完成“人操作 + 解说”的演示。
   第 8 节给了逐条脚本，`F10` 可以稳定复现提交动作，`F9` 可截图。这是本次交付**唯一的硬性缺口**，
   我没有声称已完成。
2. 命令行下只在**编辑模式**做了自检；批处理 + `-nographics` 进入播放模式时，Unity 会卡在
   编辑器自身的 Search 数据库初始化（`ArgumentOutOfRangeException`，属 Unity 内部问题），
   所以“播放模式下的人工交互”是通过第 8 节的脚本由人操作验证的。
3. **未实现 AR Foundation / XR Simulation**：本题为无 AR 设备备选版，AR 部分只要求回答
   README 第 5 节的三问（已完成）。放置逻辑已抽象为 `PlaceAt(worldPoint, worldNormal, surface)`，
   将来接 AR 只需替换射线来源。

### 7.3 其他已知问题与取舍

- 首次用 Unity Hub 打开工程时会生成 `Library/`、`.csproj`（已在 `.gitignore` 中排除）。
- UI 用 `LegacyRuntime.ttf`（Unity 2022.2+ 的内置字体），在更老版本需改为 `Arial.ttf`。
- 材质使用 `Unlit/Color`，不参与光照；标记的可见性优先于画质。
- 历史标记恢复只接受场景范围内的坐标（超出则跳过并在 Console 提示），
  避免把 AR 的大坐标误当成室内坐标。
- 管理端轮询间隔 10 秒，不是实时推送（WebSocket 未做）。
- 无鉴权：内网演示工具定位，未做登录/权限。
- SQLite 单连接串行写入，足以应对单人巡检；高并发写入需要换 PostgreSQL。
- 后端 CORS 为 `*`，便于本地开发；上公网应改为白名单。

**未做的加分项**：AR Foundation XR Simulation、Docker、Delete 之外的批量操作、地图热力图。

---

## 8. 演示视频拍摄脚本（建议）

题目要求两段 3–5 分钟视频，建议这样录（`F10` 可以稳定复现提交动作，`F9` 截图）：

**视频 1：Unity 客户端（3–4 分钟）**

1. 背景与架构 30s：三端关系图（第 1 节）。
2. 打开场景 → Play，展示后端已连接（左下角绿色）。
3. 用 WASD + 右键拖拽在机房中走动，展示不同区域（约 1 分钟）。
4. 对着某个机柜左键点击 → 标记出现在**点击的那个点**上（可先拖拽转视角再点，证明不会误放）。
5. 填写标题/描述、选「高」优先级，展示坐标，点提交 → 绿色「上报成功」提示。
6. 按住左键**拖拽**松手 → 不会放置标记（对比演示）。
7. 停掉 Go 服务后点提交 → 红色失败提示（含原因），标记与内容保留；重启服务后重试成功。
8. 按 `F5` 或重启 Unity 再 Play → 历史标记按坐标恢复（加分项）。

**视频 2：React 管理端 + 持久化（2–3 分钟）**

1. 打开 http://localhost:8080 ，展示刚上报的问题出现在列表顶部（可点刷新）。
2. 展示统计卡片、筛选、搜索、详情侧栏。
3. 把状态改为「处理中」→ 提示成功；把状态改回或改为「已解决」。
4. 停掉 Go 服务 → 页面显示错误页（不白屏）→ 重启服务 → 点重试恢复。
5. 强调重启后列表数据、状态、坐标全部一致（对应 `docs/backend-restart-persistence.txt`）。

---

## 9. 目录结构

```
holocode-inspection/
├── backend/                 Go 后端（module: .../backend）
│   ├── main.go              入口、flag/环境变量、优雅退出
│   ├── model.go             Issue / Position / 枚举
│   ├── validate.go          参数校验（字段级错误）
│   ├── store.go             SQLite 持久化（WAL，ID 生成）
│   ├── server.go            路由与 handler、统一错误信封
│   ├── middleware.go        CORS、访问日志
│   ├── static.go            SPA 静态托管
│   └── server_test.go       14 个测试（含重启持久化）
├── web/                     React + TypeScript 管理端（Vite）
│   └── src/
│       ├── api/{types,client}.ts   类型守卫、fetch 封装、错误分类
│       ├── components/              表格、状态/优先级徽章、详情侧栏
│       ├── App.tsx                  列表、轮询、乐观更新、错误页
│       └── App.test.tsx             7 个组件测试
├── unity/                   Unity 6.6 LTS 工程
│   └── Assets/
│       ├── Scenes/Inspection.unity  只挂 SceneBootstrap 的空场景
│       ├── Scripts/
│       │   ├── Core/                IssueData / IssueApi / JsonScan
│       │   ├── Player/              PlayerController / InspectionInput
│       │   ├── World/               SceneComposer / IssueMarker
│       │   ├── Placing/             IssuePlacementController（核心状态机）
│       │   ├── UI/                  InspectionHUD（代码构建 uGUI）
│       │   ├── Diagnostics/         截图 / 自动演示 / 自检（F9/F10/F11）
│       │   └── Bootstrap/           SceneBootstrap（一键装配）
│       └── Tests/EditMode/          NUnit 单元测试
├── docs/                    运行证据：API 记录、持久化比对、截图
├── AI_LOG.md                AI 使用记录
├── README.md
├── start.bat / start.sh     一键启动
```

