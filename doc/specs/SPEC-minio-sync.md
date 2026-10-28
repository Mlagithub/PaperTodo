# Spec: PaperTodo MinIO 同步插件 (MinIO Sync Plugin)

- **作者/负责人**: Keep & AI Agent
- **关联意图**: [`doc/intent/minio-sync-plugin.md`](../intent/minio-sync-plugin.md)
- **状态**: 已批准 (Approved)
- **API 兼容目标**: PaperTodo Plugin Protocol 2.2+ (包含 NoteAssets 写入能力增补)

---

## 1. 假设清单 (Assumptions)

1. **插件技术栈**: 采用 C# Native Plugin（.NET 10 / WPF），运行在宿主独立托管的 Plugin Runtime 中，以获得高性能二进制处理、原生网络并发和系统级后台状态保持能力。
2. **对象存储兼容性**: 针对标准 S3 V4 签名 API（MinIO、Rust S3、阿里云 OSS、AWS S3 等通用对象存储），通过轻量且依赖干净的 S3 客户端（或官方 MinIO SDK）通信。
3. **网络与凭证模式**: 用户在插件设置面板中配置 `Endpoint` (支持 HTTP/HTTPS、IPv6、域名及端口)、`Bucket`、`AccessKey`、`SecretKey`、可选的 `Prefix` (默认 `papertodo/`)，支持内网直连（如 Tailscale / 家庭局域网）。
4. **宿主数据契约配合**:
   - 待办项与笔记正文的读写直接走 `IPaperTodoHostApi` (Workspace)。
   - 现有的 `IPaperNoteAssetsApi` 目前只暴露了 `ReadImage`；为了支持换机接力时的图片下载还原，需要在宿主/抽象层补充受保护的 `ImportImage` 接口（或由插件经由安全契约调用宿主 `NoteImageStore`），保持 LMDB 单写者安全。
5. **设备唯一性**: 插件生成并在本地持久化一个唯一的 `DeviceId`（如 GUID），用于在上传的 Manifest 元数据中标识最后写入方，以精确判定分叉。

---

## 2. 目标 (Objective)

为 PaperTodo 构建一个跨设备便签/待办与笔记同步插件，基于用户自建的 MinIO 对象存储，实现多台电脑间的**严格串行接力**。
- **核心价值**: 离机时自动或手动安全推送到云端；换机开机后自动或手动拉取恢复，保持便签和待办内容无缝接续。
- **非目标**: 不做多端实时协同敲字 (OT/CRDT)；不强制同步物理窗口绝对坐标 (X/Y) 以免异构多屏错位；不破坏本地单实例和持久化隔离。

---

## 3. 技术架构与存储协议 (Tech Stack & Storage Schema)

### 3.1 远端对象存储布局 (S3 Object Hierarchy)

在用户指定的 Bucket 和 Prefix 下，目录结构如下：

```text
s3://<bucket>/<prefix>/
├── v1/
│   ├── manifest.json                  # 全局同步元数据（版本号、最后设备、时间戳、纸片摘要清单）
│   ├── papers/
│   │   ├── {paperId}.json             # 单张纸片的完整业务数据（标题、类型、Todo列表、Note Markdown文本）
│   │   └── ...
│   └── assets/
│       ├── {sha256}.bin               # 按照内容哈希（SHA-256）存储的图片原始二进制
│       └── ...
```

### 3.2 `manifest.json` 协议规范

```json
{
  "schemaVersion": 1,
  "syncRevision": 42,
  "lastDeviceId": "device-uuid-xxxx",
  "lastDeviceName": "Work-Desktop",
  "updatedAt": "2026-03-30T14:22:00Z",
  "papers": {
    "paper-uuid-1": {
      "type": "todo",
      "title": "工作待办",
      "contentHash": "sha256-hash-value-1",
      "updatedAt": "2026-03-30T14:20:00Z"
    },
    "paper-uuid-2": {
      "type": "note",
      "title": "项目备忘",
      "contentHash": "sha256-hash-value-2",
      "updatedAt": "2026-03-30T14:21:30Z",
      "referencedAssets": ["sha256-img-hash-a"]
    }
  }
}
```

- **哈希去重**: 图片以 SHA-256 内容寻址独立存放于 `assets/`；多篇笔记引用同一图片或图片未修改时，永不重复上传/下载。
- **细粒度纸片存储**: 每张纸片独立一个 JSON 文件；变更哪张就只上传哪张，拉取时也只下载差异纸片。

---

## 4. 同步状态机与冲突处理 (Sync Lifecycle & Conflict Strategy)

### 4.1 同步触发阶段

1. **启动拉取 (On Startup / Resume)**:
   - 插件 Runtime 启动后，先向 MinIO 请求 `manifest.json`。
   - 对比本地最新同步记录（本地保存的 `lastSyncRevision` 与 `papers` 哈希）：
     - 若远端 `syncRevision > 本地` 且 本地无未上传修改：执行 **静默更新拉取**。
     - 若远端与本地都有新增/修改（发生分叉）：执行 **安全冲突隔离**。
2. **防抖自动上传 (Debounced Auto-Push)**:
   - 订阅 `Workspace` 的变更事件（`paper.changed`, `todo.changed`, `note.changed` 等）。
   - 本地产生内容修改时，标记本地 dirty，重置 30 秒倒计时防抖定时器。
   - 用户停止编辑 30 秒后，在后台静默打包差异纸片和新增图片上传至 MinIO，并更新远端 `manifest.json`。
3. **退出同步 (On Application Shutdown)**:
   - 捕获插件 Dispose 或宿主退出通知，若当前存在 dirty 变更，执行阻塞同步上传并落盘更新本地同步指针。
4. **手动触发 (Manual Trigger)**:
   - 点击顶栏图标或托盘菜单中的「立即同步」按钮，强制触发一次检查与推拉。

### 4.2 冲突解决原则（安全兜底）

- **判定分叉**: 本地有未推送的修改（Dirty），且远端 `manifest.json` 的 `syncRevision` 已经被其他 `deviceId` 推进。
- **解决动作**:
  - 对于产生冲突的特定纸片，下载远端版本作为新便签创建，标题加上 `[冲突副本-远端]`，保留本地原便签；
  - 顶栏状态指示器切换为橙色警告，并通知用户：“检测到云端有并发修改，已自动为您保留冲突副本便签，请核对。”
  - 杜绝任何静默覆盖导致的数据丢失。

---

## 5. 项目结构与代码组织 (Project Structure)

```text
plugin-samples/PaperTodo.Plugin.MinioSync/
├── PaperTodo.Plugin.MinioSync.csproj    # Native 插件工程 (net10.0-windows)
├── plugin.json                          # 插件清单 (apiVersion: 2.2, runtime: true)
├── MinioSyncPlugin.cs                   # 插件入口 (IPaperPluginRuntimeProvider)
├── MinioSyncRuntime.cs                  # 后台运行核心服务 (管理生命周期、事件订阅)
├── Client/
│   ├── S3Client.cs                      # S3 兼容协议实现 (签名计算、GetObject, PutObject, HeadObject)
│   └── S3Config.cs                      # MinIO 连接配置模型
├── Engine/
│   ├── SyncEngine.cs                    # 核心同步逻辑 (差异对比、拉取、推送、冲突判定)
│   ├── SyncManifest.cs                  # Manifest 结构定义与序列化
│   └── ImageAssetSyncer.cs              # Note 图片资产上传与哈希管理
├── UI/
│   ├── SyncStatusTopBarItem.cs          # 宿主顶栏图标与状态灯呈现 (WPF 控件/TopBar API)
│   └── SyncStatusViewModel.cs           # 状态绑定 (Idle, Syncing, Error, Conflict)
└── Settings/
    └── MinioPluginSettings.cs           # 设置模型与存储转换
```

---

## 6. 构建、测试与运行命令 (Commands)

```bash
# 1. 编译插件工程
dotnet build plugin-samples/PaperTodo.Plugin.MinioSync/PaperTodo.Plugin.MinioSync.csproj -c Release

# 2. 运行自动化测试
dotnet test tests/PaperTodo.MinioSyncChecks/PaperTodo.MinioSyncChecks.csproj

# 3. 安装到本地 plugins 目录进行实测
pwsh plugin-samples/Build-And-Install-NativePlugin.ps1 -PluginId com.papertodo.plugin.minio
```

---

## 7. 编码规范 (Code Style)

遵循 PaperTodo 既有的 C# 编码规范：
- 显式 `sealed class`，不可变数据优先使用 `record`；
- 所有 I/O 操作全异步化（`async`/`await` 带 `CancellationToken`）；
- 涉及网络异常需分类处理（连接超时、鉴权失败 403、Bucket 不存在 404 等），向 UI 汇报清晰的错误描述；
- 绝不在 UI 线程执行网络请求与哈希计算。

---

## 8. 测试策略 (Testing Strategy)

1. **单元测试 (`tests/PaperTodo.MinioSyncChecks/`)**:
   - `S3SignerTests`: 验证 AWS S3 V4 签名算法在各 HTTP 方法及 Query 下的正确性。
   - `SyncEngineDiffTests`: 验证 Manifest 在同设备推进、异设备串行、异设备冲突分叉三种场景下的计算逻辑。
   - `ConflictResolutionTests`: 验证分叉时冲突副本的生成是否完整、是否不覆盖原数据。
2. **Mock S3 模拟测试**:
   - 使用内存或本地模拟 HTTP 服务的 S3 行为，测试大文件上传分块、网络中断重试及异常回滚。
3. **真实 MinIO 端到端测试**:
   - 对接本地或开发环境真实 MinIO 实例进行双客户端换机接力模拟验证。

---

## 9. 质量与安全红线 (Boundaries)

- **Always (必须做)**:
  - 每次推拉前校验图片和纸片 JSON 的 SHA-256 完整性；
  - 遇到未知错误先保留本地未同步文件与快照，记录诊断日志；
  - 将 MinIO SecretKey 作为敏感信息保存在插件私有配置中，不在日志明文打印。
- **Ask First (需确认)**:
  - 是否支持在局域网内跳过 SSL 证书校验（自签名证书常见于家庭内网 PVE 环境）；
  - 是否在宿主主工程中扩展公开的 `ImportNoteImage` API 还是通过反射/内部友元访问。
- **Never (绝不允许)**:
  - 绝不因网络超时或同步异常阻塞 WPF 主 UI 线程；
  - 绝不静默覆盖不同设备上存在差异的同 ID 纸片内容；
  - 绝不把物理窗口位置 (X/Y) 强行同步到云端。

---

## 10. 验收成功标准 (Success Criteria)

- [ ] **S1. 配置生效**: 在 PaperTodo 设置中输入 MinIO Endpoint、AK、SK、Bucket，能一键测试连通性。
- [ ] **S2. 串行换机接力**:
  - 设备 A 上创建/修改便签与待办，包含插入一张 Note 图片，同步状态变绿（已同步）；
  - 设备 B 启动后自动拉取，能完整展现设备 A 创建的便签、待办项及 Note 图片，无需人工干预。
- [ ] **S3. 异构屏幕安全**: 设备 A（多屏/高分屏）与设备 B（笔记本小屏）同步后，设备 B 的便签均正常位于可见区域，位置未飞出屏幕。
- [ ] **S4. 冲突无损**: 两台设备产生内容分叉时，自动生成带有 `[冲突副本]` 的新便签，双方数据 100% 完整保留。
- [ ] **S5. 状态可见**: 顶栏清晰显示绿色（已同步）、蓝色转圈（同步中）、红色（错误）和橙色（冲突），点击可查看最后同步时间与发起手动同步。

---

## 11. 关键决策结论 (Resolved Decisions)

1. **自签名 SSL 证书支持**:
   - 配置项显式提供 `InsecureSkipVerify` (允许不安全的 SSL 连接 / 忽略证书错误) 布尔开关，以兼容内网自建 PVE / Lucky 反代 / 自签名 SSL 环境。
2. **宿主图片写入 API 扩展 (采用方案 A)**:
   - 在 `PaperTodo.Plugin.Abstractions` (`IPaperNoteAssetsApi`) 中正式增补受保护的 `ImportImage(string paperId, string mime, byte[] bytes)` 接口；
   - 并在宿主 `PaperCommandService.NoteAssets.cs` 中实现该方法，严格走 `ImageStore.ImportEncodedBytes`，确保 LMDB 单一写入者事务与图片 ID 去重安全性。
