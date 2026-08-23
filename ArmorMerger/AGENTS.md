# ArmorMerger 开发指引

## 1. 项目概述

独立 WPF 工具，用于 Helldivers 2 Mod 的护甲 patch 合并。

**核心功能：**
- 分析 patch 文件中的 Unit 结构（TOC + GPU 数据）
- 合并多个 Unit 的 TOC 数据和 GPU 数据到新 patch
- 导出完整 Mod 包（含 manifest.json）

## 2. 补丁文件格式

### Patch 文件结构（.patch_0）

```
Header (72 bytes):
  Magic (4): 0xF0000011
  NumTypes (4): 类型条目数量
  NumFiles (4): File 条目数量（Unit 数量）
  Reserved (60): 保留字段

Type Entry (32 bytes * NumTypes):
  [0-7]: Reserved
  [8-15]: TypeId (long)
  [16-23]: ResourceCount (ulong)
  [24-31]: Reserved

File Entry (80 bytes * NumFiles):
  [0-7]: FileId (long) - Unit 唯一标识
  [8-15]: TypeId (long) - 资源类型
  [16-23]: TocOffset (ulong) - TOC 数据在 patch 中的偏移（相对数据区）
  [24-31]: StreamOffset (ulong) - 数据在 stream 文件中的偏移
  [32-39]: GpuOffset (ulong) - 数据在 gpu_resources 文件中的偏移
  [40-55]: Reserved
  [56-59]: TocSize (uint) - TOC 数据大小
  [60-63]: StreamSize (uint) - stream 数据大小
  [64-67]: GpuSize (uint) - gpu_resources 数据大小
  [68-75]: Reserved
  [76-79]: EntryIndex (uint) - 1-based 索引

TOC Data:
  各 Unit 的 TOC 数据按 TocOffset 顺序排列
```

### 伴生文件

- `.patch_0.gpu_resources` - GPU 资源数据（mesh、纹理等）
- `.patch_0.stream` - 流数据（可选）

### 关键字段关系

- `TocOffset` = TOC 数据在 patch 文件中的**绝对偏移**（不是相对偏移！）
- `GpuOffset` = GPU 数据在 gpu_resources 文件中的**绝对偏移**
- `TocSize` = TOC 数据大小
- `GpuSize` = GPU 数据大小
- 验证：`TocOffset` = Header(72) + TypeEntry(32*N) + FileEntry(80*M) = 数据区起始位置

## 3. 合并逻辑

### 分体版 vs 合并版

| 版本 | Unit 数量 | patch 大小 | gpu_resources 大小 |
|------|----------|-----------|-------------------|
| 分体版 | 23 | 439KB | 53MB |
| 合并版 | 1 | 39KB | 37MB |

合并版使用 Unit[15] (FileId=0xBFDC0F01475D16C8) 的 TOC 结构，
但 GPU 数据是合并后的（37MB vs 原始的10MB）。

### 合并操作

1. 选择要合并的 Unit 索引
2. 读取各 Unit 的 TOC 数据
3. 重新计算 TocOffset（连续排列）
4. 读取各 Unit 的 GPU 数据
5. 重新计算 GpuOffset（连续排列）
6. 输出新 patch + gpu_resources

## 4. 易错点

| 容易犯的错误 | 正确做法 |
|---|---|
| TocOffset 是相对偏移，需要加 dataStartOffset | TocOffset 是 patch 文件中的**绝对偏移**，直接使用即可 |
| Size 字段是 Unit 数据大小 | File Entry 中没有 "Size" 字段，实际是 TocSize + GpuSize 分别表示 |
| 合并就是挑选 Unit | 合并需要重新计算所有偏移量 |
| gpu_resources 可以整体读入内存 | 大文件应使用有界随机读取 |
| EntryIndex 是数据偏移 | EntryIndex 是 1-based 索引 |

## 5. 验证命令

```powershell
# 构建
dotnet build ArmorMerger/ArmorMerger.csproj --configuration Debug

# 运行
dotnet run --project ArmorMerger/ArmorMerger.csproj --configuration Debug
```
