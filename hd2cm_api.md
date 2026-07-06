# Helldivers 2 Unofficial API 参数分析

> 数据来源：
> - [Helldivers-2-API.json](https://helldivers-2.github.io/api/openapi/Helldivers-2-API.json)
> - [Swagger UI](https://helldivers-2.github.io/api/openapi/swagger-ui.html)
> - [README.md](https://raw.githubusercontent.com/helldivers-2/api/refs/heads/master/README.md)

---

## 1. 接口基础信息

| 项目 | 内容 |
|------|------|
| OpenAPI 版本 | `3.0.0` |
| 生成工具 | NSwag v14.6.2.0 |
| API 标题 | Helldivers 2 |
| API 版本 | `1.0.0.0` |
| 基础地址 | `https://api.helldivers2.dev/` |
| 描述 | Helldivers 2 Unofficial API |
| 官方性质 | 非官方，与 ArrowHead studios 无关联 |

---

## 2. 参数总览

该 API 规范中共有 **8 个路径参数** 和 **3 个安全相关头部参数**。

- 所有接口均为 `GET`。
- 无 query 参数。
- 无 request body 参数。
- 所有路径参数均为必填。

---

## 3. 路径参数

| 接口 | 参数名 | 位置 | 类型 | 必填 | 说明 |
|------|--------|------|------|------|------|
| `GET /raw/api/v2/SpaceStation/War/801/{index}` | `index` | path | `integer` (`int64`) | 是 | 空间站 ID，文档示例为 `749875195` |
| `GET /api/v1/assignments/{index}` | `index` | path | `integer` (`int64`) | 是 | 任务（Assignment）索引 |
| `GET /api/v1/campaigns/{index}` | `index` | path | `integer` (`int32`) | 是 | 战役（Campaign）索引 |
| `GET /api/v1/dispatches/{index}` | `index` | path | `integer` (`int32`) | 是 | 通讯（Dispatch）索引 |
| `GET /api/v1/planets/{index}` | `index` | path | `integer` (`int32`) | 是 | 星球（Planet）索引 |
| `GET /api/v1/steam/{gid}` | `gid` | path | `string` | 是 | Steam 新闻条目 ID |
| `GET /api/v2/dispatches/{index}` | `index` | path | `integer` (`int32`) | 是 | v2 通讯索引 |
| `GET /api/v2/space-stations/{index}` | `index` | path | `integer` (`int64`) | 是 | v2 空间站索引 |

### 3.1 路径参数共性

- 所有 `index` 参数都没有默认值、枚举或额外格式约束，仅受类型限制。
- v1 的星球、战役、通讯使用 `int32`；空间站、任务相关接口使用 `int64`。
- `gid` 是唯一一个字符串类型的路径参数。

---

## 4. 头部参数

### 4.1 请求头部（建议/未来必填）

| 头部 | 类型 | 是否必填 | 说明 |
|------|------|----------|------|
| `X-Super-Client` | `apiKey` in header | 目前可选，**未来将强制要求** | 用于唯一标识你的应用或域名，例如 `api.helldivers2.dev` |
| `X-Super-Contact` | `apiKey` in header | 可选 | 开发者联系方式（邮箱或 URL），当网站没有公开联系方式时建议提供 |

> 注意：OpenAPI 规范中所有接口都声明了这两个安全头部，但 README 明确说明目前 API 不需要认证即可访问（除非需要更高限流）。`X-Super-Client` 未来会变成强制项，未携带的客户端将会请求失败。

### 4.2 响应头部（限流相关）

| 头部 | 说明 |
|------|------|
| `X-Ratelimit-Limit` | 当前时间窗口内允许的总请求数 |
| `X-RateLimit-Remaining` | 当前时间窗口内剩余可请求次数 |
| `Retry-After` | 仅在返回 `429` 时存在，表示需要等待的秒数 |

### 4.3 定义但未启用的安全方案

| 方案名 | 类型 | 说明 |
|--------|------|------|
| `Bearer` | `http` bearer token | JWT bearer token，用于认证访问（当前未在任何接口中启用） |

---

## 5. 限流策略

- 当前限流：**每 10 秒 5 次请求**。
- 该限制未来可能会提高。
- 建议客户端在调用时检查响应中的限流头部，避免触发 `429 Too Many Requests`。

---

## 6. 请求示例

### 6.1 获取指定星球

```http
GET https://api.helldivers2.dev/api/v1/planets/123
X-Super-Client: MyApp
X-Super-Contact: dev@example.com
```

### 6.2 获取指定 Steam 新闻条目

```http
GET https://api.helldivers2.dev/api/v1/steam/1234567890abcdef
X-Super-Client: MyApp
X-Super-Contact: dev@example.com
```

### 6.3 获取指定空间站

```http
GET https://api.helldivers2.dev/raw/api/v2/SpaceStation/War/801/749875195
X-Super-Client: MyApp
X-Super-Contact: dev@example.com
```

---

## 7. 结论

- 该 API 参数设计非常简单，仅使用路径参数和固定安全头部。
- 目前 `X-Super-Client` 和 `X-Super-Contact` 不是强制项，但建议始终携带，以兼容未来变更并方便官方联系。
- `Bearer` 认证虽然在规范中定义，但尚未实际启用。
- 当前限流较严格（5 请求/10 秒），客户端应做好限流控制。

---

## 8. 实战响应数据分析

通过实际调用接口并保存响应到 `api_responses/` 目录，得到以下观察。

### 8.1 响应文件一览

| 文件 | 大小 | 说明 |
|------|------|------|
| `raw_api_WarSeason_current_WarID.json` | ~0.02 KB | 当前赛季 ID |
| `api_v1_war.json` | ~0.74 KB | 社区包装后的战争总览 |
| `raw_api_WarSeason_801_Status.json` | ~87 KB | 实时战争状态 |
| `raw_api_Stats_war_801_summary.json` | ~126 KB | 星系与行星统计 |
| `raw_api_WarSeason_801_WarInfo.json` | ~129 KB | 战争静态信息 |
| `raw_api_v2_Assignment_War_801.json` | ~1 KB | 原始重要指令 |
| `api_v1_assignments.json` | ~1 KB | 包装后的重要指令 |

### 8.2 `GET /api/v1/war`

返回全局战争信息，关键字段：

```json
{
  "started": "2024-01-23T20:05:13Z",
  "ended": "2028-02-08T20:04:55Z",
  "now": "1972-05-18T17:15:30Z",
  "clientVersion": "0.3.0",
  "factions": ["Humans", "Terminids", "Automaton", "Illuminate"],
  "impactMultiplier": 0.046635006,
  "statistics": {
    "missionsWon": 953748550,
    "missionsLost": 93116970,
    "missionTime": 3127832651695,
    "terminidKills": 207688559272,
    "automatonKills": 111257267658,
    "illuminateKills": 69247164312,
    "bulletsFired": 1839786387325,
    "bulletsHit": 2008551744413,
    "timePlayed": 3127832651695,
    "deaths": 8304463410,
    "revives": 2,
    "friendlies": 955853674,
    "missionSuccessRate": 91,
    "accuracy": 100,
    "playerCount": 28418
  }
}
```

可直接用于 UI 的字段：

- 当前在线人数：`statistics.playerCount`
- 任务成功率：`missionSuccessRate`
- 全局影响倍率：`impactMultiplier`
- 各阵营击杀：`terminidKills`、`automatonKills`、`illuminateKills`

### 8.3 `GET /raw/api/WarSeason/801/Status`

实时战争状态是展示银河战争主线进度的核心数据。

顶层字段：

| 字段 | 说明 |
|------|------|
| `warId` | 赛季 ID |
| `time` | 快照时间戳 |
| `impactMultiplier` | 当前任务结算影响倍率 |
| `storyBeatId32` | 剧情节拍 ID |

主要数组统计：

| 数组 | 数量 | 说明 |
|------|------|------|
| `planetStatus` | 274 | 各星球实时状态 |
| `planetAttacks` | 63 | 当前星球间进攻路线 |
| `campaigns` | 40 | 当前活跃战役 |
| `jointOperations` | 1 | 联合行动 |
| `planetEvents` | 1 | 星球特殊事件 |
| `planetRegions` | 35 | 星球区域状态 |

`planetStatus` 单条示例：

```json
{
  "index": 0,
  "owner": 1,
  "health": 1000000,
  "regenPerSecond": 4.1666665,
  "players": 104,
  "position": { "x": 0, "y": 0 }
}
```

含义：

- `index`：星球 ID
- `owner`：控制派系（1=人类，2=终结族，3=Automaton，4=Illuminate）
- `health`：当前生命值 / 解放进度
- `regenPerSecond`：自然恢复速度
- `players`：当前该星球玩家数
- `position`：星系图坐标

### 8.4 `GET /raw/api/WarSeason/801/WarInfo`

战争的静态信息，用于把星球 ID 映射到坐标、名称和连接关系。

关键字段：

| 字段 | 说明 |
|------|------|
| `warId` | 赛季 ID |
| `startDate` | 赛季开始 Unix 时间戳 |
| `endDate` | 赛季结束 Unix 时间戳 |
| `layoutVersion` | 布局版本 |
| `minimumClientVersion` | 最低客户端版本 |
| `planetInfos` | 274 个星球静态定义 |
| `homeWorlds` | 2 个阵营母星 |
| `planetRegions` | 207 个区域定义 |

### 8.5 `GET /raw/api/Stats/war/801/summary`

统计摘要包含全星系和逐星球数据。

```json
{
  "galaxy_stats": { /* 全星系统计 */ },
  "planets_stats": [ /* 284 条行星统计 */ ]
}
```

`galaxy_stats` 关键字段：

- `missionsWon` / `missionsLost`：任务胜败数
- `bugKills` / `automatonKills` / `illuminateKills`：阵营击杀
- `bulletsFired` / `bulletsHit`：开火/命中数
- `timePlayed`：总游戏时长
- `deaths` / `revives` / `friendlies`：死亡/复活/友伤
- `missionSuccessRate`：任务成功率
- `accurracy`：命中率

> 注意：`planets_stats` 中存在 `planetIndex` 为负数（如 `-1337`、`-420`）的测试/占位条目，使用前需要过滤。

---

## 9. 重要指令（Major Order）

### 9.1 包装后接口 `GET /api/v1/assignments`

推荐在 UI 中使用此接口，字段更友好：

```json
[
  {
    "id": 2274535665,
    "progress": [3],
    "title": "MAJOR ORDER",
    "briefing": "Defend against the designated number of Terminid attacks...",
    "description": null,
    "tasks": [ { "type": 12, "values": [5, 2, 0, 0], "valueTypes": [3, 1, 11, 12] } ],
    "reward": { "type": 1, "amount": 45 },
    "rewards": [ { "type": 1, "amount": 45 } ],
    "expiration": "2026-07-05T09:02:34.9089837Z",
    "flags": 0
  }
]
```

### 9.2 原始接口 `GET /raw/api/v2/Assignment/War/801`

返回 ArrowHead 官方原始格式，数值字段较多：

```json
[
  {
    "id32": 2274535665,
    "startTime": 74876150,
    "progress": [3],
    "expiresIn": 250230,
    "setting": {
      "type": 4,
      "overrideTitle": "MAJOR ORDER",
      "overrideBrief": "Defend against...",
      "tasks": [ ... ],
      "rewards": [ { "type": 1, "id32": 897894480, "amount": 45 } ]
    }
  }
]
```

### 9.3 本地化支持

API 支持通过 `Accept-Language` 头部返回游戏内本地化文本。

请求示例：

```http
GET https://api.helldivers2.dev/api/v1/assignments
X-Super-Client: MyApp
X-Super-Contact: dev@example.com
Accept-Language: zh-Hans
```

返回：

```json
{
  "title": "重要指令",
  "briefing": "抵御指定轮数的终结族攻击，保障TCS+建造地点的安全，确定阵列的具体设立位置。"
}
```

常用语言代码：

| 语言 | 代码 |
|------|------|
| 简体中文 | `zh-Hans` |
| 繁体中文 | `zh-Hant` |
| 英语 | `en-US` |
| 德语 | `de-DE` |
| 俄语 | `ru-RU` |
| 法语 | `fr-FR` |

若传入 `Accept-Language: ivl-IV`，则返回所有可用语言的对象形式，方便本地缓存和用户切换。

---

## 10. 测试脚本

已编写 Python 测试脚本用于抓取上述接口并保存响应：

```
C:\Users\TYHH10\AppData\Local\Temp\trae\test_helldivers_api.py
```

运行方式：

```powershell
python "C:\Users\TYHH10\AppData\Local\Temp\trae\test_helldivers_api.py"
```

脚本行为：

- 依次调用 5 个银河战争相关接口
- 打印状态码和限流头部
- 美化输出 JSON 响应
- 将每个响应保存到 `api_responses/` 目录
- 接口之间间隔 3 秒以避免触发限流

---

## 11. 数据质量注意事项

在实际响应中发现以下数据异常，UI 展示时需自行处理：

| 问题 | 位置 | 建议处理 |
|------|------|----------|
| `now` 为 `1972-05-18` | `api_v1_war.json` | 该字段不可信，避免直接显示 |
| `bulletsHit` > `bulletsFired` | `api_v1_war.json` / `galaxy_stats` | 命中率不要直接用 `accuracy`，建议自己计算或显示为“-” |
| `accuracy` 固定为 100 | 多处 | 同上，不可直接采信 |
| `revives` 仅 2 | `api_v1_war.json` | 与死亡人数比例明显异常，仅作参考 |
| `planetIndex` 为负数 | `planets_stats` | 过滤 `planetIndex < 0` 的占位数据 |
| `description` 经常为 `null` | `api_v1_assignments.json` | 优先使用 `briefing` 作为任务描述 |