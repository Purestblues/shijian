# 视觉与动效对齐 Super Productivity（参考实现）

> 触发：2026-11-11 用户指定参考项目 `D:\Projects\super-productivity-master`（Angular + Material，开源），
> 喜欢它的**动效、UI、卡片设计、配色**。本文只定目标与分批，不写代码。
> 分析基于仓库源码（295 个 scss、223 处 transition、30 个 keyframes、16 个 Angular 动画文件），非凭截图。

## 一、参考项目到底怎么做的（只取与我们有用的部分）

### 1.1 动效骨架

```
时长：xs 90 / s 150 / m 225 / l 375 / enter 225 / leave 195 / page 225（ms）
缓动：standard cubic-bezier(.4,0,.2,1)
      enter    cubic-bezier(0,0,.2,1)     ← 所有「出现」用它
      leave    cubic-bezier(.4,0,1,1)
位移：页面 8px；小元素 4px；覆盖层 scale 1.05（不是 0.8）；侧滑 25%
```

- **换页**：旧页 75ms 淡出 + 新页 180ms 淡入（延迟 45ms），并行，总 225ms。用 `translateY(8px)`，**不用 scale**（缩放会重采样整页）；两页不做 crossfade（会双重曝光）。
- **列表项**：进出做的是 `height 0 ↔ auto` + opacity，**不是整行位移**（位移会让相邻行跳动），进场 stagger +40ms、离场 −40ms。
- **展开/折叠**：`height 0 ↔ *`，同时把 margin/padding 一起收到 0，否则收起来会卡在 padding 上再跳一下。
- **hover/状态**：90ms ease-in-out 改底色；150ms ease-out 改阴影。
- **没有真弹簧**：全场 0 处 overshoot 曲线，所谓「回弹」都是三帧 keyframes（0→1.4→1）。→ 我们不需要 SpringAnimation。
- 全局一个开关关掉所有动效（对应我们已有的 `Motion.IsEnabled`）。

### 1.2 配色骨架

- **不是「一个主题色 + 灰阶」**，而是：一套中性表面阶梯 + 一个**运行期可换**的主色色阶（50–900 十档）+ 一个固定第二强调色（粉 `#ff4081`）。
- 中性色不是纯灰：亮色背景 `#f8f8f7`（暖 0.3%）、正文 `rgb(44,44,44)`、暗色背景 `#131314`（偏蓝）。
- 亮色**刻意不用明度分层**（surface-2/3/4 全是 `#fff`），层级靠 1px 发丝线；暗色才有 4 档真实明度。
- 语义色就是 Material 500：成功 `#4caf50`、警告 `#ff9800`、危险 `#f44336`；**没有第 4 种 info 色**，info 复用主色。
- 交互态统一用 alpha 叠加在一个「RGB 三元组」token 上（hover .06 / focus .10 / pressed .14），换主题只改三元组。
- 彩色**极度克制**：大面积填充 ≤10% 透明、边框 30% 透明、只给图标本身满色；文字永远回到 ink。
- 背景有**极淡的双色径向光晕**（亮色 40%/15%，暗色 10%/4%），不是纯色。

### 1.3 卡片 / 布局 / 字体

- **主视觉是「扁平行 + 发丝线」，不是阴影卡片**：`radius 8px` + `1px solid divider` + 卡片底色，**静置零阴影**；阴影只给真正浮起来的东西（对话框 8dp、拖拽预览、便签 hover）。
- 标尺：圆角 4/6/8/12/16/pill；间距 8 的倍数（8/16/24/…/72），卡片内边距 8–12，卡片间距 8，页面底部留 72 呼吸。
- 字体优先系统 UI 栈（SF Pro / Segoe UI Variable / Roboto）；正文 14/400/1.5；标题 600 且字号越大字距越负（-0.02em）；**区块标题故意比内容小一号**（12px/600/+0.02em/muted）。
- 数字用 `tabular-nums`，**不用等宽字体**。
- 滚动条 4px、无轨道底色、药丸 thumb，hover 才加深。
- 状态不能只靠底色：选中 = 底色 tint **+ 一条 outline**。

## 二、我们现在的样子（`ShiJian.Client/Resources/Themes/Theme.axaml`）

| 维度 | 现状 | 与参考的差距 |
| --- | --- | --- |
| 主题色 | 单一 `ThemeColor`（默认 `#8780F7`），全局 30 个文件引用 | 没有色阶（50–900），没有第二强调色，没有「按分类/项目换主色」 |
| 表面 | 已有 `Surface0..4` 阶梯（暗色 `#0C0C0C/#161618/#1E1E22/#28282E/#383840`） | 亮色/暗色都用「明度分层」，与参考的「亮色靠发丝线」不同；卡片用的是 Surface4（最亮一档），层级语义反了 |
| 卡片 | 「大灰块」：`StandardBackgroundBrush` = Surface4，圆角走 `RadiusSmall/Medium/16` | 没有 1px 发丝边，圆角不统一（8/16/20 混用），无阴影体系 |
| 硬编码色 | 15 个 axaml 里 408 处硬编码 hex | 需要清洗进 token |
| 文字 | `TextPrimary/Secondary/Tertiary/Disabled` 四档 | 缺「字距随字号变化」的排版规则 |
| 动效 | 只有换页 260ms 淡入（`PageMotion`） | 无 token、无位移、无列表/展开动画 |

## 三、落地分批

### 阶段 A · Token 层（低风险，先做）

1. 新建 `Themes/Tokens/`：`Color.axaml`（明暗两套）、`Space.axaml`、`Radius.axaml`、`Type.axaml`、`Motion.axaml`、`Elevation.axaml`。
2. 用参考项目的值替换现有 `Surface*/Border*/Text*`，把「卡片」从 Surface4 改为 `surface-2 + 1px divider`。
3. 圆角收敛到 4/6/8/12/16/pill（现有 `RadiusMedium=16` 与 `RadiusLarge=16` 重复，需先定用途）。
4. 清掉 408 处硬编码 hex，映射到 token（分文件做，每文件一次提交）。
5. 字号/字重/字距按 1.3 的规则重排。

### 阶段 B · 组件视觉（N1 + N3，量大，视觉收益最大）

1. **卡片组件**：统一 `Card`（8px 圆角 + 1px 发丝边 + surface-2 + 零阴影）与 `CardHover`（只加深阴影或底色，不位移）。
2. **列表行**：按参考项目做「扁平行 + 上下 1px 分隔线」，相邻行用 `top:-1px` 让边框塌陷成一条（否则出现 2px 灰线）。
3. **区块标题**：加 eyebrow 风格（12px/600/+0.02em/muted），替换现在的大标题堆叠。
4. **图表取色**：从「用户选的单一主题色」改为按分类取色阶，且满足 WCAG 对比度（沿用现有 `ChartColorHelper` 的相对亮度判定）。
5. **背景光晕**（可选）：`WindowContentBackground` 上加极淡径向渐变。

### 阶段 C · 动效体系 v2（N4 重做）

1. `Motion` 里加 token：`Xs 90 / S 150 / M 225 / L 375`、三条缓动曲线，全部动效只允许引用这些。
2. 换页按参考实现：新页 `opacity 0→1`（180ms，延迟 45ms）+ `translateY 8→0`，总 225ms。
3. 位移要**自己插值**（见下方技术结论），所以新增 `Motion/SlideAnimator`（`DispatcherTimer` ~16ms + 三次贝塞尔求值），只改 `TranslateTransform.Y`，不触发 layout。
4. 列表行进出：`Height` + `Opacity`（`Height` 可以直接用 Avalonia 的 Animation，已验证可行），stagger 40ms。
5. 展开/折叠：同上，同时收 padding/margin。

## 四、技术结论（已实测，别再走弯路）

| 能力 | Avalonia 12.1.1 实测 |
| --- | --- |
| `Opacity` 动画 | ✅ 可以，`DoubleAnimator` 内置 |
| `Height` / `Width` 动画 | ✅ 可以（`Layoutable.HeightProperty`，double） |
| `RenderTransform` 动画（含 `TranslateTransform` / `TransformOperations`） | ❌ **完全不可用**：`Animation.Animators` 已移除，`TransformAnimator`/`TransformOperationsAnimator` 都是 internal；`Animation.SetAnimator` 也因为拿不到动画器实例而无法用。任何关键帧里带 `RenderTransform`，`RunAsync` 立刻抛 `No animator registered for the property RenderTransform`，**整个动画被跳过**（不是只跳这一帧） |
| 因此 8px 位移 | 需自写插值：`TranslateTransform.Y` 逐帧推进（GPU 合成，不触发 layout） |

推论：参考项目里靠 `transform` 做的一切（位移/缩放/旋转）我们都要么自写插值，要么换成 `Height`/`Width`/`Margin` 这类可动画属性。

## 五、明确不照搬

| 项 | 原因 |
| --- | --- |
| Angular/Material 组件体系 | 我们是 Avalonia，只能取设计意图，不能取实现 |
| `color-mix()` 运行期混色 | Avalonia 无此能力，需在 token 层预先算好静态色值 |
| 双色径向光晕的复杂叠加 | 需要 `RadialGradientBrush` + 多层 alpha，先做单层试试，观感不够再叠 |
| backdrop-filter / blur 动效 | Avalonia 只有 `BlurEffect`，逐帧改半径会整树重渲染 |
| 弹簧/回弹曲线 | 参考项目本身没有；我们的 `CubicBezierEase` y 值必须 0..1，超界要自写缓动 |
| 每项目一色的完整色阶体系 | 取决于下方决策 Q2；如果只做静态色阶，先实现 3–5 个分类色 |

## 六、验收线

1. 关掉动效开关后，任何页面/控件都**没有**过渡（无残留半透明、无延迟）。
2. 所有动效时长只来自 token 表，代码里不允许出现裸毫秒数。
3. 卡片/列表在亮色与暗色下都满足：正文对比度 ≥ 4.5:1，次要文字 ≥ 3:1（沿用现有 `ChartColorHelper` 的判定）。
4. 换页位移 8px、时长 ≤ 225ms；肉眼不应出现「页面飞进来」。
5. 列表项进出不做整行位移，相邻行不跳动。
6. 主题切换不需要重启，所有颜色走 `DynamicResource`。

## 七、已拍板（2026-11-11）

| # | 问题 | 结论 |
| --- | --- | --- |
| Q1 | 卡片语汇 | **照搬**：8px 圆角 + 1px 发丝边 + surface-2 底 + 零阴影；阴影只给弹窗/拖拽/浮层 |
| Q2 | 主色策略 | **单一主题色 + 50–900 色阶 + 固定第二强调色**；界面外壳不跟分类变色；图表/标签用分类自己的色阶 |
| Q3 | 背景光晕 | **要**，先做静态渐变（颜色不跟主题色动态混，换主题色不重算） |
| Q4 | 列表行 | **扁平行 + 发丝线**（相邻边框 `top:-1px` 塌陷），无圆角无阴影 |

开工顺序：阶段 A（token 层）→ 阶段 B（卡片/列表/标题/图表取色）→ 阶段 C（动效 v2）。

**进度（2026-11-11，阶段 A/B 主体 + C 已随 [`v1.3.4-fork.7`](https://github.com/Purestblues/shijian/releases/tag/v1.3.4-fork.7) 发布）**

| 阶段 | 状态 | 说明 |
| --- | --- | --- |
| A · token 层 | ✅ 暗色 + 浅色 + Azure 完成 | 表面阶梯 / 文字四档 / 发丝线 / 卡片 / Elevation / 字号标尺；`Themes/Tokens/` 已拆分为 6 个 token 文件（`Color`、`Radius`、`Space`、`Type`、`Motion`、`Elevation`）并补齐 `RadiusXLarge=16`、三套主题 `Elevation1-3` 与 `ThemeRamp50..900Brush` |
| B · 组件视觉 | ✅ 已完成 | `.card`/`.statCard`/`.listRow` 落地；图表按分类 50–900 色阶着色、热力图 5 档阶梯与月历日历纯色透明度编码完成；设置页图标+文字标签栏与 `Surface2/Surface3` 嵌套子卡、分类应用/网站列表单卡 48px 扁平分割行与删除交互、WebUI 浅色 Nord token 与文案对齐完成 |
| C · 动效 v2 | ✅ 遗留项已完成 | `MotionTokens`、`PageMotion`、`SlideMotion`、`ExpandMotion` 与 `ListRowMotion`（列表行 stagger 进场 + 移除高度折叠）均已落地 |
| 遗留（已完成） | ✅ 2026-11-19 收口 | 列表行进出 stagger、`CategoryAppListPage`/`CategoryWebSiteListPage` 扁平清单与移除动效、`Themes/Tokens/` 拆分、`RadiusXLarge=16` 语义去重、图表 50–900 色阶已全部实现并通过 211 项单测与 `/gui-verify` 双主题验收（交互与微动效重构见 `PLAN-INTERACTION-MOTION.md`） |
阶段 C 里「8px 位移」需自写插值（`Motion/SlideAnimator`），其余（淡入/height/展开）可直接用 Avalonia 原生动画。
