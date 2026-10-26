using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.Editor.Graph;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace SocialSimulation.Graph.Tests
{
    public sealed class GraphProductTests
    {
        private GraphDocument document;
        private GraphWorkbenchWindow window;
        private string assetPath;
        private GraphView View => (GraphView)typeof(GraphWorkbenchWindow).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        private object Invoke(string method, params object[] args) => typeof(GraphWorkbenchWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);

        [SetUp]
        public void SetUp()
        {
            document = ScriptableObject.CreateInstance<GraphDocument>();
            document.DocumentId = "test.product." + Guid.NewGuid().ToString("N");
            document.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root, Position = new Vector2(30, 80) });
            var wait = new GraphNode { Id = "wait", Type = GraphNodeType.Wait, Position = new Vector2(320, 80) };
            wait.Set("ticks", "2"); document.Nodes.Add(wait);
            document.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "wait", Order = 7 });
        }

        private void Open()
        {
            window = EditorWindow.CreateWindow<GraphWorkbenchWindow>(); window.CreateGUI(); window.Open(document);
        }

        private IEnumerator WaitForLayout()
        {
            window.position = new Rect(100, 100, 1100, 750);
            window.Show(); window.Repaint();
            int readyFrames = 0;
            for (int frame = 0; frame < 30; frame++)
            {
                yield return null;
                bool ready = View.graphElements.Where(item => !(item is Edge)).All(item =>
                {
                    var rect = item.GetPosition();
                    return !float.IsNaN(rect.x + rect.y + rect.width + rect.height) &&
                        !float.IsInfinity(rect.x + rect.y + rect.width + rect.height) && rect.width > 0 && rect.height > 0;
                });
                readyFrames = ready ? readyFrames + 1 : 0;
                if (readyFrames >= 2) yield break;
            }
            Assert.Fail("GraphView did not receive finite layout geometry within 30 Editor frames.");
        }

        private void Select(params string[] ids)
        {
            View.ClearSelection();
            foreach (var id in ids) View.AddToSelection(View.graphElements.Single(item => item.viewDataKey == id));
            Invoke("RefreshInspector");
        }

        private void AddLayout()
        {
            Invoke("AddNote", new Vector2(360, 300));
            Select("wait", document.Notes.Single().Id);
            Invoke("AddGroup", new Vector2(280, 40));
        }

        [TearDown]
        public void TearDown()
        {
            if (window != null) window.Close();
            if (assetPath != null) AssetDatabase.DeleteAsset(assetPath);
            else if (document != null) UnityEngine.Object.DestroyImmediate(document);
            Undo.ClearAll();
        }

        [UnityTest]
        public IEnumerator LayoutMetadataRoundTripsWithoutChangingExecutionRevision()
        {
            var before = GraphCompiler.Compile(document).BehaviorPlan.Revision;
            Open(); AddLayout();
            yield return WaitForLayout();
            var groupId = document.Groups.Single().Id; var noteId = document.Notes.Single().Id;
            Select(groupId); window.rootVisualElement.Q<TextField>("groupTitle").value = "出发前准备";
            Select(noteId); window.rootVisualElement.Q<TextField>("noteContents").value = "等待 2 Tick\n保留当前任务";
            var rect = new Rect(400, 350, 370, 220);
            var note = View.graphElements.Single(item => item.viewDataKey == noteId); note.SetPosition(rect);
            yield return WaitForLayout();
            View.graphViewChanged(new GraphViewChange { movedElements = new List<GraphElement> { note } });
            Assert.AreEqual(before, GraphCompiler.Compile(document).BehaviorPlan.Revision);
            assetPath = "Assets/SocialSimulation/Editor/Graph/Tests/__layout_" + Guid.NewGuid().ToString("N") + ".asset";
            AssetDatabase.CreateAsset(document, assetPath); Invoke("Save");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            document = AssetDatabase.LoadAssetAtPath<GraphDocument>(assetPath); window.Open(document);
            Assert.AreEqual("出发前准备", document.Groups.Single().Title);
            CollectionAssert.AreEquivalent(new[] { "wait", noteId }, document.Groups.Single().MemberIds);
            Assert.AreEqual("等待 2 Tick\n保留当前任务", document.Notes.Single().Contents);
            Assert.AreEqual(rect, document.Notes.Single().Position);
            Assert.AreEqual(7, document.Edges.Single().Order);
            Assert.AreEqual(before, GraphCompiler.Compile(document).BehaviorPlan.Revision);
            Assert.AreEqual(1, View.graphElements.OfType<Group>().Count());
            Assert.AreEqual(1, View.graphElements.OfType<StickyNote>().Count());
        }

        [Test]
        public void EditingBeforeFirstLayoutKeepsAuthoredPositionsFinite()
        {
            Open(); AddLayout();
            var note = document.Notes.Single(); var position = note.Position;
            var nodePosition = document.FindNode("wait").Position;
            Select(note.Id); window.rootVisualElement.Q<TextField>("noteContents").value = "布局完成前的编辑";
            var group = View.graphElements.OfType<Group>().Single();
            View.graphViewChanged(new GraphViewChange { movedElements = new List<GraphElement> { group } });
            Assert.AreEqual(position, document.Notes.Single().Position);
            Assert.AreEqual(nodePosition, document.FindNode("wait").Position);
            Assert.AreEqual("布局完成前的编辑", document.Notes.Single().Contents);
        }

        [Test]
        public void NoteEditingAndGroupMembershipSupportUndoRedo()
        {
            Open(); AddLayout(); var noteId = document.Notes.Single().Id;
            Select(noteId); Undo.IncrementCurrentGroup();
            window.rootVisualElement.Q<TextField>("noteContents").value = "新的说明";
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreEqual("", document.Notes.Single().Contents);
            Undo.PerformRedo(); Assert.AreEqual("新的说明", document.Notes.Single().Contents);
            Select(noteId); Undo.IncrementCurrentGroup(); Invoke("UngroupSelection");
            Assert.IsFalse(document.Groups.Single().MemberIds.Contains(noteId));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.IsTrue(document.Groups.Single().MemberIds.Contains(noteId));
            Undo.PerformRedo(); Assert.IsFalse(document.Groups.Single().MemberIds.Contains(noteId));
        }

        [Test]
        public void CopyingAGroupCopiesMembersWithFreshIdsAndIndependentContents()
        {
            Open(); AddLayout(); var originalGroup = document.Groups.Single(); var originalNote = document.Notes.Single();
            var selectedGroup = View.graphElements.Single(item => item.viewDataKey == originalGroup.Id);
            var json = View.serializeGraphElements(new[] { selectedGroup });
            View.unserializeAndPaste("Paste group", json);
            Assert.AreEqual(2, document.Groups.Count); Assert.AreEqual(2, document.Notes.Count);
            Assert.AreEqual(3, document.Nodes.Count);
            var copied = document.Groups.Single(item => item.Id != originalGroup.Id);
            Assert.AreEqual(2, copied.MemberIds.Count);
            Assert.IsFalse(copied.MemberIds.Any(originalGroup.MemberIds.Contains));
            var copiedNote = document.Notes.Single(item => item.Id != originalNote.Id);
            copiedNote.Contents = "副本"; Assert.AreEqual("", originalNote.Contents);
            Assert.AreEqual(originalNote.Position.position + new Vector2(40, 40), copiedNote.Position.position);
        }

        [Test]
        public void CopyingAnEmptyGroupOrNoteDoesNotRequireExecutionNodes()
        {
            Open(); Invoke("AddGroup", new Vector2(600, 500));
            var group = View.graphElements.OfType<Group>().Single();
            View.unserializeAndPaste("Paste empty group", View.serializeGraphElements(new[] { group }));
            Assert.AreEqual(2, document.Groups.Count); Assert.AreEqual(2, document.Nodes.Count);
            Invoke("AddNote", new Vector2(0, 400));
            var note = View.graphElements.OfType<StickyNote>().Single();
            View.unserializeAndPaste("Paste note", View.serializeGraphElements(new[] { note }));
            Assert.AreEqual(2, document.Notes.Count); Assert.AreEqual(2, document.Nodes.Count);
        }

        [Test]
        public void DeleteGroupPreservesMembersAndDeleteNoteCleansMembership()
        {
            Open(); AddLayout(); var groupId = document.Groups.Single().Id; var noteId = document.Notes.Single().Id;
            Select(groupId); Undo.IncrementCurrentGroup();
            View.deleteSelection("Delete group", GraphView.AskUser.DontAskUser);
            Assert.AreEqual(0, document.Groups.Count); Assert.AreEqual(2, document.Nodes.Count); Assert.AreEqual(1, document.Notes.Count);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.AreEqual(1, document.Groups.Count);
            Select(noteId); Undo.IncrementCurrentGroup(); View.deleteSelection("Delete note", GraphView.AskUser.DontAskUser);
            Assert.AreEqual(0, document.Notes.Count); CollectionAssert.AreEqual(new[] { "wait" }, document.Groups.Single().MemberIds);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreEqual(1, document.Notes.Count); Assert.IsTrue(document.Groups.Single().MemberIds.Contains(noteId));
        }

        [UnityTest]
        public IEnumerator DraggingGroupPersistsMembersAndOneUndoRestoresPositions()
        {
            Open(); AddLayout();
            yield return WaitForLayout();
            var nodePosition = document.FindNode("wait").Position;
            var notePosition = document.Notes.Single().Position;
            var group = View.graphElements.OfType<Group>().Single();
            var rect = group.GetPosition(); rect.position += new Vector2(120, 90);
            Undo.IncrementCurrentGroup(); group.SetPosition(rect);
            yield return WaitForLayout();
            View.graphViewChanged(new GraphViewChange { movedElements = new List<GraphElement> { group } });
            Assert.AreEqual(nodePosition + new Vector2(120, 90), document.FindNode("wait").Position);
            Assert.AreEqual(notePosition.position + new Vector2(120, 90), document.Notes.Single().Position.position);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreEqual(nodePosition, document.FindNode("wait").Position); Assert.AreEqual(notePosition, document.Notes.Single().Position);
        }

        [Test]
        public void NativeGroupAddAndRemoveCallbacksPersistMembership()
        {
            Open(); Invoke("AddGroup", new Vector2(200, 50));
            var group = View.graphElements.OfType<Group>().Single();
            var node = View.nodes.Single(item => item.viewDataKey == "wait");
            group.AddElement(node); CollectionAssert.AreEqual(new[] { "wait" }, document.Groups.Single().MemberIds);
            group.RemoveElement(node); Assert.AreEqual(0, document.Groups.Single().MemberIds.Count);
        }

        private static PackageManifest Template(bool presentation = true)
        {
            var value = new PackageManifest { Id = "npc.worker", Version = "1", Kind = "NPC", Body = new BodyProfile { Rig = "Generic" } };
            value.Capabilities.Add(new CapabilityDefinition { Id = "work", BodyConstraint = "Any", ExecutorId = "logic", RequiresPresentation = presentation });
            return value;
        }

        [TestCase("missingCapability", GraphCompatibilityState.MissingCapability)]
        [TestCase("missingExecutor", GraphCompatibilityState.MissingExecutor)]
        [TestCase("wrongRig", GraphCompatibilityState.IncompatibleBody)]
        [TestCase("requiredAnimation", GraphCompatibilityState.MissingRequiredPresentation)]
        [TestCase("optionalAnimation", GraphCompatibilityState.OptionalPresentationMissing)]
        [TestCase("available", GraphCompatibilityState.Available)]
        public void TemplateCompatibilityReportsTheActualMissingDependency(string variant, GraphCompatibilityState expected)
        {
            var template = Template(variant != "optionalAnimation");
            if (variant == "wrongRig") template.Capabilities[0].BodyConstraint = "Humanoid";
            if (variant == "missingCapability") template.Capabilities.Clear();
            if (variant == "available") template.AnimationBindings.Add(new AnimationBindingProfile { CapabilityId = "work", AnimatorState = "Work" });
            var action = new ActionDefinition { Id = "work.perform", CapabilityId = "work", ExecutorId = "logic" };
            var actual = GraphTemplateCompatibility.Evaluate(action, template, id => variant != "missingExecutor" && id == "logic");
            Assert.AreEqual(expected, actual.State);
            Assert.AreEqual(expected == GraphCompatibilityState.Available || expected == GraphCompatibilityState.OptionalPresentationMissing, actual.CanUse);
        }

        [Test]
        public void TemplateValidationFindsIncompatibleActionsInNestedSubgraphs()
        {
            var child = ScriptableObject.CreateInstance<GraphDocument>(); child.DocumentId = "nested";
            child.Nodes.Add(new GraphNode { Id = "nestedRoot", Type = GraphNodeType.Root });
            var action = new GraphNode { Id = "nestedAction", Type = GraphNodeType.Action }; action.Set("actionId", "work.perform");
            child.Nodes.Add(action); child.Edges.Add(new GraphEdge { FromNodeId = "nestedRoot", ToNodeId = "nestedAction" });
            document.FindNode("wait").Type = GraphNodeType.Subtree; document.FindNode("wait").Subgraph = child;
            try
            {
                var compiled = GraphCompiler.Compile(document); Assert.IsTrue(compiled.Success);
                var failures = GraphTemplateCompatibility.Validate(compiled.BehaviorPlan, Template(), id => null, id => true);
                Assert.AreEqual(1, failures.Count); Assert.AreEqual("nestedAction", failures[0].NodeId);
                Assert.AreEqual(GraphDiagnosticSeverity.Error, failures[0].Severity);
                StringAssert.Contains("MissingActionDefinition", failures[0].Code);
            }
            finally { UnityEngine.Object.DestroyImmediate(child); }
        }

        [Test]
        public void SelectedTemplateFiltersRegisteredActionsAndRejectsUnknownDraftBeforeApply()
        {
            var source = AssetDatabase.FindAssets("t:NpcPackageAsset").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<NpcPackageAsset>).First(item => item.Validate().Count == 0);
            var template = UnityEngine.Object.Instantiate(source);
            var catalog = ScriptableObject.CreateInstance<SocialContentCatalogAsset>(); catalog.NpcPackages.Add(template);
            var actions = (List<ActionDefinition>)typeof(NpcPackageAsset).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(template);
            actions.Add(new ActionDefinition { Id = "test.registered.walk", CapabilityId = "locomotion.walk", ExecutorId = "navmesh.move" });
            try
            {
                Open();
                window.rootVisualElement.Q<ObjectField>("graphContentCatalog").value = catalog;
                window.rootVisualElement.Q<ObjectField>("graphNpcTemplate").value = template;
                window.rootVisualElement.Q<Toggle>("compatibleActionsOnly").value = true;
                var button = window.rootVisualElement.Q<VisualElement>("palette").Query<Button>().ToList()
                    .Single(item => item.text.Contains("test.registered.walk"));
                Assert.IsTrue(button.enabledSelf);
                var wait = document.FindNode("wait"); wait.Type = GraphNodeType.Action; wait.Set("actionId", "test.registered.walk");
                window.Open(document);
                var valid = (List<GraphDiagnostic>)Invoke("TemplateDiagnostics", GraphCompiler.Compile(document));
                Assert.IsFalse(valid.Any(item => item.Severity == GraphDiagnosticSeverity.Error));
                wait.Set("actionId", "not.registered");
                Invoke("ApplyDocument", GraphApplyMode.ForNewInstances);
                StringAssert.Contains("模板兼容性未通过", window.rootVisualElement.Q<Label>("status").text);
                var slot = (GraphPlanSlot)typeof(GraphWorkbenchWindow).GetField("appliedPlan", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Assert.IsNull(slot.BehaviorPlan);
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(template); }
        }

        [Test]
        public void InvalidCatalogCannotSupplyActionsAndUnknownDomainActionsStayDisabled()
        {
            var domain = ScriptableObject.CreateInstance<SocialDomainPackageAsset>();
            domain.Configure("domain.test.unbound", "1", "未绑定领域", Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), new[] { "test.unbound.action" });
            var valid = ScriptableObject.CreateInstance<SocialContentCatalogAsset>(); valid.DomainPackages.Add(domain);
            var invalid = ScriptableObject.CreateInstance<SocialContentCatalogAsset>(); invalid.NpcPackages.Add(null);
            try
            {
                Assert.IsTrue(new GraphActionCatalog(valid).IsValid);
                Open();
                window.rootVisualElement.Q<ObjectField>("graphContentCatalog").value = valid;
                window.rootVisualElement.Q<ObjectField>("graphNpcTemplate").value = null;
                window.rootVisualElement.Q<Toggle>("compatibleActionsOnly").value = false;
                var unknown = window.rootVisualElement.Q<VisualElement>("palette").Query<Button>().ToList()
                    .FirstOrDefault(item => item.text.Contains("test.unbound.action"));
                Assert.NotNull(unknown); Assert.IsFalse(unknown.enabledSelf);
                StringAssert.Contains("缺少已注册", unknown.tooltip);
                var catalog = new GraphActionCatalog(invalid); Assert.IsFalse(catalog.IsValid);
                Assert.IsNull(catalog.ResolveAction("test.unbound.action"));
                window.rootVisualElement.Q<ObjectField>("graphContentCatalog").value = invalid;
                Assert.NotNull(window.rootVisualElement.Q<Label>("invalidActionCatalog"));
                Assert.IsFalse(window.rootVisualElement.Q<VisualElement>("palette").Query<Button>().ToList()
                    .Any(item => item.text.Contains("test.unbound.action")));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(invalid);
                UnityEngine.Object.DestroyImmediate(valid);
                UnityEngine.Object.DestroyImmediate(domain);
            }
        }
    }
}
