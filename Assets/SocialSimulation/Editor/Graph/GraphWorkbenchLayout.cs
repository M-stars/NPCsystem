using System;
using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Graph;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    public sealed partial class GraphWorkbenchWindow
    {
        private void ConfigureLayoutCallbacks()
        {
            graph.elementsAddedToGroup = (group, elements) => ChangeGroupMembership(group, elements, true);
            graph.elementsRemovedFromGroup = (group, elements) => ChangeGroupMembership(group, elements, false);
            graph.groupTitleChanged = (group, title) =>
            {
                if (rebuilding || document == null || !(group is SocialGroupView view) || view.Model.Title == title) return;
                Undo.RecordObject(document, "Rename graph group");
                view.Model.Title = title; LayoutChanged();
            };
            graph.NoteChanged = SaveNote;
            graph.elementResized = element =>
            {
                if (element is GraphElement resized)
                    SaveMovedLayout(new List<GraphElement> { resized },
                        () => Undo.RecordObject(document, "Resize graph note"));
            };
            graph.deleteSelection = (operation, ask) =>
            {
                var selected = graph.selection.OfType<GraphElement>().ToList();
                var nodeIds = new HashSet<string>(selected.OfType<SocialNodeView>().Select(item => item.Model.Id));
                selected.AddRange(graph.edges.Where(edge => nodeIds.Contains(edge.input?.node?.viewDataKey ?? "") ||
                    nodeIds.Contains(edge.output?.node?.viewDataKey ?? "")));
                graph.DeleteElements(selected.Distinct());
                RefreshInspector();
            };
        }

        private void LayoutChanged()
        {
            EditorUtility.SetDirty(document);
            if (status != null) status.text = "布局已修改 · 尚未保存（不改变执行版本）";
        }

        private void AddNote(Vector2 position)
        {
            if (document == null) return;
            Undo.RecordObject(document, "Add graph note");
            var note = new GraphNote { Id = Guid.NewGuid().ToString("N"), Position = new Rect(position, new Vector2(260, 170)) };
            document.Notes.Add(note); LayoutChanged(); Open(document);
            graph.AddToSelection(graph.FindLayoutElement(note.Id)); RefreshInspector();
        }

        private void AddGroup(Vector2 position)
        {
            if (document == null) return;
            var members = graph.selection.OfType<GraphElement>()
                .Where(item => item is SocialNodeView || item is SocialNoteView).Select(item => item.viewDataKey).ToList();
            Undo.RecordObject(document, "Group graph selection");
            foreach (var id in members) RemoveLayoutMembership(id);
            var group = new GraphGroup { Id = Guid.NewGuid().ToString("N"),
                Position = new Rect(position, new Vector2(300, 200)), MemberIds = members };
            document.Groups.Add(group); LayoutChanged(); Open(document);
            graph.AddToSelection(graph.FindLayoutElement(group.Id)); RefreshInspector();
        }

        private void UngroupSelection()
        {
            if (document == null) return;
            var ids = graph.selection.OfType<GraphElement>().Where(item => item is SocialNodeView || item is SocialNoteView)
                .Select(item => item.viewDataKey).ToList();
            if (!document.Groups.Any(group => group?.MemberIds != null && group.MemberIds.Any(ids.Contains))) return;
            Undo.RecordObject(document, "Ungroup graph selection");
            foreach (var id in ids) RemoveLayoutMembership(id);
            LayoutChanged(); Open(document);
        }

        private void RemoveLayoutMembership(string id)
        {
            foreach (var group in document.Groups)
                group?.MemberIds?.RemoveAll(item => item == id);
        }

        private void ChangeGroupMembership(Group group, IEnumerable<GraphElement> elements, bool added)
        {
            if (rebuilding || document == null || !(group is SocialGroupView view) || !document.Groups.Contains(view.Model)) return;
            var ids = elements.Where(item => item is SocialNodeView || item is SocialNoteView)
                .Select(item => item.viewDataKey).ToList();
            if (view.Model.MemberIds == null) view.Model.MemberIds = new List<string>();
            if (!ids.Any(id => view.Model.MemberIds.Contains(id) != added)) return;
            Undo.RecordObject(document, added ? "Add graph group members" : "Remove graph group members");
            foreach (var id in ids)
            {
                if (added) { RemoveLayoutMembership(id); view.Model.MemberIds.Add(id); }
                else view.Model.MemberIds.Remove(id);
            }
            LayoutChanged();
        }

        private bool RemoveLayoutElement(GraphElement element, Action record)
        {
            if (element is SocialGroupView group)
            {
                record(); document.Groups.Remove(group.Model); LayoutChanged(); return true;
            }
            if (element is SocialNoteView note)
            {
                record(); document.Notes.Remove(note.Model); RemoveLayoutMembership(note.Model.Id); LayoutChanged(); return true;
            }
            return false;
        }

        private void SaveMovedLayout(IEnumerable<GraphElement> elements, Action record)
        {
            if (rebuilding || document == null) return;
            // Dragging a group moves its members too. Persist their actual canvas coordinates together.
            var moved = elements.ToList();
            if (moved.Any(item => item is SocialGroupView))
                moved = graph.graphElements.Where(item => item is SocialNodeView || item is SocialNoteView || item is SocialGroupView).ToList();
            bool changed = false;
            foreach (var item in moved)
            {
                var rect = item.GetPosition();
                if (!HasValidLayout(rect)) continue;
                if (item is SocialNodeView node && node.Model.Position != rect.position)
                { record(); node.Model.Position = rect.position; changed = true; }
                else if (item is SocialNoteView note && note.Model.Position != rect)
                { record(); note.Model.Position = rect; changed = true; }
                else if (item is SocialGroupView group && group.Model.Position != rect)
                { record(); group.Model.Position = rect; changed = true; }
            }
            if (changed) LayoutChanged();
        }

        private void SaveNote(SocialNoteView view)
        {
            if (rebuilding || document == null) return;
            var model = view.Model;
            var displayedRect = view.GetPosition();
            var position = HasValidLayout(displayedRect) ? displayedRect : model.Position;
            if (model.Title == view.title && model.Contents == view.contents && model.Theme == (int)view.theme &&
                model.FontSize == (int)view.fontSize && model.Position == position) return;
            Undo.RecordObject(document, "Edit graph note");
            model.Title = view.title; model.Contents = view.contents; model.Theme = (int)view.theme;
            model.FontSize = (int)view.fontSize; model.Position = position; LayoutChanged();
        }

        private static bool HasValidLayout(Rect rect)
        {
            return Finite(rect.x) && Finite(rect.y) && Finite(rect.width) && Finite(rect.height) &&
                rect.width > 0 && rect.height > 0;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private bool RefreshLayoutInspector()
        {
            if (document == null) return false;
            var group = graph.selection.OfType<SocialGroupView>().FirstOrDefault();
            var note = graph.selection.OfType<SocialNoteView>().FirstOrDefault();
            if (group != null)
            {
                inspector.Add(new Label("分组 · " + group.Model.MemberIds.Count + " 个成员"));
                var title = new TextField("标题") { value = group.Model.Title, name = "groupTitle", maxLength = 256 };
                title.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(document, "Rename graph group");
                    group.Model.Title = evt.newValue;
                    rebuilding = true; group.title = evt.newValue; rebuilding = false; LayoutChanged();
                });
                inspector.Add(title);
                inspector.Add(new Label("拖动标题移动全部成员。删除分组保留节点与注释。可将节点拖入分组，或用右键移出。"));
                return true;
            }
            if (note == null) return false;
            inspector.Add(new Label("画布注释（不参与执行）"));
            var noteTitle = new TextField("标题") { value = note.Model.Title, name = "noteTitle" };
            noteTitle.RegisterValueChangedCallback(evt => { note.title = evt.newValue; SaveNote(note); });
            inspector.Add(noteTitle);
            var contents = new TextField("正文") { value = note.Model.Contents, multiline = true, name = "noteContents" };
            contents.RegisterValueChangedCallback(evt => { note.contents = evt.newValue; SaveNote(note); });
            inspector.Add(contents);
            inspector.Add(new Label("可拖动画布注释，或拖动右下角调整大小；右键切换主题和字号。"));
            return true;
        }
    }
}
