using System.Collections;
using NUnit.Framework;
using SocialSimulation.Core;
using SocialSimulation.Actions;
using SocialSimulation.UnityRuntime;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.WorkAndTrade;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace SocialSimulation.Domains.Combined.Tests
{
    public sealed class CombinedWorldPlayModeTests
    {
        private const string SceneName = "CombinedSocialWorld";

        [UnityTest]
        public IEnumerator GeneratedSceneRunsAllThreeDomainsWithHumanoidAndGenericActors()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            Assert.IsNotNull(runner);
            Assert.IsNotNull(runner.World);
            Assert.AreEqual(27, runner.World.Agents.Count);
            Assert.AreEqual(9, runner.RegionViews.Count);
            Assert.AreEqual(58, runner.PointViews.Count);
            var humanoids = 0;
            var generic = 0;
            foreach (var npc in runner.NpcViews.Values)
            {
                Assert.IsNotNull(npc.Animator, npc.NpcInstanceId);
                Assert.IsTrue(npc.Animator.avatar.isValid, npc.NpcInstanceId);
                if (npc.Animator.avatar.isHuman) humanoids++; else generic++;
                Assert.AreEqual(npc.PackageId, runner.World.Agents[npc.NpcInstanceId].TemplateId);
            }
            Assert.AreEqual(25, humanoids);
            Assert.AreEqual(2, generic);
            Assert.IsNotNull(Object.FindAnyObjectByType<CombinedDemoScenario>());
        }

        [UnityTest]
        public IEnumerator InvalidTravelAndIncompatibleSaveLeaveWorldUntouched()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            Assert.IsFalse(runner.RequestTravel("npc.worker.01", "trade.shop.corner", "missing.point"));
            var json = runner.SaveToJson();
            var before = runner.World;
            var invalid = json.Replace("\"SchemaVersion\":5", "\"SchemaVersion\":2");
            Assert.AreNotEqual(json, invalid);
            Assert.Throws<System.InvalidOperationException>(() => runner.LoadFromJson(invalid));
            invalid = json.Replace("\"Fingerprint\":\"", "\"Fingerprint\":\"corrupted");
            Assert.Throws<System.InvalidOperationException>(() => runner.LoadFromJson(invalid));
            Assert.AreSame(before, runner.World);
            Assert.AreEqual(json, runner.SaveToJson(), "Failed validation must not mutate the live world.");
        }

        [UnityTest]
        public IEnumerator HumanoidSeatedAnimationRemainsInLoopAndGenericCanWalk()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            var seated = runner.NpcViews["npc.teacher.01"].Animator;
            seated.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            seated.SetBool("Moving", false);
            seated.SetBool("ActionActive", true);
            seated.SetInteger("ActionCue", 2);
            for (var i = 0; i < 150; i++) seated.Update(0.05f);
            var state = seated.GetCurrentAnimatorStateInfo(0);
            Assert.IsTrue(state.IsName("Sit"), "Sitting must finish the entry animation and stay seated.");
            Assert.IsTrue(state.loop);
            Assert.Greater(state.normalizedTime, 1f, "Sitting should not continually restart SitDown.");
            var blocky = runner.NpcViews["npc.worker.01"].Animator;
            blocky.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            blocky.SetBool("Moving", true);
            blocky.SetBool("Running", false);
            for (var i = 0; i < 40; i++) blocky.Update(0.05f);
            Assert.IsTrue(blocky.GetCurrentAnimatorStateInfo(0).IsName("Walk"));
            Assert.IsNotNull(blocky.GetCurrentAnimatorClipInfo(0)[0].clip);
        }

        [UnityTest]
        public IEnumerator DestroyedNpcIsRemovedFromRuntimeAndReleasesReservations()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            const string id = "npc.worker.02";
            Object.Destroy(runner.NpcViews[id].gameObject);
            yield return null;
            yield return null;
            Assert.IsFalse(runner.NpcViews.ContainsKey(id));
            Assert.IsFalse(runner.World.Agents.ContainsKey(id));
            foreach (var token in runner.World.Resources.Tokens.Values)
                Assert.IsFalse(token.Active && token.AgentId == id);
            Assert.IsFalse(runner.IsFaulted);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MidTravelSaveLoadRestoresGraphResourcesAndContinuesCourse()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            Assert.IsNotNull(runner?.World);
            var deadline = Time.realtimeSinceStartup + 60f;
            string json = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (runner.World.Time >= 310 && runner.World.Time < 700)
                {
                    json = runner.SaveToJson();
                    if (json.Contains("\"TravelOrders\":[{")) break;
                }
                yield return null;
            }
            Assert.IsNotNull(json, "Could not capture NPCs travelling to class.");
            Assert.IsTrue(json.Contains("\"TravelOrders\":[{"), "Save omitted in-flight navigation.");
            var beforeTime = runner.World.Time;
            var beforeTokens = ActiveTokenCount(runner.World);
            runner.LoadFromJson(json);
            Assert.AreEqual(beforeTime, runner.World.Time, 0.000001);
            Assert.AreEqual(beforeTokens, ActiveTokenCount(runner.World));
            Assert.AreEqual(27, runner.World.Agents.Count);
            deadline = Time.realtimeSinceStartup + 125f;
            while (runner.World.Time < 2700 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.GreaterOrEqual(runner.World.Time, 2700);
            var startedA = false;
            var startedB = false;
            foreach (var item in runner.World.Trace)
                if (item.Type == "activity.started")
                {
                    startedA |= item.PlaceId == "campus.classroom.a";
                    startedB |= item.PlaceId == "campus.classroom.b";
                }
            Assert.IsTrue(startedA && startedB, "Both classrooms must start before 08:30.");
            var seatedB = 0;
            foreach (var session in runner.World.Activities.Sessions.Values)
                if (session.PlaceId == "campus.classroom.b" && session.Kind == "campus.course")
                    foreach (var person in session.Participants)
                        if (person.AgentId.StartsWith("npc.student") && person.Status == "Participating" &&
                            !string.IsNullOrEmpty(person.ResourceTokenId)) seatedB++;
            Assert.GreaterOrEqual(seatedB, 8, "Most accepted students in room B must reach and occupy seats.");
            var probe = AddRestoreProbe(runner);
            var animationFlags = new System.Collections.Generic.Dictionary<string, bool>();
            foreach (var npc in runner.NpcViews.Values)
            {
                animationFlags.Add(npc.NpcInstanceId, npc.Animator.GetBool("ActionActive"));
                npc.Animator.SetBool("ActionActive", false);
            }
            var rejectedSave = runner.SaveToJson();
            probe.FailValidation = true;
            var unchangedWorld = runner.World;
            Assert.Throws<System.InvalidOperationException>(() => runner.LoadFromJson(rejectedSave));
            Assert.AreSame(unchangedWorld, runner.World);
            foreach (var npc in runner.NpcViews.Values)
            {
                Assert.IsFalse(npc.Animator.GetBool("ActionActive"), "Read-only validation changed " + npc.NpcInstanceId + " animation.");
                npc.Animator.SetBool("ActionActive", animationFlags[npc.NpcInstanceId]);
            }
            probe.FailValidation = false;
            foreach (var item in runner.World.Trace)
                Assert.IsFalse(item.Type == "course.behavior_failed" && item.Payload == "NavigationFailed",
                    "Navigation failed for " + item.SubjectId);
            deadline = Time.realtimeSinceStartup + 145f;
            while (runner.World.Time < 6200 && Time.realtimeSinceStartup < deadline && !runner.IsFaulted) yield return null;
            Assert.GreaterOrEqual(runner.World.Time, 6200);
            var household = runner.GetComponent<HouseholdDomainModule>();
            var trade = runner.GetComponent<WorkAndTradeDomainModule>();
            Assert.IsTrue(household.CareCompleted);
            Assert.IsTrue(trade.WagePaid && trade.ClerkPurchaseCompleted && trade.StudentPurchaseCompleted && trade.ClubCompleted,
                "Combined day incomplete: wage=" + trade.WagePaid + ", purchase=" + trade.ClerkPurchaseCompleted +
                ", customer=" + trade.StudentPurchaseCompleted + ", club=" + trade.ClubCompleted);
            Assert.GreaterOrEqual(runner.World.Norms.Violations.Count, 1, "Observed corridor violations should use the scoped rule host.");
            Assert.IsEmpty(runner.World.Diagnostics);
        }

        [UnityTest]
        public IEnumerator ClosingAndUnloadingRegionReleasesResourcesAndRejectsAdmission()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            const string place = "trade.shop.corner";
            var token = runner.World.Resources.TryAcquire(place + "/queue", "test.region.scope", "npc.clerk.02", 60);
            Assert.IsNotNull(token);
            Assert.IsTrue(runner.RequestTravel("npc.worker.02", place, "queue", "region.test"));
            var session = runner.World.Activities.Create("region.test", "npc.clerk.02", place,
                runner.World.Time, runner.World.Time + 30, 60, 1, 2);
            runner.SetRegionOpen(place, false);
            Assert.IsFalse(token.Active);
            Assert.AreEqual("Cancelled", session.State);
            Assert.IsFalse(runner.CanEnterRegion("npc.worker.02", place, out _));
            Assert.IsFalse(runner.RequestTravel("npc.worker.02", place, "queue"));
            runner.SetRegionOpen(place, true);
            Assert.IsTrue(runner.CanEnterRegion("npc.worker.02", place, out _));
            Object.Destroy(runner.RegionViews[place].gameObject);
            yield return null;
            yield return null;
            Assert.IsFalse(runner.RegionViews.ContainsKey(place));
            Assert.IsFalse(runner.PointViews.ContainsKey(place + "/queue"));
            Assert.IsFalse(runner.World.Places[place].Open);
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
        }

        [UnityTest]
        public IEnumerator RegionClosurePreservesDisabledSlotsAndTombstoneSaveCanReload()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            const string region = "trade.shop.corner";
            const string point = region + "/queue";
            var original = runner.SaveToJson();
            runner.World.Resources.RemoveSlot(point);
            runner.SetRegionOpen(region, false);
            runner.SetRegionOpen(region, true);
            Assert.IsFalse(runner.World.Resources.Slots[point].Active, "Region reopening must preserve a disabled point.");
            Assert.IsTrue(runner.RequestTravel("npc.worker.02", region, "counter"));
            runner.UnregisterRegion(region);
            var unloaded = runner.SaveToJson();
            Assert.Throws<System.InvalidOperationException>(() => runner.LoadFromJson(original), "Loading a different loaded-region set requires explicit streaming.");
            runner.LoadFromJson(unloaded);
            Assert.IsFalse(runner.RegionViews.ContainsKey(region));
            Assert.IsFalse(runner.World.Places[region].Open);
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
        }

        [UnityTest]
        public IEnumerator ReopeningOutsideHoursAndCrossingAWholeCalendarPeriodCannotLeakTokens()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            var definition = runner.Content.Regions["trade.shop"];
            definition.OpeningPeriodSeconds = 60;
            definition.OpeningTimeOffsetSeconds = 0;
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 10 });
            const string place = "trade.shop.corner";
            const string point = place + "/queue";
            var token = runner.World.Resources.TryAcquire(point, "calendar.test", "npc.clerk.02", 180);
            Assert.IsNotNull(token);
            runner.World.TickSeconds = 60;
            Assert.IsTrue(runner.TryStepOne(out var error), error);
            Assert.IsFalse(token.Active, "A full-period Tick must still process the closing boundary.");
            runner.World.TickSeconds = 20;
            Assert.IsTrue(runner.TryStepOne(out error), error);
            runner.SetRegionOpen(place, false);
            runner.SetRegionOpen(place, true);
            Assert.IsTrue(runner.World.Resources.Slots[point].Active);
            Assert.IsFalse(runner.World.Resources.Slots[point].RegionAvailable);
            Assert.IsNull(runner.World.Resources.TryAcquire(point, "calendar.closed", "npc.clerk.02", 30));
        }

        [UnityTest]
        public IEnumerator PausedSingleStepExpiresTravelAndKeepsFailureAfterSaveLoad()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            runner.World.TickSeconds = 5;
            Assert.IsTrue(runner.RequestTravel("npc.worker.02", "trade.shop.corner", "queue", "bounded.travel", 10));
            Assert.IsTrue(runner.TryStepOne(out var error), error);
            runner.LoadFromJson(runner.SaveToJson());
            Assert.IsTrue(runner.TryStepOne(out error), error);
            var failures = 0;
            foreach (var entry in runner.World.Trace)
                if (entry.Type == "agent.travel_failed" && entry.Payload == "bounded.travel") failures++;
            Assert.AreEqual(1, failures, "Travel expires on the simulation Tick, including paused single steps.");
            Assert.AreEqual(SocialNavigationStatus.Failed, runner.GetTravelStatus("npc.worker.02", "trade.shop.corner", "bounded.travel"));
            runner.LoadFromJson(runner.SaveToJson());
            Assert.AreEqual(SocialNavigationStatus.Failed, runner.GetTravelStatus("npc.worker.02", "trade.shop.corner", "bounded.travel"));
            Assert.IsFalse(runner.SaveToJson().Contains("\"TravelOrders\":[{"));
        }

        [UnityTest]
        public IEnumerator UnloadingActiveClassroomPreservesOtherClassroomAndCanReloadImmediately()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            runner.World.TickSeconds = 5;
            while (runner.World.Time < 350) Assert.IsTrue(runner.TryStepOne(out var stepError), stepError);
            ISocialActorRuntimeDiagnostics campus = null;
            foreach (var component in runner.GetComponents<MonoBehaviour>())
                if (component.GetType().Name == "CampusDomainModule") campus = (ISocialActorRuntimeDiagnostics)component;
            Assert.IsNotNull(campus);
            Assert.IsNotEmpty(campus.GetActorRuntimeSnapshots("npc.student.01"));
            Assert.IsNotEmpty(campus.GetActorRuntimeSnapshots("npc.student.11"));
            runner.UnregisterRegion("campus.classroom.a");
            Assert.IsEmpty(campus.GetActorRuntimeSnapshots("npc.student.01"));
            Assert.IsNotEmpty(campus.GetActorRuntimeSnapshots("npc.student.11"));
            runner.LoadFromJson(runner.SaveToJson());
            Assert.IsTrue(runner.TryStepOne(out var error), error);
            Assert.IsEmpty(campus.GetActorRuntimeSnapshots("npc.student.01"));
            Assert.IsNotEmpty(campus.GetActorRuntimeSnapshots("npc.student.11"));
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
        }

        [UnityTest]
        public IEnumerator ActiveCareCanUnloadWhilePausedAndImmediatelyRestoreItsOwnSave()
        {
            yield return SceneManager.LoadSceneAsync("IndependentCommunity", LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            runner.World.TickSeconds = 5;
            while (runner.World.Time < 80) Assert.IsTrue(runner.TryStepOne(out var error), error);
            var active = false;
            foreach (var session in runner.World.Activities.Sessions.Values)
                if (session.Kind == "household.breakfast" && session.State != "Cancelled") active = true;
            Assert.IsTrue(active, "Exercise an active care graph before unloading.");
            runner.UnregisterRegion("household.home.river");
            runner.LoadFromJson(runner.SaveToJson());
            Assert.IsFalse(runner.GetComponent<HouseholdDomainModule>().CareCompleted);
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
            Assert.IsTrue(runner.TryStepOne(out var afterError), afterError);
            foreach (var token in runner.World.Resources.Tokens.Values)
                Assert.IsFalse(token.Active && token.SlotId.StartsWith("household.home.river/"));
        }

        [UnityTest]
        public IEnumerator TransientScenarioRestoreFailureRollsBackWorldAndNpcPoses()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            runner.SetPaused(true);
            var injection = AddRestoreProbe(runner);
            var json = runner.SaveToJson();
            var time = runner.World.Time;
            var position = runner.NpcViews["npc.clerk.01"].transform.position;
            injection.FailNext = true;
            Assert.Throws<System.InvalidOperationException>(() => runner.LoadFromJson(json));
            Assert.AreEqual(time, runner.World.Time);
            Assert.Less(Vector3.Distance(position, runner.NpcViews["npc.clerk.01"].transform.position), 0.1f);
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
            Assert.IsTrue(runner.World.Paused);
        }

        private static RestoreFailureProbe AddRestoreProbe(SocialWorldRunner runner)
        {
            var probe = runner.gameObject.AddComponent<RestoreFailureProbe>();
            var field = typeof(SocialWorldRunner).GetField("scenarioPersistences",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var scenarios = (System.Collections.Generic.Dictionary<string, ISocialScenarioPersistence>)field.GetValue(runner);
            scenarios.Add(typeof(RestoreFailureProbe).FullName, probe);
            return probe;
        }

        private static int ActiveTokenCount(SimulationWorld world)
        {
            var count = 0;
            foreach (var token in world.Resources.Tokens.Values) if (token.Active) count++;
            return count;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator IndependentCommunityCompletesDayAndRestoresActiveShiftWithoutCampus()
        {
            yield return SceneManager.LoadSceneAsync("IndependentCommunity", LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            var household = runner.GetComponent<HouseholdDomainModule>();
            var trade = runner.GetComponent<WorkAndTradeDomainModule>();
            Assert.IsNotNull(runner.World);
            Assert.AreEqual(7, runner.World.Agents.Count);
            Assert.AreEqual(4, runner.RegionViews.Count);
            Assert.IsFalse(runner.Content.Packages.ContainsKey("campus"));
            foreach (var module in runner.GetComponents<MonoBehaviour>())
                Assert.IsFalse(module.GetType().FullName.Contains(".Campus."));
            var restored = false;
            var deadline = Time.realtimeSinceStartup + 255f;
            while (runner.World.Time < 5800 && Time.realtimeSinceStartup < deadline && !runner.IsFaulted)
            {
                if (!restored && runner.World.Time >= 1800)
                {
                    var json = runner.SaveToJson();
                    var time = runner.World.Time;
                    runner.LoadFromJson(json);
                    Assert.AreEqual(time, runner.World.Time);
                    restored = true;
                }
                yield return null;
            }
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
            Assert.GreaterOrEqual(runner.World.Time, 5800);
            Assert.IsTrue(restored);
            Assert.IsTrue(household.CareCompleted, "照护应在独立社区完成。");
            Assert.IsTrue(trade.WagePaid, "工作完成后应领取一次工资。");
            Assert.IsTrue(trade.ClerkPurchaseCompleted, "同一照护者/雇员应完成购买。");
            Assert.IsTrue(trade.StudentPurchaseCompleted, "独立社区中的另一名顾客应完成购买。");
            Assert.IsTrue(trade.ClubCompleted, "社团活动应完成。");
            Assert.IsEmpty(runner.World.Diagnostics);
        }
    }

    public sealed class RestoreFailureProbe : MonoBehaviour, ISocialScenarioPersistence
    {
        public bool FailNext;
        public bool FailValidation;
        public void Initialize(SocialWorldRunner runner, SimulationWorld world) { }
        public string CaptureScenarioState() => "{\"probe\":1}";
        public void ValidateScenarioState(SocialWorldRunner runner, SimulationWorld world, string json)
        {
            if (FailValidation) throw new System.InvalidOperationException("Injected validation failure.");
        }
        public void RestoreScenarioState(SocialWorldRunner runner, SimulationWorld world, string json)
        {
            if (!FailNext) return;
            FailNext = false;
            throw new System.InvalidOperationException("Injected transient restore failure.");
        }
    }
}
