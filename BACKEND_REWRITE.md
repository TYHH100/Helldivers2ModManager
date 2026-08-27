# 后端重写计划（backend-rewrite 分支）

> 本文档是后端重写工程的固定基准。模块划分、设计决策、里程碑与验收标准以本文为准；
> 执行过程中如需偏离，先修改本文并说明理由，再动代码。

## 一、目标与总原则

- **新代码先住进 `next/` 文件夹**：新建 `Helldivers2ModManager.Core` 类库（net10.0，零 WPF 引用）
  与配套新测试项目 `Helldivers2ModManager.Core.Tests`，全部在 `next/` 下开发。
  旧后端全程不动，主程序始终可编译运行。
- **借鉴而非照抄**：以现有实现为行为基准，按模块职责重新组织；现有算法
  （材质变体去重、纯黑占位过滤、模糊匹配、MeshSelector 等）连同其守护测试一起迁移。
- **验收门**：全部模块完成 → 自动化测试全绿 + 人工验收清单通过 → 才允许删除旧代码、
  把新代码移出 `next/`。
- **硬约束全部继承**：仓库根 `AGENTS.md` §4/§5 的有界读取、64 位偏移校验、并发上限集中管理、
  取消语义、部署内核态复制等规则在新代码中强制执行并配测试。
- **稳扎稳打**：按里程碑推进，每个里程碑完成后暂停给用户过目再继续；不做一步到位的赌注。

## 二、现状诊断（重写动机的事实依据）

| 问题 | 实际情况 |
|---|---|
| 巨型单体 | `VersionCheckService` 横跨 8 个文件的 partial 类（约 6000 行），一个类承载检测、4 种修复、备份、伴生恢复、游戏归档读取 |
| 格式三重复制 | Patch TOC 二进制格式（magic `0xF0000011`、72B 头、32/80B 条目）在 VersionCheck / ResourceInspection / TypeDetection 三处各实现一遍 |
| UI 反向依赖 | 服务层直接使用 `ObservableCollection` / Dispatcher / Messenger；`ModService` 内部创建并缓存 `ModViewModel`；版本检测结果持久化由 ViewModel 直接操作 Repository |
| 隐式初始化链 | `ModService.Init()` → `ModHashService.Init()` 等延迟绑定反模式打断循环依赖 |
| 扁平结构 | 约 40 个服务文件平铺在单个 `Services/` 目录，无模块边界 |

可继承的资产：Models 层大部分为纯数据/算法（ModelPreview 系列约 20 个守护测试）；
SQLite 持久化层模式统一（WAL + 每操作一连接 + 写串行）；PatchTool 是旧后端的第二个消费者
（InternalsVisibleTo 强耦合，本阶段不改造）。

## 三、模块划分

新类库内部按模块分目录 + 命名空间。依赖方向单向：
`Common ← PatchKit ← GameData/Mods ← Deployment/Repair ← …`，禁止反向依赖和循环。

| 模块 | 职责 | 吸收的旧代码 |
|---|---|---|
| `Common` | Result/错误码模型、路径安全守卫、并发工具（限流/AsyncKeyedLock）、后台任务抽象（后台线程执行 + 状态机生命周期 + 步骤列表）；**任务状态机内部以单消费者串行有序管道处理所有状态迁移**，保证步骤更新先于终态落地（见设计决策 4；UI 可观察集合皮留在主应用适配层） | BackgroundTaskService 核心（含 QueueOnUiThread 终态排队语义的机制化替代） |
| `PatchKit` | 补丁二进制格式的**唯一实现**：TOC 读写、gpu_resources 有界随机读取、stream LZ4 解码、TypeId 常量表、内存上限集中在统一 Options | VersionCheckService / PatchResourceInspectionService / ModTypeDetectionService 中三份重复格式实现合并 |
| `GameData` | 游戏归档访问：DSAR 分块索引 + BundleInfo 复用只读流、Unit 参考索引构建与解析、伴生文件重建配方 | GameUnitReferenceReader、GameCompanionRecoveryReader |
| `Mods` | 清单（Legacy/V1，**格式严格兼容**，改用 System.Text.Json 源生成替代手写序列化）、目录/压缩包导入、增量更新、删除（回收站）、类型检测自动打标、SHA-256 哈希生命周期、**Mod 导出**（ZIP / 7z 多压缩等级 Fast~Ultra，含大字典内存警告、排除过滤、进度与速度上报） | ModService、ModHashService、Manifest 系列、ModOption 系列、ModTypeDetectionService、FileHashUtils；DashboardPageViewModel 中的导出实现（`ExportMod`/`DoExportAsync` 等，原属 ViewModel 层的孤儿功能） |
| `Deployment` | 部署计划与顺序、staging 目录、有限并发内核态复制 / 符号链接策略、Purge 清理、二分排查支撑 | ModService.Deploy/Purge、DeploymentOrderHelper、BisectService（含 BisectCore） |
| `Profiles` | 启用状态快照捕获/应用、防抖保存协调（串行写入队列 + Flush） | ProfileService、ProfileSaveCoordinator、EnabledDataRepository |
| `Groups` | 分组管理与成员状态 | ModGroupService、ModGroupRepository |
| `Versioning` | 版本检测（纯检测，无修复）：批量/单 Mod 检查、参考版本解析、补丁结构分析、三层缓存语义 | VersionCheckService 主分部的检测职责 |
| `Repair` | 从单体拆出的显式协作服务：元数据修复 / 协助修复（LOD 与父模板迁移规则表）/ 伴生恢复 / 备份库（`.hd2mm-backup` 元数据、恢复、清理）/ 批量编排；锁约定显式化 | VersionCheckRepairService、VersionCheckAssistedRepairService、VersionCheckCompanionRecoveryService、VersionCheckBackupService、VersionCheckBatchRepairService |
| `Analysis` | 冲突扫描缓存键、护甲污染分析、护甲复用分析 | ModConflictService、ArmorReuseService、ModConflictRepository |
| `Preview` | 资源检查（资源查看器）、MeshInfo/材质变体去重、纯黑占位材质过滤、BCn 纹理解码与通道统计、动画解析器（从 Models 迁入）、D3D11 蒙皮（Vortice） | PatchResourceInspectionService、ModelPreviewBackend、ModelPreview* 算法模型（**不含** `ModelPreviewViewportGuides`——Media3D 类型属主应用展示层）、GpuSkinningService |
| `Nexus` | API 客户端（重试/超时/API Key）、更新组版本、缓存 | Services/Nexus 整体平移改造 |
| `Search` | 拼音/模糊匹配纯函数 + 过滤管道；输入输出改为领域模型，不再接触 ViewModel | FuzzySearchMatcher、SearchFilterService |
| `Persistence` | SQLite WAL 连接层 + 重设计的表结构（不要求兼容旧库） | DatabaseService 及各 Repository |
| `Configuration` | 设置纯 POCO 模型（剥离 ObservableCollection）+ 引导配置（exe 目录最小启动项：存储目录/语言/主题/DPAPI 加密密钥），STJ 源生成序列化 | SettingsService |
| `Localization` | 仅定义 `IStringCatalog` 抽象；后端一律返回错误码/结果对象，不认识任何语言资源 | — |

## 四、关键设计决策

1. **显式模块注册取代反射扫描**：每个模块暴露 `AddXxx(this IServiceCollection)` 扩展方法，
   App 显式组合调用。日后新增功能模块 = 新文件夹 + 一个 Add 方法 + 接口契约。
   废除 `[RegisterService]` 程序集扫描机制。
2. **构造注入完整依赖**：所有服务在构造函数中拿到完整依赖，废除 `Init()` 延迟绑定反模式；
   生命周期分显式阶段（bootstrap → configure → start）。
3. **Core 零 WPF 类型 + 展示包装拆分模式**：禁止出现 ObservableCollection / Dispatcher /
   Messenger / ViewModel；进度通过接口回调报告。凡旧代码中领域与 UI 混合的类型，
   一律拆成「Core 领域模型 + 主应用展示包装」两半：`ModTag`（Core 存颜色字符串，
   包装层转 Brush）、`ModGroup.ModGuids` 等集合（Core 用普通集合，包装层可观察化）、
   任务条目三件套（ObservableObject 皮留主应用）、`ModelPreviewViewportGuides`
   （Media3D 几何辅助整体留主应用）。
4. **后台任务顺序语义**（继承 QueueOnUiThread 教训）：终态（Complete/Fail/Cancel）不得同步
   抢占仍在管道中的步骤更新。Core 任务状态机内部以单消费者串行有序管道处理全部状态迁移与
   步骤事件；适配层只负责把事件编组到 UI 线程，不参与排序决策。
   （原缺陷：符号链接部署瞬间完成时，BeginInvoke 排队的步骤更新被终态守卫拦截，
   弹窗永远显示"正在部署"。）
5. **本地化**：语言资源从手解析 JSON 改为 **.resx + 卫星程序集**（.NET 原生多语言机制，
   编译期强类型属性访问器，文化回退由框架处理）。运行时切语言保留：设置
   `CultureInfo.CurrentUICulture` + 绑定刷新层适配现有 `LocExtension`。
   后端只见错误码枚举，前端做错误码 → 文案映射（需建立一份对照表）。
   范围声明（用户已确认的解释）："不用 JSON"仅指语言资源体系；清单格式因兼容承诺必须保持
   JSON 格式、引导配置小文件采用 STJ 源生成 JSON——两者均为 .NET 内置机制，无第三方依赖。
6. **数据兼容范围（用户已确认）**：仅 `manifest.json` 清单格式无损兼容（Legacy/V1 行为不变）。
   ⚠️ 明确后果：`settings.json` 与 `mod_manager.db` 重新设计后，老用户的设置、分组、启用状态、
   哈希缓存**不会自动迁移**，首次启动相当于全新初始化。如日后需要迁移，
   可追加一次性迁移器，不阻塞主线。
   **兜底约束**：无论是否迁移，首次启动**绝不删除或覆写**旧 `settings.json` 与
   `mod_manager.db`，保留现场以便日后补迁移器或用户手工恢复。
7. **持久化重设计**：单一 SQLite 数据库承载全部状态（profiles/groups/hashes/version results/
   conflict cache/nexus cache/preferences）；引导配置单独放 exe 目录最小文件。
8. **资源读取硬约束**：有界随机读取、`long`/`ulong` 偏移溢出检查、内存上限集中到统一 Options、
   并发数集中定义（`Clamp(ProcessorCount/2, 2, 4)` 类规则不再散落复制）。
9. **部署性能基线继承**：收集文件对后 `Parallel.ForEachAsync` 限并发 + 内核态
   `File.Copy(..., true)`；手动流缓冲统一 81920；符号链接仅在明确开启且权限满足时使用。

## 五、里程碑

每个里程碑完成后暂停验收，再进入下一个。

- [x] **M0 骨架**：`next/` 目录、两个新项目加入 sln、模块注册机制、Common 原语
      （Result/错误码骨架、路径守卫、并发工具、任务抽象接口）、
      Core 向测试项目开放 `InternalsVisibleTo("Helldivers2ModManager.Core.Tests")`。
      验收：解决方案构建通过。
- [x] **M1 PatchKit**：格式层唯一实现。验收：建立**差分 harness**——同一批 `Test\` 真实样例
      分别跑新旧解析器，程序化比对 TOC 条目/GPU 结构/stream 边界的结构化输出一致（只读样例）。
- [x] **M2 GameData**：归档索引、Unit 引用解析、伴生配方。验收：并入同一差分 harness，
      Unit 引用解析输出与旧版逐字段一致。
- [x] **M3 Persistence + Configuration**：新库建表/读写、设置 POCO、引导配置。
      验收：存储层单元测试通过。
- [x] **M4 Mods 域**：清单兼容重点。验收：旧清单 fixture 测试全部移植并通过；
      导入/更新/删除/检测/哈希行为对齐。
- [x] **M5 Deployment + Profiles + Groups**。验收：部署/回收站/防抖保存行为对齐。
- [x] **M6 Versioning + Repair**（最大块）：拆解 6000 行单体。
      验收：检测/四种修复/备份/伴生恢复/批量编排逐项对照旧逻辑；
      材质迁移规则表（0x54AE→0x8F66 等）与 LOD 分类判定测试移植通过。
- [x] **M7 Analysis**。验收：冲突扫描与护甲复用结果与旧版一致。
- [x] **M8 Preview 全家桶**。验收：ModelPreview 约 20 个算法守护测试移植通过；
      资源检查/纹理解码/动画解析回归。
- [x] **M9 Nexus + Search + Localization 资源体系**。验收：API 客户端回归、拼音匹配回归、
      resx 双语言（zh-CN/en-US）覆盖完整、错误码对照表建立。
- [x] **M10 切换**：主应用适配层接线（薄 Facade 让现有 ViewModel 机械式改造；
      原 `GetOrCreateModViewModel` 的 identity-map 缓存移入适配层持有，避免重复创建实例）→
      **切换完整性校验**：用 `rg` 枚举旧服务全部公开成员的消费点，逐条确认已有新路径或
      显式放弃 → 自动化全绿 + 用户人工验收 → 删除旧 `Services/` 等旧后端代码 →
      新代码移出 `next/` 到正式位置 → 移除反射注册机制。
      验收：发布构建 + 实机跑通导入/部署/检查/修复/预览/导出全流程。

## 六、切换期边界（已知且接受）

- **Helldivers2PatchTool 暂不改造**：删除旧代码后其引用的类型消失，将无法编译。
  M10 时把该工具临时移出解决方案构建并在分支注明，待日后专项适配。
  Purger 不受影响。
- **前端本次不动**：主应用 ViewModel 通过适配层消费新 Core；彻底的前端重构是后续独立工程。
- 测试样例库位于仓库根 `Test\`（本地资产，不入库），Fixture 测试不可在无样例环境复现；
  新测试项目沿用相同的 fixture 定位方式。

## 七、测试策略

- 新测试项目 `next/Helldivers2ModManager.Core.Tests`（MSTest），与旧测试并存直到 M10。
- 迁移守护测试优先级：清单解析系列 > ModelPreview 算法系列 > 材质迁移/LOD 分类 > 
  BisectCore / FuzzySearchMatcher / FileHashUtils / MultiSelect。
- Fixture 集成测试用 `Test\Mods\Mods\` 真实补丁只读验证，禁止修改样例。
- 差分 harness（M1 建立，M2+ 复用）：新旧解析器对同一输入的结构化输出比对工具，
  作为行为对拍的自动化手段，随里程碑扩展覆盖 Unit 解析、修复计划等输出。
- 每个模块至少覆盖：正常路径、截断/越界等异常输入、取消语义、并发上限。
- 构建注意：涉及共享 obj 时串行构建；不要用 `--no-build` 验证生成代码。

## 八、人工验收清单（M10 前）

- [ ] 导入目录 Mod 与压缩包 Mod
- [ ] 导出 Mod（ZIP 与 7z Fast/Standard/High/Ultra，含大字典内存警告路径与导出后定位文件）
- [ ] 启用/禁用、分组、排序、Profile 保存与重启恢复
- [ ] 部署（复制模式与符号链接模式）+ 删除 + 回收站
- [ ] 版本检查（单 Mod 与批量）+ 结果展示
- [ ] 元数据修复 / 协助修复 / 伴生恢复 / 批量修复（含备份与回滚）
- [ ] 冲突扫描、护甲污染、护甲复用
- [ ] 资源查看器、模型预览（含特例模型：白银之城-侦探-CW9 黑色问题不回归）、纹理预览通道切换
- [ ] Nexus 登录、下载、更新检查
- [ ] 搜索（含拼音）、自动打标
- [ ] 语言切换（zh-CN / en-US）、日志清理、设置持久化
- [ ] 发布构建（Release win-x64 self-contained）实机运行

## 九、风险清单

| 风险 | 缓解 |
|---|---|
| 重写引入行为回归 | 守护测试先行迁移；差分 harness 程序化对拍（M1 起持续复用） |
| 功能遗漏（导出功能曾漏排模块表，靠自审查发现） | M10 切换前用 `rg` 枚举旧服务全部公开成员消费点做完整性校验；每里程碑吸收清单即 API 清单 |
| 6000 行单体拆分遗漏隐式共享状态（缓存/信号量） | M6 前专项梳理 `_patchAnalysisCache`/`_repairSemaphore`/`_gameReferenceIndex` 的全部触点 |
| 错误码文案映射不全导致界面空白 | M9 建立双向校验：代码引用的错误码必须存在于 resx 两语言 |
| 老用户数据丢失引起困扰 | 已确认接受；文档与首次启动提示说明"全新初始化"；旧 `settings.json`/`mod_manager.db` 永不删除，保留恢复现场 |
| 任务终态抢占步骤更新（历史真实缺陷复发） | 设计决策 4：状态机串行有序管道；任务引擎单测覆盖"步骤更新与终态竞态"场景 |
| PatchTool 断链被遗忘 | M10 移出构建时在分支提交信息与本文件同时注明 |


