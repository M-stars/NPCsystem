# SocialSimulation

Unity 6 单机 3D 社会人群模拟工程。通用内核管理身份、地区、资源、活动、日程、关系、经济、规范、知识、治理、承诺、决策和存档；领域模块在同一世界时钟中组合。交付样例包含家庭、校园、工作交易与社团，系统的内容包和运行接口可用于其他场景。新的社会语义仍需对应领域执行器与内容配置。

## 开始使用

1. 用 Unity 6000.6.2f1 打开工程，载入 `Assets/SocialSimulation/Domains/Combined/CombinedSocialWorld.unity`。
2. 进入 Play Mode，在 Game 视图使用暂停、单步、速度和 Save/Load 控件；打开 `Tools/Social Simulation/Runtime Observer` 观察单个 NPC 的状态与事件。
3. 使用 `SocialSimulation/Samples/Validate Combined World` 检查样例的身份、层级和 NavMesh。重新生成场景用 `SocialSimulation/Samples/Generate Combined World`，这会覆盖 `SocialSimulation Generated World` 根节点下的编辑。

创建内容时先阅读 [NPC 与地区标准包](Docs/标准资产包.md)。`Tools/Social Simulation/Content Catalog` 对单包和已注册目录做校验；编辑图使用 `Tools/Social Simulation/Graph Workbench` 与 `Tools/Social Simulation/Social Structure Workbench`。

无校园示例为 `Assets/SocialSimulation/Domains/Combined/IndependentCommunity.unity`，运行家庭照护、工作领薪、购买和社团活动；7 名角色中包含 Humanoid 与 Generic 两种骨骼。两个场景共享标准内容包及动作运行接口。

## 文档

- [使用说明](Docs/使用说明.md)：样例运行、观察、注册、图工作台、存档与测试入口。
- [NPC 与地区标准包](Docs/标准资产包.md)：目录、Prefab 层级、ID、校验与注册合同。
- [扩展指南](Docs/扩展指南.md)：服务边界、领域接入、动作、图版本及测试建议。
- [知识与信息传播](Docs/知识与信息传播.md)：个人已知范围、来源、渠道、转述与遗忘。
- [组织治理与承诺](Docs/组织治理与承诺.md)：授权请求、独立接受、履约、违约与共同终止。
- [人格原型](Docs/人格原型.md)：稳定生成个体差异与相关维度。
- [运行观察器](Docs/运行观察器.md)：绑定世界、个人知识筛选、决策依据与历史诊断。
- [角色与动画资源](Docs/资源与授权.md)：已导入 Kenney/KayKit 资源与其他可选来源。
- [验证记录](Docs/验证记录.md)：实际执行的检查、结果和性能口径。
- [当前进度](Docs/当前工作进度.md)：接续位置与尚未关闭的验收要求。

当前 Core 快照为 **v3**，Unity 运行存档为 **v5**，旧开发存档明确拒绝，尚无自动迁移。最新包依赖协议 EditMode 为 2026-09-30 **343/343** 通过，真实内容目录 13 个包联合校验通过。当前完整 PlayMode 正在执行；上一批完整结果为 **15/15**。Observer 已验证个人知识筛选、跨世界同 ID 隔离、决策依据和两种窗口布局。上一批 Windows 构建和双场景 Player 已通过；新包协议的目标平台结果仍待更新，见验证记录。

重复验证入口为 [Validate-SocialSimulation.ps1](Tools/Validate-SocialSimulation.ps1)，支持 EditMode、PlayMode、Build、Player 与 All。使用步骤及报告范围见[使用说明](Docs/使用说明.md#重复执行验证)；各阶段及连续不同筛选均已有本批通过报告；后续源码需使用新的报告标签重新验证。

样例和性能测量不能直接替代目标产品的场景压测。集成新领域后，应按[验证记录](Docs/验证记录.md)中的口径重新运行相关检查。
