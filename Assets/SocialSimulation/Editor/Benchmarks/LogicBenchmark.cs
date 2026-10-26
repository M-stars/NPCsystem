using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SocialSimulation.Core;
using UnityEditor;
using UnityEngine;

namespace SocialSimulation.Editor.Benchmarks
{
    /// <summary>Repeatable logical-load measurement; it deliberately excludes rendering and NavMesh costs.</summary>
    public static class LogicBenchmark
    {
        private sealed class BenchmarkSystem : IWorldSystem
        {
            private readonly string[] ids;
            private readonly string[] slots;
            public int Order => 50;

            public BenchmarkSystem(string[] ids, string[] slots)
            { this.ids = ids; this.slots = slots; }

            public void Tick(SimulationWorld world, double seconds)
            {
                var offset = (int)(world.TickIndex % 20);
                for (int i = offset; i < ids.Length; i += 20)
                {
                    var agentId = ids[i];
                    var agent = world.Agents[agentId];
                    var candidates = new List<GoalCandidate>(3)
                    {
                        new GoalCandidate { Id = agentId + ":rest", Kind = "Rest", Need = 1f - agent.Needs.Energy,
                            PersonalityFit = agent.Personality.Conscientiousness, PlaceId = "bench.hall" },
                        new GoalCandidate { Id = agentId + ":talk", Kind = "Social", Social = 1f - agent.Needs.Social,
                            PersonalityFit = agent.Personality.Extraversion, PlaceId = "bench.hall" },
                        new GoalCandidate { Id = agentId + ":walk", Kind = "Travel", Duty = 0.45f,
                            PersonalityFit = agent.Personality.Curiosity, PlaceId = "bench.hall" }
                    };
                    world.Decisions.Select(agentId, candidates);
                    if (world.TickIndex % 60 == 0)
                    {
                        var scope = "bench.action." + agentId + "." + world.TickIndex;
                        var token = world.Resources.TryAcquire(slots[i % slots.Length], scope, agentId, 10);
                        if (token != null) world.Resources.Release(token.Id, scope);
                    }
                }
            }
        }

        private sealed class CaseResult
        {
            public int Agents;
            public int Ticks;
            public double Milliseconds;
            public long AllocatedBytes;
            public long HeapDeltaBytes;
            public int Gen0Collections;
            public int Events;
            public int ActiveTokens;
            public int Diagnostics;
        }

        [MenuItem("Tools/Social Simulation/Run 100-300 NPC Logic Benchmark")]
        public static void RunAndSave()
        {
            var small = RunCase(100, 3600);
            var large = RunCase(300, 3600);
            var output = new StringBuilder();
            output.AppendLine("# SocialSimulation 逻辑压测");
            output.AppendLine();
            output.AppendLine("测量时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            output.AppendLine("Unity：" + Application.unityVersion + "；系统：" + SystemInfo.operatingSystem +
                "；CPU：" + SystemInfo.processorType + "；内存：" + SystemInfo.systemMemorySize + " MB");
            output.AppendLine("模式：Unity Editor 中同步运行纯逻辑；每秒一个 Tick，共 3600 Tick；20 Tick 错峰目标评分，60 Tick 座位申请/释放。" +
                "无渲染角色、导航请求或物理；下表不代表完整场景帧率。");
            output.AppendLine();
            output.AppendLine("| NPC | Tick | 总耗时 ms | 平均 ms/Tick | 当前线程托管分配 MB | 托管堆净变化 MB | Gen0 GC | 事件 | 活跃 Token | 诊断 |");
            output.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            AppendCase(output, small);
            AppendCase(output, large);
            var path = Path.GetFullPath("Docs/Benchmarks/LogicBenchmark.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, output.ToString(), Encoding.UTF8);
            UnityEngine.Debug.Log("SocialSimulation logic benchmark complete: " + path + "\n" + output);
            AssetDatabase.Refresh();
        }

        private static void AppendCase(StringBuilder output, CaseResult result)
        {
            output.Append("| ").Append(result.Agents).Append(" | ").Append(result.Ticks).Append(" | ")
                .Append(result.Milliseconds.ToString("F1")).Append(" | ")
                .Append((result.Milliseconds / result.Ticks).ToString("F4")).Append(" | ")
                .Append((result.AllocatedBytes / 1048576.0).ToString("F2")).Append(" | ")
                .Append((result.HeapDeltaBytes / 1048576.0).ToString("F2")).Append(" | ")
                .Append(result.Gen0Collections).Append(" | ").Append(result.Events).Append(" | ")
                .Append(result.ActiveTokens).Append(" | ").Append(result.Diagnostics).AppendLine(" |");
        }

        private static CaseResult RunCase(int count, int ticks)
        {
            var world = new SimulationWorld("benchmark." + count, 4242) { TickSeconds = 1, TraceCapacity = 512 };
            world.Places.Add("bench.hall", new PlaceState { Id = "bench.hall", Tag = "hall", Capacity = count });
            var slots = new string[24];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = "bench.seat." + i.ToString("D2");
                world.Resources.Slots.Add(slots[i], new ResourceSlot { Id = slots[i], PlaceId = "bench.hall", Capacity = 1 });
            }
            var ids = new string[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = "bench.person." + i.ToString("D4");
                world.AddAgent(new AgentState { Id = ids[i], DisplayName = ids[i], PlaceId = "bench.hall" });
            }
            world.AddSystem(new BenchmarkSystem(ids, slots));
            for (int i = 0; i < 100; i++) world.StepOne(); // JIT and caches
            GC.Collect();
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var beforeHeap = GC.GetTotalMemory(false);
            var beforeGen0 = GC.CollectionCount(0);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) world.StepOne();
            watch.Stop();
            var active = 0;
            foreach (var token in world.Resources.Tokens.Values) if (token.Active) active++;
            return new CaseResult
            {
                Agents = count, Ticks = ticks, Milliseconds = watch.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc,
                HeapDeltaBytes = GC.GetTotalMemory(false) - beforeHeap,
                Gen0Collections = GC.CollectionCount(0) - beforeGen0,
                Events = world.Trace.Count, ActiveTokens = active, Diagnostics = world.Diagnostics.Count
            };
        }
    }
}
