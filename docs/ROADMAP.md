# 路线图

> 本仓库是 [NLick47/Taix](https://github.com/NLick47/Taix)（现名**时见 / ShiJian**）的个人 fork。
> 2026-11 已全量改名为 `ShiJian` / `shijian-*`，并**不再同步上游**。
> 改动集中在客户端（`ShiJian.Client`）；服务端只有一处配合改动：`General` 配置里新增 `MotionMode`
> 字段（配置版本 v4 → v5），否则动效开关会被服务端配置回写抹掉。监控 / 托盘 / 插件未动。

## 现状

| 维度 | 状态 |
| --- | --- |
| 发布 | [`v2.0`](https://github.com/Purestblues/shijian/releases/tag/v2.0) 正式版（Setup.exe / 绿色版 zip / macOS dmg，均由 GitHub Actions 产出）；仓库已改为 [Purestblues/shijian](https://github.com/Purestblues/shijian) |
| 验证程度 | Windows 客户端逐项实测 + `/gui-verify` 全功能自动化复验；macOS 产物已验证构建通路 |
| 自动化测试 | `ShiJian.Tests`（xunit v3 + Avalonia.Headless）202 个用例全部通过；CI 里先跑测试再打包 |
| 上游同步 | 已停：2026-11 改名与 N14 前端全量重构后，已彻底脱离原 Taix 体系 |
| 代码规模 | 客户端 308 个 `.cs`、69 个 `.axaml`，0 处 TODO/FIXME |

## 已知问题

编号用于提交信息引用，例如 `修复 G1 深色主题下图表对比度不足`。
G1 / G3 / G6 已在 M1 修复，G4 核验为「已经具备」；G8 / G9 已在 M3 修复；G10 / G11 是 M3 收尾时发现的，G10 已规避、G11 在 N4 批次核验为「控件层面已可达」；下表保留原始描述用于追溯。

| # | 问题 | 根因 | 影响 |
| --- | --- | --- | --- |
| G1 | 深色主题下图表偏暗 | `Librarys/ChartColorHelper.cs` 的压暗阈值按浅色背景校准（相对亮度 > 0.82 才压暗） | 深色主题下分类色与背景对比不足 |
| G2 | 概览页没有分类维度 | 榜单与卡片只消费 `DailyLogModel`，未带 `Category` | 有分类的用户看不出「哪类花的时间多」 |
| G3 | 部分网站图标裂图 | `WebFavicons` 目录里存在 0 字节 / 损坏的 ico，Skia 解码抛 `NullReferenceException` | 回退默认图标（功能正常，观感差、日志噪音） |
| G4 | 统计页柱子点击无响应 | `Charts/ColumnChart` 只实现了悬停，没有点击选中与下钻 | 想看「某小时用了哪些应用」必须换页 |
| G5 | 时间轴 / 分类页配色不统一 | 这两处仍直接用 `category.Color`，未走 `ChartColorHelper` | 同一个分类在不同页显示成不同颜色 |
| G6 | 首页请求偏多、事件全量重载 | 首屏 14 个并发请求；每次 `AppChanged` 都重拉全部数据 | 频繁切换应用时产生无效请求与重绘 |
| G7 | 动效无法关闭 | 未检测系统「减少动画」设置，也没有手动开关 | 对晕动症用户不友好 |
| G8 | 详细页时间轴不好用 | 上方是可缩放平移的条状时间轴，下方是每应用一条泳道，两处信息重复且要手动对齐 | 想看清「今天几点在干什么」需要来回操作 |
| G9 | 点网站分类必定闪退 | `Imager` 图标缓存淘汰时释放了仍在渲染的位图，`Image.Render` 抛 `ObjectDisposedException` | 整个客户端进程直接退出 |
| G10 | 调试版启动弹「预料之外的错误」，主界面空白 | `AvaloniaUI.DiagnosticsSupport` 2.2.3 的样式里用了嵌套选择器，与 Avalonia 12.1.1 不兼容（`Nesting selector ... cannot determine parent selector`） | 只影响 Debug 构建；现已默认不挂 DevTools，需要时设 `TAIX_DEVTOOLS=1`。Release 构建不受影响 |
| G12 | 榜单进度条不显示 / 滚动后消失 | 宽度靠代码手算（`容器宽*0.95 - 文字宽`），依赖「布局已完成」与「尺寸还会再变一次」两个假设；虚拟化复用的行尺寸不再变化，宽度永久停在 0 | 切页瞬间、快速滚动后榜单只剩文字没有条。`fork.7` 已修为布局驱动的 `RatioBar` |
| G13 | 设置页折叠头不可点 | `SettingPanelMultiItem` 的 `Expander` 头部没渲染展开箭头，标题区也没有点击热区 | 用户只能靠右侧 `...` 菜单操作，折叠功能实际不可达。**`fork.8` 已修控件层**（整条头部可点 + 箭头随状态转向），但注意：当前设置页两块 `SettingPanel` 传的都是单对象（走 `RenderSinglePanel`），多例面板没有实例化，所以这项在生产 UI 里**没有可见触点**，属于潜伏缺陷的提前修 |
| G11 | 自绘图表用 Tab 走不到 | 原记录归因为 Avalonia 12 的 Tab 遍历不把自定义 `Control` 当候选，但 2026-11-11 在 Avalonia 12.1.1 headless 下逐层（StackPanel / ScrollViewer / ContentPresenter / ItemsControl 模板）都走到了，`Focusable` + `IsTabStop` + `GotFocus→InvalidateVisual` 三件套齐全 | 现已新增 `ChartTabNavigationTests` 锁住「Tab / Shift+Tab 能进出四类图表」；若实机仍复现，需要补充具体页面与操作序列 |
| G14 | 概览页「今日」偶发空白/异常（**P0**） | `IndexPageViewModel.LoadRangeAsync:277`（`Skip`/`Take` 链路）偶发 `System.ArgumentOutOfRangeException` | 默认首屏指标卡与图表空白，切换周期后恢复 |
| G15 | 统计页堆叠柱图图例纵向裁切（**P1**） | `Views/ChartPage.axaml:239` `ColumnChart` 固定 `Height="310"`，未为多行折行图例留足动态高度 | 「周 / 年」视图底部 3 排图例被横向边缘切掉一行 |
| G16 | 分类汇总柱图参考线文字重叠（**P1**） | `ColumnChart.cs:487-494` `midY` 与 `avgY` 直接按数值坐标绘制文本，未做标签碰撞避让 | 中位线与平均值时长标注重叠难辨（如 `2h21m`/`2h48m`） |
| G17 | 详细页年视图热力图纵轴重叠（**P1**） | `HeatGrid.cs:197-206` 按固定绘图高度均分 12 行（`1月`–`12月`），行高小于字体高度 | 12 个月份标签相互挤压粘连，可读性差 |
| G18 | 分类关联弹窗搜索无效与脏数据（**P1**） | ① 「选择站点」`CategoryWebSiteListPageViewModel.DoSearch` 用 `WildcardHelper.IsMatch` 整串匹配；② 「选择应用」仅 `KeyUp` 触发且存在空名称/重复应用项 | 输入关键词无法筛出目标应用或站点，列表头部存在空白行 |
| G19 | 侧边栏高亮态在设置/详情跳页后不同步（**P1**） | `MainWindow.axaml:289` 设置项 `Uri="SettingsPage"` 与 `CommandParameter="SettingPage"` 不一致，且全局搜索跳详情未清理旧导航选中态 | 当前所在页与侧边栏紫色高亮指示不一致 |
| G20 | 概览页自定义日期胶囊点击无响应（**P2**） | `IndexPage` 自定义宿主的 `DateSelect` 居中胶囊点击未弹出日历（箭头可正常加减日期） | 只能逐天点击左右箭头调整起止日期，无法直选跨月日期 |
| G21 | 分类「应用与网站」局部蓝底占位（**P2**） | 部分实体在分类实体页与概览/详细页图标管线不一致；同时 `Imager` 解码坏 `.ico` 持续产生 `NullReferenceException` 降级日志 | 部分实体卡显示蓝底默认占位，日志有降级噪音 |
| G22 | 设置与全局搜索 3 处细节瑕疵（**P2**） | ① 新建合并组选满 2 项未清空红字错误；② WebDAV 开关 `Toggle` 未绑定中文 `OffText`；③ `Ctrl+K` 底栏 `[↑↓]`/`[Esc]` 与提示文案重复 | 轻微表单校验困惑与文案不一致 |

## 里程碑

### M1 · 正确性收口（已完成，已随 `fork.2` 发布）

- [x] **G1** 配色改为按主题背景的对比度判定（用相对亮度差，而不是固定阈值）
      `ChartColorHelper` 改用 WCAG 相对亮度算对比度（目标 3:1）：浅色背景把过浅的分类色压暗、
      深色背景把过暗的提亮，只调到刚好够看。主题切换时 `ThemeServicer` 发 `ThemeChanged`，
      概览页用缓存数据就地重算，统计页重新拉取当前页面数据。
- [x] **G3** favicon 解码前跳过 0 字节文件；失败时降级为域名首字母色块
      `Imager` 对 0 字节文件直接当作「没有图标」，解码失败也不再刷 Error 日志；
      `Img` 控件新增 `FallbackText`，加载失败时显示按域名稳定着色的首字母色块
      （榜单、卡片、柱状图图例、时间轴、网站详情页均已接上）。
- [x] **G4** 核验结论：统计页柱图已经支持点击选中 + 该时段明细（`ColumnChart.OnPointerPressed`
      + `IsCanColumnSelect`，默认开启），无需改动。
- [x] **G6** `AppChanged` 加 500ms 去抖；首屏可合并的请求合并
      事件去抖统一放在 `ModelBase.SubscribeRefresh`（100ms → 500ms）；
      榜单接口本身就带回每个应用 / 网站的时长，应用数、网站数、总时长改成本地汇总，
      概览页首屏请求从 16 个降到 10 个。

### M2 · 数据表达深化（已完成，已随 `fork.2` 发布）

- [x] **G2** 分类维度贯穿首页：榜单 / 更多卡片显示分类，可点击筛选
      `ChartDataMapper` 产出 `CategoryName / CategoryColor` 与分类徽章（同色淡底 + 同色文字，
      不再用饱和度很高的实心色块），榜单开启 `IsShowBadge`，卡片右上角显示「圆点 + 分类名」；
      点徽章直接进「分类详情」页（该分类在这段时间的汇总）。徽章点击走隧道事件，
      不会被「点整行进详情」吃掉。
- [x] 网站侧分类图表——核验结论：早已实现。概览页的应用/网站切换、统计页的网站分类环图与
      堆叠柱图、分类页的网站分类都在用网站分类体系，无需改动。
- [x] 时段对比：24 小时柱图叠加「昨日同小时」淡色柱
      `StackedBarChart` 新增 `CompareValues / CompareBrush`，在当前柱后面画一根略宽的淡色柱；
      概览页多拉一份「上一周期」的分类×时段矩阵并在本地按列汇总，右上角开关默认开启；
      网站维度没有同粒度数据，开关自动隐藏。
- [x] **G5** 时间轴 / 分类页接入统一配色
      详细页时间轴、分类页圆点、分类详情页的分类 chip 与图表都改走 `ChartColorHelper`；
      主题切换时各页收到 `ThemeChanged` 重新解析，不用重启。
      （顺手修了一个隐患：`ChartColorHelper` 现在把背景色 / 主题色缓存成静态值，
      否则后台线程组装图表数据时访问 `Application` 资源会直接抛异常。）
- [x] 概览页周期从「今日 / 本周」扩展到自定义范围
      周期切换新增「自定义」，展开起止日期选择器；单日按小时、多日按天出柱；
      对比基准取等长的上一个区间，文案为「比上期」。

### M3 · 体验与无障碍（已完成，已随 `fork.3` 发布）

- [x] **G8** 详细页改用环形时钟，去掉多轨泳道
      `Controls/Timeline/ClockRing` 自绘表盘：圆周就是周期的时间轴（日 = 24 小时、
      周 = 7 天、月 = 当月天数、年 = 12 个月），扇区是真实使用时段。
      数据源按粒度分开：日 = 会话（`AppSessions`），周 / 月 = 小时桶（`api/data/hours-range`），
      年 = 逐月各查一次 `api/data/range`（服务端的范围查询只按应用汇总，不给到天；
      整年的小时桶是几 MB 的响应，不划算）。
      日页下方原本的每应用泳道删除，改成表盘 + 右侧「前 5 名 + 其他」图例（可点进应用详情 / 分类汇总）；
      周 / 月 / 年 保留原榜单，表盘叠在上方。悬停显示该段的时间与名称，点击扇区进应用详情。
- [x] **G9** 分类页「点网站分类必定闪退」
      根因不在分类本身：`Imager` 的图标 LRU 淘汰时 `Dispose()` 了位图，而 `Img` 控件
      还拿着同一张 `Bitmap` 渲染，下一次绘制在 `Avalonia.Controls.Image.Render` 里抛
      `ObjectDisposedException (Ref<IBitmapImpl>)`，异常冒到渲染循环直接带走进程。
      修法两层：缓存淘汰 / `Clear()` 只丢引用不再释放（交给 GC）；新增 `Controls/Base/SafeImage`
      自己画图，绘制前验活位图、失败回退默认图标，把异常挡在控件内部。
- [x] **G7** 动效统一开关（跟随系统设置 + 设置页可手动关闭）
      `GeneralModel.MotionMode`（跟随系统 / 始终开启 / 始终关闭，默认跟随系统）驱动
      `MotionServicer`：Windows 读 `SPI_GETCLIENTAREAANIMATION`，macOS 读
      `NSWorkspace.accessibilityDisplayShouldReduceMotion`，读不到当开启；窗口每次激活重读一次系统偏好。
      执行端两条线：代码动画（页面进场、总览卡错峰、图表生长、数字滚动）统一读 `Motion.IsEnabled`；
      XAML 里的 `Transitions`（含内联写死在元素上的）由 `MotionTransitions` 在运行时摘掉、重开时还原，
      并持续扫描新实例化的列表行（带退避，不会空转）。
- [x] 键盘导航
      Esc：搜索层优先，其余情况返回上一页（焦点在输入框时先退到页面，避免误退页）。
      侧边栏：Tab 可进、上下键换项、Enter 激活；`TabSwitch`（周期 / 维度切换）左右键改选项；
      榜单列表方向键选中 + Enter 进详情（`KeyboardListActivation` 附加行为，分类页与详情列表都接上了）。
      图表：`ColumnChart` / `DonutChart` / `StackedBarChart` / `ClockRing` 均可聚焦，
      左右键移动选中项、`ColumnChart` 的 Enter 即下钻，自绘控件画统一的焦点环（`FocusVisual`）。
      其余交互控件补了 `:focus-visible` 焦点样式（`Themes/Base/FocusStyles.axaml`）。
- [x] 空态与加载态统一
      `EmptyData` 重写：支持 `Text` / `Description` / `IsLoading` / `IsCompact`，颜色全走主题资源
      （原本写死的浅灰在深色主题下几乎看不见）；加载中显示骨架条，不再先闪一下「暂无数据」。
      各页（概览 / 统计 / 详细 / 分类 / 分类汇总 / 列表页）统一接入，`ListChart` 也支持
      `IsLoading` / `EmptyText` / `EmptyCompact`，加载时由页面 VM 的 `IsLoading` 驱动。

### M5 · 详细页看板（已完成，已随 `fork.5` 发布）

- [x] 指标卡：总使用时长 / 使用应用（网站：访问站点）/ 会话次数（网站：浏览记录）/ 最长单次，
      前两项带「比昨日·上周·上月·去年」环比。数据来自 `api/data/sessions`（应用）
      与 `api/data/webdata/browse-log-list`（站点）的裁剪汇总。
- [x] 时段分布柱图：日 = 24 小时、周 = 7 天、月 = 当月每天、年 = 12 个月，
      叠「上一周期」淡色对比柱（`StackedBarChart.CompareValues`）。
      每段总量直接由「分类 × 时段」矩阵按列求和得到，不额外发请求。
- [x] 使用习惯：首次 / 末次使用（网站为浏览）、峰值时段、活跃小时·天数·月份、
      时段偏好四段条（凌晨 / 上午 / 下午 / 晚上）。
- [x] 使用热力（新增 `Controls/Charts/HeatGrid` 自绘）：日 = 主要应用 / 站点 × 小时、
      周 = 星期 × 小时、月 = 日历（周 × 星期，格子＝当天总时长，非当月的月份格子留空）、
      年 = 月 × 日；悬停看具体时长，格子高度封顶、行少时整体居中，日历模式画成方块。
- [x] 分类占比：环形图 + 前 6 类（其余归「其他」），网站维度走网站分类体系。
- [x] 响应式：窗口宽度 < 1080 收掉右侧洞察栏，高度 < 660 收掉底部柱图，保证表盘还有直径。
      网站维度没有会话级时间轴，周 / 月 / 年榜单改成占满整行（原来是右边一个 420 宽列表、左边全空）。
- [x] 口径说明：环形时钟中心时长按「会话」算，指标卡 / 柱图 / 占比按「小时桶」算，
      两处可能差几分钟（会话合并会把间隔 ≤60 秒的碎片并进去）；这是历史口径，未强行统一。
- [x] 单测：`InsightBuilder`（切片 / 裁剪 / 星期与月日网格 / 会话汇总 / 环比 / 时段分段）新增 18 个用例。

### M6 · 新一代体验（进行中：视觉体系 v1 + 动效 v2 已随 `fork.7` 发布；`fork.8` 收尾完成待发）

> 详细顺序、依赖与发版切分见 [`docs/PLAN-2026-11.md`](PLAN-2026-11.md)；
> 视觉/动效目标与逐项差距见 [`docs/DESIGN-SP-ALIGN.md`](DESIGN-SP-ALIGN.md)。

- [x] **N4** 页面淡入淡出（已按参考项目 v2 重做）
      动效标尺集中到 `Librarys/MotionTokens.cs`：时长四档 90/150/225/375ms，
      曲线三条 `standard(.4,0,.2,1)` / `enter(0,0,.2,1)` / `leave(.4,0,1,1)`（用 `SplineEasing` 表达，
      Avalonia 12 没有公开的 `CubicBezierEase`），页面切换 75ms 出 / 180ms 入 + 45ms 延迟。
      换页动效集中到 `PageContainer` → `Librarys/PageMotion.cs`：新页 `opacity 0→1`（180ms，延迟 45ms）
      + `translateY 8→0`，总 225ms，10 个页面一次全覆盖，各页面不再自己写页面级动画。
      位移由 `Librarys/SlideMotion.cs` 逐帧插值实现（`TopLevel.RequestAnimationFrame` 驱动
      `TranslateTransform.Y`，走渲染变换不触发 layout）；若控件在动画中被卸载会立刻落到终态。
      `Motion.IsEnabled` 关闭时走 `SnapToRest` 同步直切，不产生延迟；动画中途被卸载也不会
      把页面留在半透明（`finally` 清终态）。
      有意**不做**旧页淡出：`PageContainer.LoadPage` 先 `Dispose` 旧页 VM、再把 `DataContext`
      置空（那是为避开 Avalonia 12 的逻辑树竞态），想留住旧画面就得推迟这两步，代价大于收益。
      `IndexPage` 原来的整页动画删除，只保留总览卡错峰（220ms / 36ms 间隔），避免两层淡入叠加。
      展开 / 收起：`Librarys/ExpandMotion.cs`（量出目标高度 → 动画 `Height` → 结束还原成 NaN 自动高度），
      已接入概览页「自定义」范围面板与 `Expander`（设置页规则列表、`SettingPanelMultiItem`）；
      动效关闭时直接落值，不留半高。单测 `ExpandMotionTests` 3 例。
      单测 `PageMotionTests` 3 例 + `SlideMotionTests` 3 例：关动效零延迟直切、进场初态/终态、
      残留状态复位、位移真的花时间且结束归零、token 曲线两端收敛；
      「动画实际耗时 ≥ 50~60% 时长」的断言用来抓「动画被静默跳过」。

      ⚠️ **Avalonia 12 的坑（曾让全站动效静默失效）**：`Animation.Animators` 已移除，
      `TransformAnimator` / `TransformOperationsAnimator` 都是 internal。关键帧里只要出现
      `RenderTransform`，`RunAsync` 会在开头抛「No animator registered for the property
      RenderTransform」，整个动画被跳过；若 `catch` 把它吞了，页面就直接就位，看起来跟没加
      动效一模一样（`IndexPage` 从 M3 起的进场动画一直是这个死状态）。所以位移（上移 8px）
      全部取消，只动 `Opacity`；`catch` 必留 `Warn` 日志。以后要做位移，要么自己插值，
      要么动 `Margin`（要接受逐帧重排）。
- [x] **N1 + N3** 视觉体系 v1（已随 `fork.7` 发布）
      颜色 token（明暗两套字典 + 发丝线 + Elevation + 字号标尺）、全局 `.card`/`.statCard`/`.listRow`/`.eyebrow`、
      29 个卡片容器与 8 个页面统一到 8px 圆角 + 1px 发丝边 + 零阴影、页面留白与内容区光晕。
      `fork.8` 收尾：第二强调色落地（`Accent2Color` = `#FF4081`，图表「上一周期」对比柱 `ChartCompareBrush` 改用它）、
      25 处组件级硬编码色收进 token（`SelectRingBrush` / `WebBadgeBg/FgBrush` / `ScrimBrush` 等），
      `AccentColor`/`AccentBrush` 这对**没人引用**的废弃 token 删除。
      剩余：图表按分类取色阶（随 N7 或之后单独做）、`RadiusMedium=RadiusLarge=16` 的语义重复
- [~] **N6** 子母程序合并（`fork.9`：**第一段已实现，视图期自动合并 + 管理页**）
      - 判定在客户端 `<c>Librarys/AppMergePlanner</c>`：`安装目录锚点 + 品牌名` 推出稳定键，
        `PiDeck-0.9.5-setup` / `PiDeck-0.11.3-setup` 这种同版本变体合成一行（组名取品牌 `PiDeck`）；
        浏览器、系统目录、`setup`/`uninstall` 这类无辨识度品牌一律不参与。
      - 折叠是**纯视图期映射**：先把 `DailyLogModel` 行按「应用 + 日期」并进母成员再算总数与榜单，
        原始数据一行不改；合并后行上有「合并 N 项」徽章，悬停列出成员。
      - 管理页：设置页「常规」底部新增 `子母程序合并` 卡片，逐组「解散」（写本地覆盖表 `app.merge.json`）
        与「恢复全部」。选择存在本地文件（Windows 在程序目录、macOS 在 `~/Library/Application Support/ShiJian`）。
      - 已接入：概览榜单/更多卡片（含对比周期）、统计页 Top 榜。**未接入**：分类页应用列表、搜索、Web UI。
      - 推迟到 `fork.10`（与 N2 一起）：`Publisher` 采集（monitor + 服务端列）、服务端 `EntityMergeGroups/Members`、
        手动绑定与跨源（应用↔网站）合并。理由：本机没有 MSVC 链接器，Rust 改动无法本地编译验证；
        而合并是展示层映射，服务端不参与也能先把效果落地。
- [~] **N2** 应用/网站统一实体表 + 「网站并入浏览器」开关（`fork.10` 第一段：概览页已切源）
      - ⑥ 浏览器标识：扩展构建期注入 `Browser`（esbuild `define`），服务端 `WebBrowseLogModels.BrowserRef`
        新列（迁移 `web_browser_ref`），历史 NULL 落「默认浏览器」。只到浏览器、不区分 profile。
      - `AppModels.Publisher`：monitor-windows 读文件版本资源 `CompanyName`，pipe 字段 `pub` 透传，
        历史行为 NULL（按路径规则退化）。会话检查点也带上，崩溃恢复不丢合并依据。
      - 合并组搬到服务端：`EntityMergeGroups/Members/Suppressions` 三张表 + `services/app_merge.rs` 判定
        （客户端 `AppMergePlanner` 的移植 + 发布者守卫）；自动组首次查询时落库，解散写 suppression。
        设置页「子母程序合并」卡片改读服务端，解散 / 恢复走 `/api/entity/merge-groups/*`，本地 `app.merge.json` 下线。
      - `/api/entity/list`：应用与网站扣成互斥的一行一行（浏览器会话区间求交），
        关闭合并时浏览器行只剩余额（「浏览器内其他页面」差额行），打开时站点行消失、浏览器拿回完整时长；
        总额两种视图都等于「应用总时长 + 未对齐的站点时长」。
      - 概览页头部「独立显示 / 并入浏览器」开关（`General.WebBrowserMerged`，配置 v6）；
        设置 → 常规新增「默认浏览器」（`General.DefaultBrowser`，候选来自 `/api/entity/browsers`）。
      - `fork.10.5`（已发布）：`/api/entity/hours`（实体 × 时段，hour/day/month 桶）+ `take` Top N +
        分类页切实体数据源 + 实体接口失败提示条 + 差额行继承浏览器分类。
      - `fork.10.6`（已发布）：`/api/entity/summary`，统计 / 详细两页切实体数据源（`browserMerged=false`）+
        异常提示横幅，柱状图下钻与年视图时钟环复用 `hours` 矩阵。
      - `fork.10.7`（已发布）：N5 分类页「分类 / 应用」视图切换 + 实体快捷打标签与折叠空分类；子母程序手动创建/编辑合并组（Kind=mixed、主实体指定）；全站各页面统一实体对齐与右键上下文菜单。
- [ ] **N7** 分类页（发现页）信息密度（方案见 `PLAN-2026-11.md` 三·五，`fork.8.5`）
      - [x] **N7-a 卡片瘦身**（已实现，未发版）：150×150 方块 → 200×92 扁卡片，一屏 6 列；
            卡上新增「今日时长 + 占比条 + 占比百分比」，应用数降为次要信息；目录/URL 匹配从药丸改成图标右上角小圆点。
            数据：应用侧 `api/data/category-hours`、网站侧 `api/webdata/browse-category-statistics`，
            一次请求拿全，本地按分类求和；每分类的应用数请求从串行改成并发。
            排序：有数据的按时长降序，0 时长收在后面按名称排。算术抽到 `Librarys/CategoryCardStats`，8 个单测覆盖。
      - [x] **N7-b 紧凑 / 卡片视图切换**（已实现，未发版）：标题栏右侧加「紧凑 / 卡片」`TabSwitch`；
            卡片抽成 `Controls/Category/CategoryCard`（应用与网站共用一份模板），形态由 `IsCard` 换模板——
            换模板不改 DataTemplate，列表项容器不重建，所以**滚动位置与选中态都不会丢**。
            紧凑 200×92、卡片 168×148（图标 40、时长 20px）。选择走 `[PageState]`，返回导航时保持
            （注意 `TryRestoreState` 只在**返回导航**时生效，从侧边栏切页会回到默认值，这是既有设计）。
      - [x] **N7-c 排序菜单**（已实现，未发版）：标题栏「排序」开关，三种口径——
            时长（默认，0 时长收尾按名称）、名称（纯字典序，中文即拼音）、数量（应用数 / 站点数降序，空分类收尾）。
            排序只对已有卡片做 `Move` 重排，不重拉数据、不重建容器，切换是瞬时的，选中项与滚动位置都不动；
            选择走 `[PageState]`。纯函数 `CategoryCardStats.Sort` / `ReorderInPlace` + 6 个单测。
            另修了一个 N7-b 留下的坑：视图开关首次进入时没有任何一项被选中
            （`TryRestoreState` 只在返回导航时恢复），现在视图与排序在模型构造时就有默认值。
            「空分类折叠成一组」**未做**，理由见 `PLAN-2026-11.md` 三·五
- [x] **N5** 分类页「分类 / 应用」视图切换 + 应用视图快速打标签（已实现，未发版）
- [x] **G11** 自绘图表 Tab 焦点——核验结论：控件层面已可达（`ColumnChart` / `DonutChart` /
      `StackedBarChart` / `ClockRing` 均有 `Focusable` + `IsTabStop` + 焦点重绘），
      新增 `ChartTabNavigationTests` 4 个用例锁住 Tab / Shift+Tab 进出图表的行为。
      热力网格 `HeatGrid` 仍只支持鼠标悬停（没有键盘交互路径，加进 Tab 序列只会制造一个死焦点）。

前置：先写《应用实体模型》文档，再动 N6 / N2 / N5。
（已写：[`docs/ENTITY-MODEL.md`](ENTITY-MODEL.md) + [关系图](ENTITY-MODEL.html)，2026-11-11 定稿；N2 开工前还有 3 项待决策）

### M7 · 分类体系统一、设置重构、数据同步与 UI 重做（N8–N13 / N15 已落地，未发版）

> 详细优先级、依赖关系与发版切分见 [`docs/PLAN-2026-11.md`](PLAN-2026-11.md)（N8–N15）。

- [x] **N8** 分类体系统一：分类页面改为应用与网站共享一套分类标签（已实现，未发版）
- [x] **N9** 预设常用分类标签：内置 9 个常用默认分类，省去用户从 0 逐个创建（已实现，未发版）
- [x] **N10** SVG 分类图标库：自绘 32 枚风格统一的 SVG 分类图标并完成存量迁移（已实现，未发版）
- [x] **N11** 典型网站/应用自动归类规则：内置常见站点/应用匹配规则 + 手动一键「智能归类」（已实现，未发版）
- [x] **N13** 应用内平滑滚动：为应用内滚动容器补齐帧同步指数阻尼平滑滚动，兼容 `Motion.IsEnabled`（已实现，未发版）
- [x] **N12** 设置页重构与归类重整（已实现，未发版）：
      1. 顶部 5 个独立切换分页（常规 / 外观 / 统计与规则 / 数据 / 关于），切页复位滚动；
      2. 清理原 Taix 遗留项（仓库更新地址对齐 `Purestblues/shijian`、版本号保留 `-fork.X`、插件说明链接指向仓库扩展目录）；
      3. 重新梳理大类与设置项归属，`SettingPanel` 支持按组过滤且同步深拷贝防互相覆盖。
- [x] **N15** 永久记录 + 数据导入导出 + WebDAV 云同步（已实现，未发版）：
      1. [x] 默认永久保留（移除界面 1 年保留选项与服务端 `AppSessions` 过期清理，已实现，未发版）；
      2. [x] 支持数据自由导入/导出（`.shijian-backup` 基于 SQLite `VACUUM INTO` 一致性快照 + `manifest.json` + SHA-256 校验 + 图标打包，支持 `merge`/`replace` 与导入前本地自动回滚快照，保留可选 CSV 表格导出）；
      3. [x] 新增 WebDAV 云同步功能（支持坚果云/Nextcloud 等标准 WebDAV 服务器测试连接、手动/启动/每小时/每天定时同步、跨设备增量合并与云端历史选择恢复）。
- [ ] **N14** 全站 UI 设计语言重做：重做卡片设计、圆角、材质、Dock 栏样式、字体与配色等整体设计语言（`M7.5` 实测缺陷已收口，待单开大版本推进）

### M7.5 · `/gui-verify` 全功能实测缺陷收口（`fork.14.5`，已完成，未发版）

> 对应 2026-11-14 全功能模拟点击与输入验证报告（完整上下文与改进方案见 [`docs/PLAN-2026-11.md`](PLAN-2026-11.md) 第九节），已完成修复、回归测试与 `/gui-verify` 实机复验。

- [x] **G14 [P0]** 修复概览页「今日」`IndexPageViewModel.LoadRangeAsync`（`BuildOverviewRankings` `Skip`/`Take`）偶发 `ArgumentOutOfRangeException`：加非负与区间边界兜底、异常提示兜底与单测
- [x] **G15 [P1]** 修复统计页 `ColumnChart` 固定高度裁切多行图例：放开 `Height` 自适应并随图例行数扩展容器高度
- [x] **G16 [P1]** 修复 `ColumnChart` 参考线（`midY` / `avgY` / `Maximum`）标签垂直碰撞重叠：引入标签避让偏移逻辑
- [x] **G17 [P1]** 修复详细页年视图 `HeatGrid` 12 行（`1月`–`12月`）行高过密与标签挤压：设定最小可读行高与滚动布局配合
- [x] **G18 [P1]** 修复 `选择应用` / `选择站点` 弹窗搜索：站点与分类弹窗统一支持子串实时过滤，并在构建候选项时清洗空白名称与重复项
- [x] **G19 [P1]** 修复侧边栏高亮不同步：统一设置项导航标识与 `MainViewModel.SyncNavigationSelection`，切换设置页/二级详情页/`Ctrl+K` 跳页时正确同步或清除高亮
- [x] **G20 [P2]** 修复概览页「自定义」日期 `DateSelect` 居中胶囊点击弹出日历面板问题
- [x] **G21 [P2]** 统一「分类 → 应用与网站」实体图标回退并优化 `Imager` 无效文件预校验与失败键缓存，消除 `NullReferenceException` 降级日志噪音
- [x] **G22 [P2]** 收口 3 处界面状态/文案细节：① 合并组选满 2 项即时清除红字报错；② WebDAV 开关补齐中文 `OnText`（`开`）/ `OffText`（`关`）；③ `Ctrl+K` 底栏去除重复键名文本

### M4 · 工程化（3/4 完成）

- [x] 增加测试工程（xunit），先覆盖纯逻辑
      新建 `ShiJian.Tests`（xunit v3 + `Avalonia.Headless.XUnit`，因为 Avalonia 12 的 headless
      包绑的是 xunit v3）。纯逻辑覆盖 `ChartColorHelper`（对比度 / 兜底 / 透明度）、
      `ChartDataMapper`（三种映射、排序、徽章、UTC→本地）、`ApiAppConfig.DetectChanges`
      （每条配置路径都钉住，防「新增配置项忘了登记」）、`WindowStatePolicy` / `WindowSnapshot`。
      headless 覆盖动效开关的摘除/还原（含关闭后新实例化的元素）与 `EmptyData` 的四种状态；
      headless 顺带验证了整套主题能正常加载——主题里再出现嵌套选择器问题会直接挂测试。
- [x] `release.yml` 打包前加一步 `dotnet test`，失败不发布
      新增独立 `test` job（ubuntu，先跑），两个打包 job 都 `needs: test`。
- [x] 发版半自动
      `script/prepare-release.ps1`：按上一个 tag 自动递增版本号（`fork.3` → `fork.4`），
      把区间提交按「新增 / 修复 / 其他」分类生成草稿（默认写 `.tmp/release-notes-draft.md`，
      加 `-Apply` 才覆盖 `release_notes.txt`）；`release.ps1` 的 `-Version` 也改成可省略，
      不传就自动递增。人只负责补一句小结。
- [ ] macOS 产物实机验证（暂缓：手边没有 Apple Silicon 机器）

## 优先级建议

**先 M1，再 M2；M4 的最小版本（只覆盖配色与聚合逻辑）建议在 M1 结束后立刻插入。**

理由：

- M1 全是「看起来像 bug」的问题，改动面小、体验收益直接
- M2 依赖 M1 的配色基础，且改动会扩散到多个页面，先有测试兜底更稳
- M4 越晚做，M2 的改动越难补测

## 明确不做

| 想法 | 原因 |
| --- | --- |
| 重写 Rust 服务端 | 现有三进程内存占用已很低，重写风险远大于收益 |
| 自研图表库 | 现有自绘控件够用，引入复杂度没有必要 |
| 插件市场 / 生态建设 | 个人使用的 fork，投入产出不成立 |
| 追上游所有分支 | 上游 11 个分支多为已合并历史，只需对齐 `upstream/main` |

## 发版节奏与版本命名规则演进

### 版本命名规则重大变更（2026-11-14）

- **规则切换**：因 **N14 里程碑（全站 UI 设计语言与前端架构整体重构）** 彻底落地，全站界面完全替换为 Nord 极简素雅设计语言，且已合流统一实体模型（N2/N8）、SVG 图标库（N10）、智能自动归类（N11）、设置页重构（N12）、应用内平滑滚动（N13）、永久数据记录与 WebDAV 快照备份（N15）以及 9 项实测缺陷收口（G14–G22），软件已具备全新形态。
- **告别 fork 序列**：自此版本起，**正式废止 `v1.3.4-fork.X` 命名规则**。
- **全新 2.0 正式版规则**：
  - 本版本正式命名为 **`2.0` 正式版**（Git Tag 为 `v2.0`，产物名为 `ShiJian-2.0-win-x64-Setup.exe` 等）。
  - 后续日常发版遵循 **两段式主次版本号递增**：如 **`2.1`、`2.2`、`2.3`** 等；
  - 若遇紧急突发热修复补丁，采用三段式补丁版本（如 `2.0.1`）。

### 历史发版节点

- 历史 fork 里程碑：`fork.2` = M1 + M2 + M3 前两项，`fork.3` = M3 收尾（动效开关、键盘导航、空态），`fork.4` = M4 工程化（测试 + CI 拦截 + 发版半自动），`fork.5` = M5 详细页看板，`fork.6` = R1 改名（Taix → 时见 / ShiJian，含数据迁移），`fork.7` = 视觉体系 v1（卡片/配色/间距/光晕）+ 动效 v2 + G11/G12 修复，`fork.8` = G13/token 化/N7-a，`fork.8.5` = N7-b/c，`fork.9` = N6 子母程序合并，`fork.10`–`fork.10.7` = N2 实体对齐。
- **2.0 正式版**：汇集 N8–N13、N15、G14–G22 收口批及 N14 前端全面重构，标志着 ShiJian 独立大版本正式成熟。
- 每条命令即可发布：`.\script\release.ps1 -Version 2.0 -Commit -Watch`（版本号可省略，脚本会自动递增，如 2.0 -> 2.1）
- 同步上游：已停。当前已是独立分支并演进为 2.0 正式版体系。

## 环境速查

| 事项 | 命令 / 说明 |
| --- | --- |
| 本地跑改动后的客户端 | `.\script\dev-run.ps1`（自动补齐图标目录，直连本机 shijian-server） |
| 发版 | `.\script\prepare-release.ps1 [-Apply]` 生成草稿 → `.\script\release.ps1 [-Commit] [-Watch]`（版本号可省略，自动递增） |
| 发版前置 | 必须用 **pwsh 7** 跑脚本（Windows PowerShell 5.1 会把 UTF-8 中文按 GBK 解析而报错），并带 `HTTPS_PROXY` 让 push / gh 走代理 |
| 跑测试 | `dotnet test ShiJian.Tests/ShiJian.Tests.csproj -c Release`（CI 打包前会先跑） |
| 验证 Rust 改动 | `gh workflow run rust-check.yml -R Purestblues/shijian`（server `cargo test` + monitor `cargo check`，约 3 分钟）。本机没有 MSVC 链接器，`cargo` 会调到 MSYS 的 `link.exe` 直接失败 |
| 本地能否打 Setup.exe | 不能：安装器（cargo）与客户端 AOT 都需要 MSVC 链接器（VS Build Tools），故统一走 GitHub Actions |
| fork 上 tag 触发 Actions | 不会触发，`release.ps1` 内部改用 `gh workflow run --ref <tag>` |
