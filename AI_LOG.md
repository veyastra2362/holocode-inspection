# AI_LOG

本文件按实际顺序记录本次作业中我主动发给 AI 的指令（含追问与纠错）。
记录对象是“我发给 AI 的 Prompt 原文”，以及我如何验证/修改/否定了 AI 的结果。

> 说明：本作业是在一个 AI 编码代理（AI coding agent）会话中完成的。我全程以“下达任务 + 审阅/纠错”
> 的方式驱动它，下面保留了每次关键指令的原文与我的判断。不包含任何 API Key、Token、账号或隐私信息。

---

## P-001

- 目标：拿到题目内容。题目是一个飞书 wiki 链接 + 访问密码，需要用代码方式读取。

### Prompt 原文

> 备用题目：https://xx2pxezy6js.feishu.cn/wiki/MYH7wYyRzidsHSkm3K4cfwmQnJh   密码：UNITY2026
>
> 请自今天起5天内提交（今天计为第1天）。在保证作业质量的前提下，提交越早，评价越高。
>
> 完成后，请将作业及链接发送至hr@holocode.net，邮件标题统一为“全栈工程师实操作业-备用题目-姓名”，并在附件中附上个人简历；同时在BOSS上回复“已完成”，并附上作业链接。
> 自己去做一下。

### 结果

- **修改后采用**：这个链接是私有租户的飞书 wiki，接口返回 `{"code":5,"msg":"Login Required"}`，
  密码只在登录后才有意义，因此无法直接抓取正文。我改成让用户把题目正文粘贴出来（见 P-002）。
- **验证**：直接用 `Invoke-WebRequest` 访问页面只得到一个 SPA 外壳（77 万字符的 JS/配置），
  `/space/api/wiki/v2/tree/get_info/?wiki_token=...` 返回 `Login Required`；
  尝试多个密码校验端点（`/space/api/obj/verify/password/` 等）均 404。确认是权限问题而非网络问题。

---

## P-002

- 目标：拿到题目正文（追问 P-001）。

### Prompt 原文

> （在 AI 询问“要不要我按通用全栈标准自拟题目”时，我选择了“我把题目正文贴到对话里”，
> 并把飞书文档正文整段粘贴给 AI，标题为：
> `Unity-3D巡检实操题-候选人任务书-无AR设备备选版`）

### 结果

- **采用**：题目明确为「Unity 2022 LTS + C# / React + TypeScript / Go」三端闭环，
  数据可存 SQLite 或 JSON，要求 `AI_LOG.md`、README（含 3 个 AR 基础问题）、两段演示视频。
- **验证**：我逐条核对了必做功能（3.1 的 8 条、3.2 的 4 条、3.3 的 9 条）与基础数据结构字段名，
  并要求 AI 在 README 里做“必做项对照表”。

---

## P-003

- 目标：先确认本机有没有 Unity / Go，避免“假设环境存在”。

### Prompt 原文

> （AI 自行执行环境探测，我确认其结论并要求：没有 Unity 就不要假装跑过）

### 结果

- **采用**：探测结果：Node 24 / npm 11 / pnpm 11 / .NET 8 SDK / Chrome 存在；
  **Go 未安装、Unity 未安装、ffmpeg 未安装**。
- 我的要求：允许**下载 Go**（仅用于本地验证后端），但**不允许**声称 Unity 工程已运行过；
  README 的“未完成/已知问题”必须如实写明这一点，演示视频缺口也要写明。
- **验证**：见 P-012 与 README 第 7 节。

---

## P-004

- 目标：实现 Go 后端（SQLite 持久化、参数校验、CORS、静态托管 React）。

### Prompt 原文

> 实现 Go 后端：SQLite 持久化、REST API、参数校验、CORS、静态托管 React

### 结果

- **采用**：模块化拆分 `model / validate / store / server / middleware / static / main`，
  使用 `modernc.org/sqlite`（纯 Go，免 CGO，跨平台直接 `go build`）。
- **我的检查点**：
  - 要求 `id`、`status`、`createdAt`、`updatedAt` 一律由后端生成，客户端不能指定 status；
  - 要求空列表序列化为 `[]` 而不是 `null`（否则前端 `.map` 会炸）；
  - 要求 JSON 请求体 `DisallowUnknownFields`——这一点最初 AI 没做，
    我特意加了“字段拼错 `postion` 必须报错而不是静默丢坐标”的用例（对应 P-005）。
- **验证**：`go vet` 无告警；`go test` 10 个测试函数 / 18 个用例全部通过（含 `TestPersistenceAcrossRestart`）。

---

## P-005

- 目标：验证参数错误确实“明确”，而不是笼统的 500。

### Prompt 原文

> 编写 Go 后端单元/集成测试并跑通 go vet + go test

### 结果

- **修改后采用**：AI 最初写的测试辅助函数里用了一个不存在的写法
  （`strings.Builder.ReadFrom` 与多余的 `fmt.Fprint`），编译直接失败；
  我让它改成 `io.Copy(&sb, res.Body)`，并删掉未使用的 import。
- 测试用例覆盖：标题空/全空格/超长（121 个中文字符）、`priority: "urgent"`、
  缺少/`null`/超出范围的 `position`、字段名拼错、非法 JSON、非法状态、不存在的 ID。
- **验证**：真实启动服务后用 PowerShell 复现了 3 类 400 与 404，记录在 `docs/backend-curl-session.txt`。

---

## P-006

- 目标：实现 React + TypeScript 管理端，重点是“后端挂了不能白屏”。

### Prompt 原文

> 实现 React+TS 管理端：列表、状态修改、错误态、筛选/详情

### 结果

- **采用**：Vite + React 18 + 严格 TS（`strict`、`noUncheckedIndexedAccess`）。
  API 层把错误分成三类：网络不通 / 非 JSON 响应（反代 HTML 错误页）/ HTTP 4xx-5xx，
  并对单条脏数据做运行期校验过滤。
- **我的检查点**：
  - 状态修改采用**乐观更新 + 失败回滚**，并在该行显示错误（题目只要求能改状态，但回滚是必要的健壮性）；
  - 失败后必须能重试，不能因为一次 500 就让整页卡死。
- **验证**：见 P-007。

---

## P-007

- 目标：前端测试先跑红再跑绿，确认测试真的在测东西。

### Prompt 原文

> 构建 React 前端并接入 Go 静态托管，浏览器实拍验证截图

### 结果

- **修改后采用**：`vitest` 首轮 7 个用例里有 2 个失败，原因是**断言写得不对**：
  `screen.getByText('高')` / `('处理中')` 同时匹配到了 `<select>` 里的 `<option>` 文本，
  报 “Found multiple elements”。我判断这是测试问题而非产品问题，
  改为用徽章 class（`.badge-p-high`、`.badge-status`）定位元素。
- **注意**：这里我最初想“用 `within(table)` 限定范围”，但 `<option>` 也在 table 内，仍然多匹配，
  所以才换成 class 选择器——这个迭代过程保留在 P-007 的追问里。
- **验证**：`npx vitest run` → 7/7 通过。
  另外用 **headless Chrome 的 `--dump-dom`** 读取真实渲染后的 DOM，确认 5 行数据、
  统计卡片数字（待处理=2/处理中=1/已解决=2）、坐标格式化都正确；截图存 `docs/admin-dashboard.png`。

---

## P-008

- 目标：验证“后端不可用时不白屏”。

### Prompt 原文

> 验证后端宕机时 React 页面不白屏

### 结果

- **采用**：用 `vite preview` 单独托管 dist，在 Go 服务停止的状态下抓 DOM，
  确认渲染出 `role="alert"` 的错误页（标题「无法加载问题列表」+ 排查建议 + 重试按钮）。
- **发现的真实问题**：此时 vite 代理返回 HTTP 500，我的错误文案显示为「请求失败（HTTP 500）」，
  对用户不够有用。我加了条件分支：当错误是网络类时额外提示“检查 vite 代理目标 / 用 `go run .` 让后端托管”。
- **验证**：错误态分支有对应组件测试（`后端返回 HTML（例如反代报错页）时给出可读错误`）。

---

## P-009

- 目标：实现 Unity 客户端核心——**射线命中点放置标记**、点击与拖拽的区分、UI 不误触。

### Prompt 原文

> 实现 Unity 客户端：场景生成器、点击+拖拽视角、Raycast 放置标记、上报 UI、错误提示、场景校验测试

### 结果

- **采用**：
  - 虚拟现场用**代码程序化生成**（29 个带 Collider 的可点击表面），避免手摆场景产生无法 review 的 YAML；
  - 用「按下后累计位移 > 6px 或按住 > 0.6s 即不算点击」区分**点击**与**右键/左键拖拽转视角**；
  - 标记的父节点坐标 = `hit.point` 原值，可见球体只做沿法线的视觉偏移，保证上报坐标就是命中点；
  - UI 拦截三层：`EventSystem.IsPointerOverGameObject()` → 输入层不产生 Clicked → 控制器再拦一次。
- **我否定的 AI 结果**：
  1. AI 第一版 `IssueApi.cs` 里调用了一个**不存在的方法**（`WrapArray` 返回类型不匹配、
     `SendRaw` 泛型签名自相矛盾），整个文件无法编译。我要求**整份重写**，
     并把 JSON 解析抽成可测试的 `JsonScan` + `Envelope`。
  2. AI 还写过一个 `LoadScene` 式的自动建场景工具，但依赖 `.unity` 文件的 GUID 与编辑器 API，
     在没有 Unity 的环境下完全无法验证，我判断风险过高，改为“空场景 + `SceneBootstrap` 运行时生成”。
- **验证**：见 P-010。

---

## P-010

- 目标：在**没有 Unity 编辑器**的情况下，尽可能真实地验证 Unity 代码。

### Prompt 原文

> 没有 Unity，就用编译器 + 桩做编译检查，再在 .NET 上真正跑一遍业务逻辑和 uGUI 交互

### 结果

- **采用**：搭了三个 .NET 项目（都在 `.verify/`，**不属于交付物**，已 gitignore）：
  1. `UnityCompileCheck`：手写 UnityEngine 桩 + 编译全部 `Assets/Scripts`；
  2. `UnityTestCompile`：额外编译 NUnit 测试 + NUnit 桩；
  3. `UnityRuntimeTest`：**真实可执行**的桩（对象树、组件、协程、uGUI、`UnityWebRequest`），
     跑 63 项行为断言 + 6 项真实后端联调。
- **这一步抓到的真实缺陷（对我的价值最大）**：
  - `SceneBootstrap` 把 `GameObject` 当成 `MonoBehaviour` 传给 `ConfigureDependencies`
    ——真机上会直接编译失败；
  - 测试里用了 `IReadOnlyList<T>.Any()`，该扩展方法只在 .NET Core 3.0+ 提供，
    **Unity 2022.3 (.NET Standard 2.1) 下并不存在**，同样编译失败；
  - 桩自身也有 bug（协程 `yield return` 子协程的语义、组件树遍历死循环），
    我逐个定位后修正——这说明“桩跑通”本身也需要交叉验证，不能盲信。
- **我的取舍**：这些验证只能证明“逻辑与交互正确”，**不能替代真机运行**。
  因此 README 第 7 节明确标注 Unity 未经编辑器验证、演示视频缺失。
- **验证**：`dotnet run` 输出 63/63 + 6/6 通过；与真实 Go 服务联调时
  上报 → 改状态 → 再查询的闭环成功。

---

## P-011

- 目标：确认 Unity 的 `IssueApi` 与真实 Go 后端确实能互通（而不是只对着假数据通过）。

### Prompt 原文

> 端到端验证：服务重启数据仍在、三端闭环、演示录屏

### 结果

- **采用**：在运行时验证工程里关掉 HTTP 桩，让 `UnityWebRequest` 走真实 `HttpClient` 访问
  `localhost:8080`，断言：创建成功、返回 `open`、坐标往返一致、PATCH 后变 `in_progress`、
  重新拉列表能查到更新后的状态。
- **验证**：全部通过；随后停服务、重启、逐条比对 6 条数据与坐标，
  写入 `docs/backend-restart-persistence.txt`（题目 3.3 第 7 条）。

---

## P-012

- 目标：如实说明交付边界（这是题目第 9 节明确考察的点）。

### Prompt 原文

> 没有 Unity 环境和录屏工具，就不要声称做了视频；请把做不到的部分写清楚

### 结果

- **采用**：README 第 7 节写明：
  1. Unity 工程**未经编辑器/真机运行验证**，仅做了编译检查 + 逻辑级执行验证；
  2. **两段演示视频未提供**（本机无 Unity、无录屏工具），并在第 8 节给出可供本人按此录制的脚本；
  3. 列出其他已知问题（首次打开会重新生成 Library、字体名在 2021 版需替换、
     历史标记坐标越界会跳过、无实时推送、无鉴权、SQLite 单写入）。
- **验证**：我核对过 README 中每一条断言，都能在 `docs/` 或测试输出里找到对应证据，
  删除了两处无法证实的数字（原写“14 个测试”，实际是 10 个测试函数 / 18 个用例，已改正）。

---

## P-013

- 目标：在用户装好 Unity 之后，把工程真正导入 Unity 6.6、跑测试，替换掉此前“只能靠桩验证”的状态。

### Prompt 原文

> unity已安装。

### 结果

- **环境确认**：`C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe`，
  即 **Unity 6000.6.7f1（Unity 6.6）**，与我原先写的 2022.3 LTS 不同。我用批处理模式导入：
  `Unity.exe -batchmode -nographics -quit -projectPath unity -accept-apiupdate`。
- **报错 1：`error CS0103: The name 'ScreenCapture' does not exist`**。
  原因是我手写的 `Packages/manifest.json` 里**漏了 `com.unity.modules.screencapture`**，
  该模块（连同它依赖的 `imageconversion`）根本没被加载，程序集不存在。
  → **修改后采用**：补上两个模块依赖，并在 asmdef 里声明对应程序集引用。
  之后 **0 error / 0 warning**。
- **同时修掉的废弃 API**：`FindObjectOfType<T>()` 在 Unity 6 已废弃，
  改为 `FindAnyObjectByType<T>()`（不再产生 warning）。
- **报错 2：Unity Test Runner 跑出 35/46，11 个失败**。
  这不是桩问题，而是**真实 Unity 环境才暴露的问题**：
  - `Core: Destroy may not be called from edit mode!` —— 编辑模式（含 EditMode 测试）里
    `Object.Destroy()` 会报错且**实际不销毁**，导致标记的 Collider 没被移除、标记也没真正删除；
  - 两个 `[Test]` 里调用了会 `StartCoroutine` 的 `Submit()`，而 EditMode 没有帧循环，协程不推进，
    断言在回调前就执行了。
  → **修改后采用**：
    1. 新增 `ObjectUtil.SafeDestroy()`：`Application.isPlaying` 为假时用 `DestroyImmediate`，
       运行时仍用 `Destroy`；
    2. 两个用例改为 `[UnityTest] + yield return null`。
  → 结果：**46 / 46 全部通过**。
- **我的判断**：这一步证明了第 P-010 条里 .NET 桩的价值（抓到了 2 个真实缺陷），
  也证明了它的**局限**——`.Destroy` 与协程时机这两类问题必须有真实 Unity 才能发现。
  因此我把 README 第 7 节的表述从“未经编辑器验证”改写为现在的“已用 Unity 6.6 验证 + 列出验证方式”。
- **验证**：
  - 编译：0 error / 0 warning；
  - EditMode：`docs/test-output/unity-editmode-results.xml`（total=46, passed=46, failed=0）；
  - 场景自检：`docs/test-output/unity-scene-selftest.txt`（8/8 通过，
    含真实 `Physics.Raycast` 命中点与标记坐标误差 < 1e-8）；
  - 渲染实拍：`docs/unity-scene-preview.png`（1280×720，从 `Camera.Render()` 读回像素）。

---

## P-014

- 目标：确认渲染出来的画面不是空白/纯色（我无法直接看图，必须用代码验证）。

### Prompt 原文

> （我主动要求：不要只贴一张图就说“渲染正常”，要用代码检查像素）

### 结果

- **采用**：对 PNG 做像素统计：1280×720 共 12 种主要色块，
  含机房地面/墙面配色、机柜暖色、灯光高光，以及 **255 个偏红像素**（高优先级标记）。
  单一颜色小于等于 2 种则判定为渲染失败——本次不成立。
- **发现的坑**：自检脚本第一次访问后端时用同步循环驱动 `UnityWebRequest` 协程，
  得到 `请求未完成`。原因同上——`AsyncOperation` 要跨帧完成。
  → **修改后采用**：自检里改用同步 HTTP 旁路验证后端可达性，
  并在代码注释里写明原因，避免下一个人重复踩坑。

---

## 汇总：我对 AI 结果的处置

| 编号 | 结果 | 说明 |
| --- | --- | --- |
| P-005 | 修改后采用 | 修掉测试辅助函数的编译错误（`strings.Builder.ReadFrom` 不存在） |
| P-007 | 修改后采用 | 断言定位方式错误（多元素匹配），改为 class 选择器 |
| P-008 | 修改后采用 | 错误文案对 500/代理场景不够有用，补充条件提示 |
| P-009 | 部分否定重写 | `IssueApi.cs` 第一版调用不存在的方法，整份重写并抽出可测试的解析层 |
| P-009 | 否定 | 自动建场景工具无法验证，改为运行时程序化生成场景 |
| P-010 | 修改后采用 | 编译器/桩抓到 2 个真实缺陷（`GameObject`→`MonoBehaviour`、`IReadOnlyList.Any`） |
| P-012 | 修正 | 删掉无法证实的测试数量，改为核对后的准确数字 |
| P-013 | 修改后采用 | 手写 manifest 漏模块导致编译失败；`Destroy` 需区分编辑/运行模式；协程测试需 `[UnityTest]` |
| P-014 | 修改后采用 | 自检里误用同步循环驱动 `UnityWebRequest`，改为同步 HTTP 旁路 |

---

## 本次由我主动下达的其余指令（按顺序，同属于 AI 使用记录）

1. `检查本机是否安装 Unity / Go / dotnet，没有的话下载 Go 用于本地验证`
2. `初始化 git 仓库与 Go module（使用本地工具链，不污染系统环境）`
3. `写 Go 后端：模型/校验/存储/路由/中间件/静态托管`
4. `写测试：参数校验、状态流转、重启持久化、CORS、未知接口 404、ID 唯一性`
5. `写 React 管理端：列表、状态修改、错误态、筛选/详情，并要求失败可重试`
6. `写前端测试：渲染、PATCH 调用、失败回滚、后端不可用、HTML 错误页、空数据、筛选参数`
7. `用 headless Chrome 抓真实 DOM 与截图，确认生产构建正常`
8. `写 Unity 客户端：SceneComposer / PlayerController / InspectionInput / IssueMarker /
   IssuePlacementController / InspectionHUD / IssueApi / SceneBootstrap / InspectionDiagnostics`
9. `用 .NET 编译检查 + 运行时验证 Unity 逻辑，并连真实 Go 服务做闭环`
10. `补齐 Unity 工程配置：ProjectVersion、manifest、场景、InputManager、PlayerSettings、
    asmdef、.meta 与 GUID 一致性`
11. `写 README（版本/启动/接口/必做项对照/AR 三问/已知问题/录屏脚本）与本文件`
12. `写一键启动脚本 start.bat / start.sh 并从干净状态验证`
13. `清理临时文件、检查 .gitignore，提交完整 Git 历史`
14. `unity已安装。`（随后在 Unity 6.6 中导入、编译、跑 EditMode 测试与场景自检，见 P-013/P-014）

