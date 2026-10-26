using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Opt-in standalone player check; writes machine-readable evidence and quits.</summary>
    public sealed class SocialPlayerSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class SceneResult
        {
            public string Scene;
            public int NavMeshVertices;
            public int Npcs;
            public int AgentsOnMesh;
            public bool TravelAccepted;
            public string TravelStatus;
            public bool SaveRestored;
            public double SimulatedTime;
            public string Error;
        }

        [Serializable]
        private sealed class Report
        {
            public string UnityVersion;
            public bool Passed;
            public int RuntimeErrorCount;
            public List<string> RuntimeErrors = new List<string>();
            public List<SceneResult> Scenes = new List<SceneResult>();
        }

        private string outputPath;
        private static readonly object errorLock = new object();
        private static readonly List<string> capturedErrors = new List<string>();
        private static int errorCount;

        private static string RequestedOutput()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-socialSmokeOutput") return args[i + 1];
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ObserveStartup()
        {
            if (RequestedOutput() == null) return;
            Application.logMessageReceivedThreaded -= CaptureError;
            lock (errorLock)
            {
                capturedErrors.Clear();
                errorCount = 0;
            }
            Application.logMessageReceivedThreaded += CaptureError;
        }

        private static void CaptureError(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert &&
                !message.Contains("Failed to create agent")) return;
            lock (errorLock)
            {
                errorCount++;
                if (capturedErrors.Count < 20) capturedErrors.Add(message);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void LaunchIfRequested()
        {
            var path = RequestedOutput();
            if (path == null) return;
            var host = new GameObject("Social Player Smoke");
            DontDestroyOnLoad(host);
            host.AddComponent<SocialPlayerSmoke>().outputPath = path;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            var report = new Report { UnityVersion = Application.unityVersion, Passed = true };
            for (var sceneIndex = 0; sceneIndex < 2; sceneIndex++)
            {
                if (sceneIndex > 0) yield return SceneManager.LoadSceneAsync(sceneIndex, LoadSceneMode.Single);
                yield return null;
                yield return null;
                var result = new SceneResult { Scene = SceneManager.GetActiveScene().name };
                report.Scenes.Add(result);
                var runner = FindAnyObjectByType<SocialWorldRunner>();
                if (runner == null || runner.World == null || runner.IsFaulted)
                {
                    result.Error = runner == null ? "Missing world runner." : runner.FaultMessage ?? "World failed to initialize.";
                    report.Passed = false;
                    continue;
                }
                result.NavMeshVertices = NavMesh.CalculateTriangulation().vertices.Length;
                result.Npcs = runner.NpcViews.Count;
                foreach (var npc in runner.NpcViews.Values)
                {
                    var agent = npc.GetComponent<NavMeshAgent>();
                    if (agent != null && agent.isOnNavMesh) result.AgentsOnMesh++;
                }
                if (result.AgentsOnMesh != result.Npcs)
                {
                    result.Error = "Only " + result.AgentsOnMesh + " of " + result.Npcs + " agents are bound to the NavMesh.";
                    report.Passed = false;
                }
                if (result.NavMeshVertices == 0 || result.Npcs == 0)
                {
                    result.Error = "No baked NavMesh or NPCs in the Player scene.";
                    report.Passed = false;
                    continue;
                }
                result.TravelAccepted = runner.RequestTravel("npc.worker.02", "trade.shop.corner", "queue", "player.smoke", 900);
                if (!result.TravelAccepted)
                {
                    result.Error = "The real NavMesh travel request was rejected.";
                    report.Passed = false;
                    continue;
                }
                var deadline = Time.realtimeSinceStartup + 45f;
                SocialNavigationStatus status;
                do
                {
                    yield return null;
                    status = runner.GetTravelStatus("npc.worker.02", "trade.shop.corner", "player.smoke");
                }
                while (status == SocialNavigationStatus.InProgress && Time.realtimeSinceStartup < deadline && !runner.IsFaulted);
                result.TravelStatus = status.ToString();
                result.SimulatedTime = runner.World.Time;
                if (status != SocialNavigationStatus.Succeeded || runner.IsFaulted)
                {
                    result.Error = runner.FaultMessage ?? "Travel did not reach a committed arrival within 45 seconds.";
                    report.Passed = false;
                }
                else
                {
                    try
                    {
                        runner.SetPaused(true);
                        var time = runner.World.Time;
                        var worldId = runner.World.WorldId;
                        runner.LoadFromJson(runner.SaveToJson());
                        result.SaveRestored = !runner.IsFaulted && runner.World.Time == time &&
                            runner.World.WorldId == worldId && runner.World.Agents.Count == result.Npcs;
                        if (!result.SaveRestored) throw new InvalidOperationException("Player save round trip changed world identity or time.");
                    }
                    catch (Exception error)
                    {
                        result.Error = error.ToString();
                        report.Passed = false;
                    }
                }
            }
            // Keep observing errors through the final scene's OnDisable/OnDestroy callbacks.
            // Unity requires another loaded scene before the last gameplay scene can be unloaded.
            var completedScene = SceneManager.GetActiveScene();
            var cleanupScene = SceneManager.CreateScene("Social Player Smoke Cleanup");
            SceneManager.SetActiveScene(cleanupScene);
            var unload = SceneManager.UnloadSceneAsync(completedScene);
            if (unload == null) Debug.LogError("Player smoke could not unload the final test scene.");
            else yield return unload;
            yield return null;
            if (completedScene.isLoaded) Debug.LogError("The final Player test scene remains loaded after cleanup.");

            Application.logMessageReceivedThreaded -= CaptureError;
            lock (errorLock)
            {
                report.RuntimeErrorCount = errorCount;
                report.RuntimeErrors.AddRange(capturedErrors);
            }
            if (report.RuntimeErrorCount > 0) report.Passed = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                report.Passed = false;
            }
            Debug.Log("Social Player Smoke: " + (report.Passed ? "passed" : "failed"));
            Application.Quit(report.Passed ? 0 : 1);
        }
    }
}
