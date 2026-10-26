# Unity 工程上下文

分析日期：2026-09-26。工程目录：`D:/unity_hub/NPCsystem`。当前目录不是 Git 仓库，故没有可记录的提交号。

## 已确认环境

- Unity Editor：6000.6.2f1；URP 17.7.0。
- 包：AI Navigation 2.0.14、Input System 1.20.0、Unity Test Framework 1.8.0、uGUI 2.6.0。
- 单机 3D；逻辑以 `SimulationWorld` 固定 Tick 驱动。`Core` 程序集不依赖 UnityEngine/UnityEditor。
- 当前工程原先只有 `SampleScene` 与模板内容，SocialSimulation 为新增代码和资产。

## 代码和资产边界

| 程序集 | 职责 | 依赖方向 |
|---|---|---|
| `SocialSimulation.Core` | ID、时钟、社会结构、资源、交易、会话、规范、决策、快照 | 无 UnityEngine 引用 |
| `SocialSimulation.Graph` | 行为图、规则图、社会结构图文档与编译执行 | Core、UnityEngine 序列化 |
| `SocialSimulation.Actions` | Action 生命周期、资源作用域、图执行桥接与检查点 | Core、Graph、UnityRuntime |
| `SocialSimulation.UnityRuntime` | 场景身份、导航、统一 Update 与表现 | Core、Graph |
| `SocialSimulation.Domains` | 家庭、工作交易、校园示例配置与组合日程 | Core、Graph、UnityRuntime、Actions |
| `SocialSimulation.Authoring` | NPC/地区包资产和目录 | Core、UnityRuntime、Domains |
| `SocialSimulation.Editor.*` | 图画布、内容检查、样例生成、逻辑压测 | 仅 Editor |

`Assets/SocialSimulation/Domains/Combined/CombinedSocialWorld.unity` 为组合演示；`SampleScene.unity` 为原 Unity 模板场景。演示场景包含 8 地区、交互点、27 个具名 NPC 和烘焙 NavMesh。Kenney Animated Characters Protagonists 模型和 Idle/Run 动画的 Humanoid Avatar 已由 Unity Editor 验证。Blocky Characters 不满足 Humanoid 绑定，保留为 Generic 资源验证样本。

## 运行和测试

- 图工作台：`Tools/Social Simulation/Graph Workbench`。
- 社会结构工作台：`Tools/Social Simulation/Social Structure Workbench`。
- 内容目录：`Tools/Social Simulation/Content Catalog`。
- 场景生成与校验：`SocialSimulation/Samples/*`。
- 测试：Unity Test Runner 的 EditMode 程序集 `SocialSimulation.Core.Tests`、`SocialSimulation.Graph.Tests`。

## 已知边界

当前样例的 27 个可见 NPC 是功能演示；100～300 人逻辑压测与完整场景帧率应分开记录。动画演示仅对已导入的 Idle/Run 绑定负责；坐下、授课、交谈等可以完成逻辑效果，但尚无专用的人形片段时使用明示的表现降级。复杂经济、全量人口聚合、网络同步和大模型推理不属于此工程的首版运行链。
