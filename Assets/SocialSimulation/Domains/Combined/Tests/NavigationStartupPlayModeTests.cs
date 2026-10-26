using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.UnityRuntime;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace SocialSimulation.Domains.Combined.Tests
{
    public sealed class NavigationStartupPlayModeTests
    {
        [UnityTest]
        public IEnumerator BothScenesBindAfterSurfaceLoadAndNpcReenableWithoutStartupErrors()
        {
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (message.Contains("Failed to create agent")) errors.Add(message);
            };
            Application.logMessageReceived += capture;
            try
            {
                foreach (var scene in new[] { "CombinedSocialWorld", "IndependentCommunity" })
                {
                    yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                    yield return null;
                    var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
                    Assert.IsNotNull(runner);
                    Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
                    runner.SetPaused(true);
                    foreach (var npc in runner.NpcViews.Values)
                    {
                        var agent = npc.GetComponent<NavMeshAgent>();
                        Assert.IsTrue(agent.enabled && agent.isOnNavMesh, scene + ": " + npc.NpcInstanceId);
                    }
                    var worker = runner.NpcViews["npc.worker.02"];
                    var navigator = worker.GetComponent<SocialNpcNavigation>();
                    var workerAgent = worker.GetComponent<NavMeshAgent>();
                    worker.gameObject.SetActive(false);
                    Assert.IsFalse(workerAgent.enabled, "Inactive navigation must release its native agent.");
                    worker.gameObject.SetActive(true);
                    yield return null;
                    Assert.IsTrue(workerAgent.enabled && workerAgent.isOnNavMesh);
                    Assert.IsTrue(workerAgent.isStopped, "Reactivation preserves simulation pause.");
                    Assert.AreEqual(SocialNavigationStatus.InProgress,
                        navigator.RequestMove(worker.transform.position, out _));
                    navigator.CancelMove();
                    var saved = runner.SaveToJson();
                    var savedPosition = worker.transform.position;
                    worker.gameObject.SetActive(false);
                    worker.transform.position = savedPosition + Vector3.right * 10000;
                    worker.gameObject.SetActive(true);
                    yield return null;
                    Assert.IsFalse(workerAgent.enabled, "An off-mesh spawn must not create a native agent.");
                    Assert.IsTrue(navigator.CanRestorePose(savedPosition));
                    runner.LoadFromJson(saved);
                    Assert.Less(Vector3.Distance(worker.transform.position, savedPosition), 0.1f);
                    Assert.IsTrue(workerAgent.enabled && workerAgent.isOnNavMesh);
                    Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
                }
                Assert.IsEmpty(errors, "Scene startup must wait for registered NavMesh data.");
            }
            finally { Application.logMessageReceived -= capture; }
        }
    }
}
