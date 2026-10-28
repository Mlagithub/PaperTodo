# Spec: PaperTodo「晶透 / 水晶玻璃 (Crystal Glass)」高级皮肤

- **作者/负责人**: Keep & AI Agent
- **关联意图**: [`doc/intent/crystal-glass-skin.md`](../intent/crystal-glass-skin.md)
- **状态**: 已批准 (Approved)
- **灵感来源**: [Glance (lulu-loopp/glance)](https://github.com/lulu-loopp/glance) 物理光学与磨砂玻璃设计

---

## 1. 假设清单 (Assumptions)

1. **皮肤定位**: 作为与现有「默认纸片 (Paper)」、「描图纸 (TracingPaper)」、「Aero 毛玻璃 (Aero)」、「像素风 (Pixel)」并列的第 5 款可选独立皮肤（ID: `crystal`），供用户在设置中自主选用，不改变其他皮肤的行为。
2. **材质实现基础**:
   - 展开态便签：依托 Windows 11 原生透明外壳（`NativeMicaBackdrop` 与 DWM 清透复合，与 Aero 共享清透底层，但材质装饰逻辑由专属的水晶渲染管线承载）。
   - 贴边胶囊 (Edge Capsule) 与辅助弹出窗口：在 `SkinBorder` 中通过 WPF 矢量光影画刷合成，保证在不支持高级 DWM 或多屏拖拽时具备统一的晶莹质感。
3. **视觉语言核心**:
   - **135° 斜角高光切线 (Rim Light)**：模拟左上方入射主光源，在玻璃边缘形成一条细微（1.25 DIP）、左上微亮通透（透明度 ~0.7）、右下微弱（~0.2）的高级光弧；
   - **多层立体内阴影 (Layered Inset Reflections)**：顶部微高光 + 底部背光微弱反弹 + 内部大范围弱漫反射，赋予便签通透的“物理厚度感”；
   - **纯净与微彩色透光**: 默认呈现纯净的白透（浅色模式）与黑透（深色模式），背景提纯不泛灰脏，并可配合当前配色方案（ColorScheme）微弱渗透出通透的琉璃色泽。

---

## 2. 目标 (Objective)

为 PaperTodo 打造一款具备顶级桌面美感的「晶透水晶玻璃」皮肤，满足用户对现代通透毛玻璃、物理边缘折射高光与立体质感的追求，同时保证长久书写阅读时的舒适对比度与 120Hz/144Hz 的丝滑性能。

---

## 3. 技术架构与渲染管线 (Tech Architecture & Rendering Pipeline)

### 3.1 皮肤标识与数据契约 (`PaperSkins.cs`)

在 `PaperSkins.cs` 中增加 `Crystal`：
```csharp
public static class PaperSkins
{
    ...
    public const string Crystal = "crystal";
    public static readonly string[] All =
        { Paper, Mica, Acrylic, ClearAcrylic, TracingPaper, Aero, Pixel, Crystal };

    public static bool UsesNativeBackdrop(string? id) =>
        id is Mica or Acrylic or ClearAcrylic or TracingPaper or Aero or Crystal;

    public static bool IsDecorated(string? id) =>
        id is TracingPaper or Aero or Pixel or Crystal;

    public static string NativeBackdrop(string? id) => id switch
    {
        Acrylic or TracingPaper => MicaBackdropTypes.Acrylic,
        Aero or Crystal => NativeMicaBackdrop.AeroGlassMaterial,
        ClearAcrylic => MicaBackdropTypes.ClearAcrylic,
        _ => MicaBackdropTypes.Mica
    };
}
```

### 3.2 色盘与透光参数 (`MaterialPalette.cs`)

在 `MaterialPalette.cs` 中为 `Crystal` 配置专属的表面色、透光率与微弱饱和度提纯参数：
- 浅色模式：白透微光 (`paper` 混入轻微冷白高光，透光底衬确保暗色文字对比度充足)；
- 深色模式：黑透水晶 (`paper` 混入深墨色玻璃基底，亮色文字清晰锐利)；
- 避免普通毛玻璃发灰：在表面混入极微量的白色漫反射基底（~7% 不透明度），既保留壁纸通透，又防止复杂桌面背景干扰正文。

### 3.3 物理光影绘制 (`SkinBorder.cs` / `SkinBorder.Materials.cs`)

在 `SkinBorder` 渲染时，对于 `Crystal` 皮肤执行三层绘制：
1. **底衬层 (Base & Diffusion)**: 绘制经由 `MaterialPalette` 调校的纯净半透明底色，作为桌面透光的受光基质；
2. **立体内光影 (Triple Inset Reflections)**:
   - 顶部内边缘：一条 1~2 DIP、透明度 0.4 的顶部直射高光；
   - 底部内边缘：一条透明度 0.12 的环境反弹微光；
   - 中心内部：均匀的微弱漫射微光，营造玻璃内部体积感；
3. **135° 边缘光弧 (Rim Light)**:
   - 计算窗口包围盒对角线长度，创建自左上角 `(0, 0)` 到右下角 `(1, 1)` 的 `LinearGradientBrush`；
   - 渐变停靠点（Gradient Stops）复刻物理光学公式：
     - `Position 0.0 -> Alpha 0.70` (左上顶角强高光)
     - `Position 0.25 -> Alpha 0.20`
     - `Position 0.50 -> Alpha 0.05` (中心微弱)
     - `Position 0.80 -> Alpha 0.10`
     - `Position 1.0 -> Alpha 0.25` (右下反光)
   - 以 `1.25 DIP` 线宽沿圆角矩形外圈绘制，形成晶莹剔透的水晶倒角切面。

---

## 4. 项目结构与代码组织 (Project Structure)

```text
src/
├── PaperSkins.cs                   # 增加 Crystal 常量、解析与分类定义
├── MaterialPalette.cs              # 增加 Crystal 水晶光影色盘、alpha 与混色参数
├── SkinBorder.cs                   # 增加 Crystal 专属几何与绘制分流
├── SkinBorder.Materials.cs         # 增加 Crystal 135° 高光画刷与内反射逻辑
├── AppController.Settings.Skins.cs # 设置面板中注入「晶透」选项
└── Resources/
    ├── Strings.json                # 中文名称: "晶透 (水晶玻璃)" / 说明
    ├── Strings.en.json             # 英文: "Crystal (Glass)" / Description
    ├── Strings.ja.json             # 日文: "クリスタル (水晶ガラス)"
    └── Strings.ko.json             # 韩文: "크리스탈 (유리)"
```

---

## 5. 构建与测试命令 (Commands)

```powershell
# 1. 编译全量工程
& "C:\Users\keep_\app\dotnet-sdk-10.0.401-win-x64\dotnet.exe" build PaperTodo.csproj -c Release

# 2. 运行材质与生命周期自动化测试
& "C:\Users\keep_\app\dotnet-sdk-10.0.401-win-x64\dotnet.exe" run --project tests/PaperTodo.MicaChecks/PaperTodo.MicaChecks.csproj -c Release
& "C:\Users\keep_\app\dotnet-sdk-10.0.401-win-x64\dotnet.exe" run --project tests/PaperTodo.LifecycleChecks/PaperTodo.LifecycleChecks.csproj -c Release
```

---

## 6. 编码规范 (Code Style)

- **冻结画刷 (Frozen Brushes)**：所有动态或静态生成的 `LinearGradientBrush`、`SolidColorBrush`、`Pen` 必须调用 `.Freeze()`，确保 WPF 多线程渲染无锁且具备最高渲染性能；
- **零按键重排**: 材质绘制仅作用于窗口外边框与背景画刷，不介入正文 AvalonEdit 的排版与文字测量，输入保持亚毫秒级响应；
- **几何共享**: 复用 `SkinBorder._shape` 与 `_borderRing`，避免在每一帧创建新的 `Geometry` 对象。

---

## 7. 测试策略 (Testing Strategy)

1. **单元与状态解析测试**:
   - 验证 `data.json` 中配置 `paperSkin: "crystal"` 能被正确反序列化与解析；
   - 验证旧数据迁移与非法皮肤值能安全回退到默认纸片。
2. **渲染回归测试 (`MicaChecks`)**:
   - 验证在浅色、深色主题下，Crystal 皮肤的画刷生成无 null、无崩溃；
   - 验证高对比度模式开启时，自动优雅退回标准高对比度实色，不产生视觉残缺。
3. **真实多屏与 Edge 胶囊验证**:
   - 展开态便签边缘高光与正文对比度手测；
   - 折叠贴边成 Edge Capsule 时，胶囊保持一致的水晶高光与倒角质感。

---

## 8. 质量与安全红线 (Boundaries)

- **Always (必须做)**:
  - 必须冻结所有 WPF 画刷与钢笔对象；
  - 必须保证在任何深浅色壁纸下，便签内文本的可读性达到 WCAG AA 级以上标准；
  - 必须提供多语言资源翻译（中/英/日/韩）。
- **Ask First (需确认)**:
  - 是否对顶栏操作按钮也增加匹配的玻璃微悬浮效果。
- **Never (绝不允许)**:
  - 绝不改动已有的 `paper`、`tracingPaper`、`aero`、`pixel` 既有视觉与代码路径；
  - 绝不在普通打字按键过程中重新分配画刷或测量背景；
  - 绝不破坏 Edge Capsule 队列的 DComp translation-only 架构硬约束。

---

## 9. 验收成功标准 (Success Criteria)

- [ ] **S1. 设置可选**: 打开 PaperTodo 设置 -> 外观 -> 纸片皮肤，列表中出现「晶透」选项，点击即可实时切换。
- [ ] **S2. 水晶立体质感**:
  - 便签外圈拥有清晰的 135° 边缘自然高光切线，左上角微亮通透，右下角自然弱化；
  - 内部具备微内高光漫反射，呈现晶莹剔透的厚度感。
- [ ] **S3. 通透且字迹锐利**:
  - 便签能透出背后的桌面壁纸，色彩鲜明不泛灰、不泛塑料白；
  - 无论是黑色文字（浅色模式）还是白色文字（深色模式），阅读清晰不吃力。
- [ ] **S4. 胶囊统一**: 折叠到屏幕边缘的 Edge 胶囊与展开态便签视觉风格高度统一。
- [ ] **S5. 全量自动化测试绿灯**: 既有所有测试及新增材质测试 100% 通过。

---

## 10. 关键决策结论 (Resolved Decisions)

1. **便签顶栏按钮风格**: 保持现有的极简悬浮微亮效果，视觉重心完全留给水晶玻璃本体，避免过多琐碎倒角干扰视线。
